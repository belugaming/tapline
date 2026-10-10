import Accelerate
import Foundation

/// Exponential sine sweep measurement (Farina): play `signal`, record the result,
/// deconvolve with `inverse` to get the impulse response of speakers + room.
public struct SineSweep: Sendable {
    public let sampleRate: Double
    public let duration: Double
    public let startFrequency: Double
    public let endFrequency: Double
    public let signal: [Float]
    /// Time-reversed sweep with a -6 dB/octave envelope, scaled so that
    /// `signal ⊛ inverse` has unity gain (0 dB at 1 kHz).
    public let inverse: [Float]

    public init(sampleRate: Double, duration: Double = 8, startFrequency: Double = 20, endFrequency: Double = 20_000, amplitude: Float = 0.25) {
        self.sampleRate = sampleRate
        self.duration = duration
        self.startFrequency = startFrequency
        let f2 = min(endFrequency, sampleRate * 0.45)
        self.endFrequency = f2
        let n = Int(duration * sampleRate)
        let rate = log(f2 / startFrequency)
        let k = 2 * Double.pi * startFrequency * duration / rate

        var sweep = [Float](repeating: 0, count: n)
        for i in 0..<n {
            let t = Double(i) / sampleRate
            sweep[i] = Float(sin(k * (exp(t / duration * rate) - 1)))
        }
        // 20 ms raised-cosine fades so the start and end do not click.
        let fade = min(n / 4, Int(0.02 * sampleRate))
        for i in 0..<fade {
            let g = Float(0.5 - 0.5 * cos(Double.pi * Double(i) / Double(fade)))
            sweep[i] *= g
            sweep[n - 1 - i] *= g
        }

        var inverse = [Float](repeating: 0, count: n)
        for i in 0..<n {
            let t = Double(i) / sampleRate
            inverse[i] = sweep[n - 1 - i] * Float(exp(-t / duration * rate))
        }
        // Normalise in the frequency domain: sweep ⊛ inverse reads 0 dB at 1 kHz.
        let identity = Self.convolve(sweep, inverse)
        let level = RoomResponse.frequencyResponse(impulse: identity, sampleRate: sampleRate, grid: [1000])?.levels.first ?? 0
        var scale = Float(pow(10, -level / 20))
        vDSP_vsmul(inverse, 1, &scale, &inverse, 1, vDSP_Length(n))

        var a = amplitude
        vDSP_vsmul(sweep, 1, &a, &sweep, 1, vDSP_Length(n))
        self.signal = sweep
        self.inverse = inverse.map { $0 / amplitude }
    }

    /// Impulse response of whatever the sweep went through.
    public func impulseResponse(from recording: [Float]) -> [Float] {
        Self.convolve(recording, inverse)
    }

