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

    /// A straight target: 0 dB, optionally tilted (dB per octave around 1 kHz).
    public static func flatTarget(tiltDBPerOctave: Double = 0) -> FrequencyResponse {
        let grid = EQFitter.grid
        return FrequencyResponse(frequencies: grid, levels: grid.map { tiltDBPerOctave * log2($0 / 1000) })
    }
}