    /// Linear convolution via FFT.
    public static func convolve(_ a: [Float], _ b: [Float]) -> [Float] {
        let outCount = a.count + b.count - 1
        guard outCount > 0 else { return [] }
        let log2n = vDSP_Length(ceil(log2(Double(outCount))))
        let n = 1 << Int(log2n)
        guard let setup = vDSP_create_fftsetup(log2n, FFTRadix(kFFTRadix2)) else { return [] }
        defer { vDSP_destroy_fftsetup(setup) }

        func forward(_ x: [Float]) -> (re: [Float], im: [Float]) {
            var padded = x + [Float](repeating: 0, count: n - x.count)
            var re = [Float](repeating: 0, count: n / 2)
            var im = [Float](repeating: 0, count: n / 2)
            re.withUnsafeMutableBufferPointer { rp in
                im.withUnsafeMutableBufferPointer { ip in
                    var split = DSPSplitComplex(realp: rp.baseAddress!, imagp: ip.baseAddress!)
                    padded.withUnsafeMutableBufferPointer { pp in
                        pp.baseAddress!.withMemoryRebound(to: DSPComplex.self, capacity: n / 2) {
                            vDSP_ctoz($0, 2, &split, 1, vDSP_Length(n / 2))
                        }
                    }
                    vDSP_fft_zrip(setup, &split, 1, log2n, FFTDirection(FFT_FORWARD))
                }
            }
            return (re, im)
        }

        var (ar, ai) = forward(a)
        var (br, bi) = forward(b)
        // Packed format: element 0 holds DC (real) and Nyquist (imag) separately.
        let dc = ar[0] * br[0], nyquist = ai[0] * bi[0]
        var cr = [Float](repeating: 0, count: n / 2)
        var ci = [Float](repeating: 0, count: n / 2)
        ar.withUnsafeMutableBufferPointer { arp in
            ai.withUnsafeMutableBufferPointer { aip in
                br.withUnsafeMutableBufferPointer { brp in
                    bi.withUnsafeMutableBufferPointer { bip in
                        cr.withUnsafeMutableBufferPointer { crp in
                            ci.withUnsafeMutableBufferPointer { cip in
                                var sa = DSPSplitComplex(realp: arp.baseAddress!, imagp: aip.baseAddress!)
                                var sb = DSPSplitComplex(realp: brp.baseAddress!, imagp: bip.baseAddress!)
                                var sc = DSPSplitComplex(realp: crp.baseAddress!, imagp: cip.baseAddress!)
                                vDSP_zvmul(&sa, 1, &sb, 1, &sc, 1, vDSP_Length(n / 2), 1)
                                sc.realp[0] = dc
                                sc.imagp[0] = nyquist
                                vDSP_fft_zrip(setup, &sc, 1, log2n, FFTDirection(FFT_INVERSE))
                            }
                        }
                    }
                }
            }
        }
        var out = [Float](repeating: 0, count: n)
        cr.withUnsafeMutableBufferPointer { crp in
            ci.withUnsafeMutableBufferPointer { cip in
                var sc = DSPSplitComplex(realp: crp.baseAddress!, imagp: cip.baseAddress!)
                out.withUnsafeMutableBufferPointer { op in
                    op.baseAddress!.withMemoryRebound(to: DSPComplex.self, capacity: n / 2) {
                        vDSP_ztoc(&sc, 1, $0, 2, vDSP_Length(n / 2))
                    }
                }
            }
        }
        // zrip forward scales by 2 each, inverse by n: undo both.
        var scale = Float(1.0 / Double(4 * n))
        vDSP_vsmul(out, 1, &scale, &out, 1, vDSP_Length(n))
        return Array(out.prefix(outCount))
    }
}

public enum RoomResponse {
    /// Magnitude response of an impulse response, windowed around its main peak.
    /// A long window (e.g. 500 ms) resolves room modes down to a few hertz.
    public static func frequencyResponse(impulse ir: [Float], sampleRate: Double, windowSeconds: Double = 0.5,
                                         grid: [Double] = EQFitter.grid) -> FrequencyResponse? {
        guard let peakIndex = ir.indices.max(by: { abs(ir[$0]) < abs(ir[$1]) }) else { return nil }
        let pre = Int(0.002 * sampleRate)
        let start = max(0, peakIndex - pre)
        let length = min(ir.count - start, Int(windowSeconds * sampleRate))
        guard length > 64 else { return nil }
        var segment = Array(ir[start..<(start + length)])
        // Fade in over the pre-delay and fade out over the last quarter.
        let fadeIn = max(1, peakIndex - start)
        for i in 0..<fadeIn { segment[i] *= Float(0.5 - 0.5 * cos(Double.pi * Double(i) / Double(fadeIn))) }
        let fadeOut = length / 4
        for i in 0..<fadeOut {
            segment[length - 1 - i] *= Float(0.5 - 0.5 * cos(Double.pi * Double(i) / Double(fadeOut)))
        }

        let log2n = vDSP_Length(max(16, Int(ceil(log2(Double(length))))))
        let n = 1 << Int(log2n)
        guard let setup = vDSP_create_fftsetup(log2n, FFTRadix(kFFTRadix2)) else { return nil }
        defer { vDSP_destroy_fftsetup(setup) }
        var padded = segment + [Float](repeating: 0, count: n - length)
        var re = [Float](repeating: 0, count: n / 2)
        var im = [Float](repeating: 0, count: n / 2)
        var mags = [Float](repeating: 0, count: n / 2)
        re.withUnsafeMutableBufferPointer { rp in
            im.withUnsafeMutableBufferPointer { ip in
                var split = DSPSplitComplex(realp: rp.baseAddress!, imagp: ip.baseAddress!)
                padded.withUnsafeMutableBufferPointer { pp in
                    pp.baseAddress!.withMemoryRebound(to: DSPComplex.self, capacity: n / 2) {
                        vDSP_ctoz($0, 2, &split, 1, vDSP_Length(n / 2))
                    }
                }
                vDSP_fft_zrip(setup, &split, 1, log2n, FFTDirection(FFT_FORWARD))
                split.imagp[0] = 0 // drop the packed Nyquist term
                vDSP_zvmags(&split, 1, &mags, 1, vDSP_Length(n / 2))
            }
        }
        let binHz = sampleRate / Double(n)
        // Power average over the bins each grid point covers (1/48 octave or one bin).
        let levels: [Double] = grid.map { f in
            let lo = max(1, Int(f * pow(2, -1.0 / 96) / binHz))
            let hi = min(n / 2 - 1, max(lo, Int(f * pow(2, 1.0 / 96) / binHz)))
            var sum = 0.0
            for b in lo...hi { sum += Double(mags[b]) }
            let power = sum / Double(hi - lo + 1) / 4 // zrip scales by 2
            return 10 * log10(max(power, 1e-20))
        }
        return FrequencyResponse(frequencies: grid, levels: levels)
    }

    /// Power average of several measurements (e.g. a few listening positions).
    public static func average(_ responses: [FrequencyResponse]) -> FrequencyResponse? {
        guard let first = responses.first else { return nil }
        let grid = first.frequencies
        let mean = grid.indices.map { i -> Double in
            let p = responses.reduce(0.0) { $0 + pow(10, $1.level(at: grid[i]) / 10) } / Double(responses.count)
            return 10 * log10(max(p, 1e-20))
        }
        return FrequencyResponse(frequencies: grid, levels: mean)
    }

    /// Subtracts a microphone calibration curve (e.g. a miniDSP UMIK-1 file).
    public static func applyCalibration(_ response: FrequencyResponse, calibration: FrequencyResponse) -> FrequencyResponse {
        FrequencyResponse(frequencies: response.frequencies,
                          levels: zip(response.frequencies, response.levels).map { $1 - calibration.level(at: $0) })
    }

    /// The speakers' low-frequency limit: the lowest frequency from which the
    /// third-octave smoothed response stays within 6 dB of the midrange for at
    /// least half an octave. Below it, boosting only wastes amplifier power.
    public static func lowFrequencyLimit(_ response: FrequencyResponse) -> Double {
        let g = EQFitter.grid
        let smooth = Smoothing.fractionalOctave(response.resampled(to: g), grid: g) { _ in 1.0 / 3 }
        let mids = g.indices.filter { g[$0] >= 200 && g[$0] <= 2000 }.map { smooth[$0] }.sorted()
        guard !mids.isEmpty else { return 20 }
        let floor = mids[mids.count / 2] - 6
        for i in g.indices where g[i] < 200 {
            let ahead = g.indices.filter { g[$0] >= g[i] && g[$0] <= g[i] * 1.41 }
            if ahead.allSatisfy({ smooth[$0] >= floor }) { return g[i] }
        }
        return 200
    }

    /// A straight target: 0 dB, optionally tilted (dB per octave around 1 kHz).
    public static func flatTarget(tiltDBPerOctave: Double = 0) -> FrequencyResponse {
        let grid = EQFitter.grid
        return FrequencyResponse(frequencies: grid, levels: grid.map { tiltDBPerOctave * log2($0 / 1000) })
    }
}

/// A stereo measurement in one recording: a full sweep on the left speaker,
/// one on the right, then short chirps left, right, left. The chirps give the
/// difference in arrival time between the speakers; using the midpoint of the
/// two left chirps cancels the clock drift between two separate audio
/// interfaces (one playing, one recording).
public struct StereoSweepPlan: Sendable {
    public let sampleRate: Double
    public let sweep: SineSweep
    public let chirp: SineSweep

    public static let preRoll = 0.6
    public static let gap = 1.5
    public static let chirpSeconds = 0.25
    public static let chirpSpacing = 0.5
    public static let tail = 0.8

    public init(sampleRate: Double, sweepSeconds: Double = 8) {
        self.sampleRate = sampleRate
        sweep = SineSweep(sampleRate: sampleRate, duration: sweepSeconds)
        // Above the subwoofer, where the direct sound gives a sharp peak.
        chirp = SineSweep(sampleRate: sampleRate, duration: Self.chirpSeconds, startFrequency: 500, endFrequency: 16_000)
    }

    // Start times in seconds.
    public var leftStart: Double { Self.preRoll }
    public var rightStart: Double { leftStart + sweep.duration + Self.gap }
    public var chirpStarts: [Double] {
        let first = rightStart + sweep.duration + Self.gap
        return (0..<3).map { first + Double($0) * Self.chirpSpacing }
    }
    public var duration: Double { chirpStarts[2] + Self.chirpSeconds + Self.tail }

    /// Left and right output signals.
    public func render() -> (left: [Float], right: [Float]) {
        let count = Int((duration * sampleRate).rounded())
        var left = [Float](repeating: 0, count: count)
        var right = [Float](repeating: 0, count: count)
        func place(_ signal: [Float], at seconds: Double, into buffer: inout [Float]) {
            let start = Int((seconds * sampleRate).rounded())
            for (i, v) in signal.enumerated() where start + i < count { buffer[start + i] = v }
        }
        place(sweep.signal, at: leftStart, into: &left)
        place(sweep.signal, at: rightStart, into: &right)
        place(chirp.signal, at: chirpStarts[0], into: &left)
        place(chirp.signal, at: chirpStarts[1], into: &right)
        place(chirp.signal, at: chirpStarts[2], into: &left)
        return (left, right)
    }

    public struct Analysis: Sendable {
        public var left: FrequencyResponse
        public var right: FrequencyResponse
        /// Right arrival minus left arrival, seconds (positive: the right speaker is farther).
        public var arrivalDifference: Double?
        /// True when the speakers have opposite polarity, nil when unclear.
        public var oppositePolarity: Bool?
        public var snrLeftDB: Double
        public var snrRightDB: Double
        public var peakDB: Double
    }

    /// Analyses a mono microphone recording at `inputRate` that started before playback.
    public static func analyze(recording: [Float], inputRate: Double, sweepSeconds: Double = 8, windowSeconds: Double = 0.5) -> Analysis? {
        let plan = StereoSweepPlan(sampleRate: inputRate, sweepSeconds: sweepSeconds)
        let fs = inputRate
        let n = plan.sweep.signal.count
        guard recording.count > Int(plan.chirpStarts[0] * fs) else { return nil }

        // Main sweeps: a sweep starting at t peaks at t * fs + n - 1 after deconvolution.
        let ir = plan.sweep.impulseResponse(from: Array(recording.prefix(Int((plan.chirpStarts[0] - 0.2) * fs))))
        let leftExpected = Int(plan.leftStart * fs) + n - 1
        guard let leftPeak = peakIndex(ir, in: (leftExpected - Int(0.05 * fs))..<(leftExpected + Int(1.0 * fs))) else { return nil }
        let latency = leftPeak - leftExpected
        let rightExpected = Int(plan.rightStart * fs) + n - 1 + latency
        guard let rightPeak = peakIndex(ir, in: (rightExpected - Int(0.04 * fs))..<(rightExpected + Int(0.04 * fs))) else { return nil }

        func response(around peak: Int) -> FrequencyResponse? {
            let lo = max(0, peak - Int(0.05 * fs)), hi = min(ir.count, peak + Int((windowSeconds + 0.05) * fs))
            guard hi > lo else { return nil }
            return RoomResponse.frequencyResponse(impulse: Array(ir[lo..<hi]), sampleRate: fs, windowSeconds: windowSeconds)
        }
        guard let left = response(around: leftPeak), let right = response(around: rightPeak) else { return nil }

        // Chirps: arrival times with sub-sample precision.
        var arrival: Double?
        var polarity: Bool?
        let nc = plan.chirp.signal.count
        let chirpBase = Int((plan.chirpStarts[0] - 0.1) * fs)
        if chirpBase < recording.count {
            let segment = Array(recording[chirpBase..<min(recording.count, chirpBase + Int((3 * chirpSpacing + 0.5) * fs) + nc)])
            let cir = plan.chirp.impulseResponse(from: segment)
            let peaks: [(position: Double, sign: Float?)] = plan.chirpStarts.compactMap { start in
                let expected = Int(start * fs) - chirpBase + nc - 1 + latency
                // Wide enough for a subwoofer peak having set the latency, or speakers a metre apart.
                let window = (expected - Int(0.025 * fs))..<(expected + Int(0.025 * fs))
                guard let p = peakIndex(cir, in: window) else { return nil }
                return (interpolatedPeak(cir, at: p), clearSign(cir, at: p, halfWidth: Int(0.0005 * fs)))
            }
            if peaks.count == 3 {
                let mid = (peaks[0].position + peaks[2].position) / 2
                arrival = (peaks[1].position - mid) / fs
                if let a = peaks[0].sign, let b = peaks[1].sign, let c = peaks[2].sign, a == c {
                    polarity = a != b
                }
            }
        }

        func rms(_ lo: Double, _ hi: Double) -> Double {
            let a = max(0, Int(lo * fs) + latency), b = min(recording.count, Int(hi * fs) + latency)
            guard b > a else { return 1e-9 }
            var sum = 0.0
            for i in a..<b { sum += Double(recording[i]) * Double(recording[i]) }
            return sqrt(sum / Double(b - a))
        }
        let noise = rms(0, preRoll * 0.8)
        let snrL = 20 * log10(max(rms(plan.leftStart, plan.leftStart + sweepSeconds), 1e-9) / max(noise, 1e-9))
        let snrR = 20 * log10(max(rms(plan.rightStart, plan.rightStart + sweepSeconds), 1e-9) / max(noise, 1e-9))
        let peak = Double(recording.map(abs).max() ?? 0)

        return Analysis(left: left, right: right, arrivalDifference: arrival, oppositePolarity: polarity,
                        snrLeftDB: snrL, snrRightDB: snrR, peakDB: 20 * log10(max(peak, 1e-9)))
    }

    static func peakIndex(_ x: [Float], in range: Range<Int>) -> Int? {
        let lo = max(0, range.lowerBound), hi = min(x.count, range.upperBound)
        guard hi > lo else { return nil }
        var best = lo
        for i in lo..<hi where abs(x[i]) > abs(x[best]) { best = i }
        return best
    }

    /// Parabolic interpolation of |x| around index `i`.
    static func interpolatedPeak(_ x: [Float], at i: Int) -> Double {
        guard i > 0, i < x.count - 1 else { return Double(i) }
        let a = Double(abs(x[i - 1])), b = Double(abs(x[i])), c = Double(abs(x[i + 1]))
        let d = a - 2 * b + c
        guard abs(d) > 1e-12 else { return Double(i) }
        return Double(i) + 0.5 * (a - c) / d
    }

    /// +1 / -1 when the peak clearly dominates the opposite polarity nearby, else nil.
    static func clearSign(_ x: [Float], at i: Int, halfWidth: Int) -> Float? {
        let lo = max(0, i - halfWidth), hi = min(x.count, i + halfWidth + 1)
        let main = x[i]
        var opposite: Float = 0
        for j in lo..<hi where (x[j] > 0) != (main > 0) { opposite = max(opposite, abs(x[j])) }
        return abs(main) > 1.5 * opposite ? (main > 0 ? 1 : -1) : nil
    }
}
