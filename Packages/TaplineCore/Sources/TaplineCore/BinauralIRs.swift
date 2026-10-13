import Accelerate
import Foundation
import TaplineDSP

/// Impulse responses of a pair of speakers in front of the listener, as they arrive at the
/// two ears: what Virtual Speakers convolves the stereo signal with.
public struct BinauralIRs: Equatable, Sendable {
    /// Where the four responses came from in the imported file(s).
    public enum Layout: String, Codable, Sendable {
        /// HeSuVi's 14-channel layout, also Impulcifer's hesuvi.wav.
        case hesuvi14
        /// HeSuVi's 7-channel layout: the left speakers only, mirrored for the right ones.
        case hesuvi7
        /// Impulcifer's hrir.wav (16 channels).
        case impulcifer16
        /// FL→left, FL→right, FR→left, FR→right.
        case fourChannel
        /// One stereo file per speaker.
        case stereoPair
        /// One stereo file of one speaker, mirrored for the other.
        case mirroredStereo
    }

    public enum ImportError: Error, Equatable {
        /// Not a layout with a front left and front right speaker.
        case unsupportedChannelCount(Int)
        /// The front speakers' responses are silent.
        case silent
        /// Two stereo files of the same side.
        case sameSide
        case sampleRateMismatch
        case wrongFileCount(Int)
    }

    /// Index of each response in `responses`.
    public static let frontLeftToLeft = 0, frontLeftToRight = 1, frontRightToLeft = 2, frontRightToRight = 3

    public var sampleRate: Double
    /// FL→left ear, FL→right ear, FR→left ear, FR→right ear, all the same length.
    public private(set) var responses: [[Float]]
    public var layout: Layout

    public var length: Int { responses.first?.count ?? 0 }
    public var duration: Double { Double(length) / sampleRate }

    public init(sampleRate: Double, responses: [[Float]], layout: Layout = .fourChannel) {
        precondition(responses.count == 4, "four impulse responses")
        let n = responses.map(\.count).max() ?? 0
        self.sampleRate = sampleRate
        self.responses = responses.map { $0 + [Float](repeating: 0, count: n - $0.count) }
        self.layout = layout
    }

    /// The front speaker pair of one WAV file: HeSuVi (14 or 7 channels), Impulcifer's
    /// hrir.wav (16), four channels in FL→L, FL→R, FR→L, FR→R order, or a stereo file of
    /// one speaker, which is mirrored for the other.
    public init(wav: WAVFile) throws {
        let c = wav.channels
        switch c.count {
        case 14:
            // HeSuVi: FL-L FL-R SL-L SL-R BL-L BL-R C-L FR-R FR-L SR-R SR-L BR-R BR-L C-R
            // (right-side speakers list the right ear first).
            self.init(sampleRate: wav.sampleRate, responses: [c[0], c[1], c[8], c[7]], layout: .hesuvi14)
        case 7:
            // The first seven of the above; the right speakers mirror the left ones.
            self.init(sampleRate: wav.sampleRate, responses: [c[0], c[1], c[1], c[0]], layout: .hesuvi7)
        case 16:
            // Impulcifer's hrir.wav: FL-L FL-R FR-L FR-R C-L C-R LFE-L LFE-R BL-L ...
            self.init(sampleRate: wav.sampleRate, responses: Array(c[0..<4]), layout: .impulcifer16)
        case 4:
            self.init(sampleRate: wav.sampleRate, responses: c, layout: .fourChannel)
        case 2:
            let pair = Self.speaker(c)
            self.init(sampleRate: wav.sampleRate, responses: [pair.left[0], pair.left[1], pair.left[1], pair.left[0]], layout: .mirroredStereo)
        default:
            throw ImportError.unsupportedChannelCount(c.count)
        }
        try validate()
    }

    /// Two stereo files, one per speaker, in either order: the louder ear tells which is which.
    public init(stereoPair a: WAVFile, _ b: WAVFile) throws {
        guard a.channels.count == 2 else { throw ImportError.unsupportedChannelCount(a.channels.count) }
        guard b.channels.count == 2 else { throw ImportError.unsupportedChannelCount(b.channels.count) }
        guard a.sampleRate == b.sampleRate else { throw ImportError.sampleRateMismatch }
        let first = Self.speaker(a.channels), second = Self.speaker(b.channels)
        guard first.isLeft != second.isLeft else { throw ImportError.sameSide }
        let left = first.isLeft ? a.channels : b.channels
        let right = first.isLeft ? b.channels : a.channels
        self.init(sampleRate: a.sampleRate, responses: [left[0], left[1], right[0], right[1]], layout: .stereoPair)
        try validate()
    }

    /// One file (any layout above) or two stereo files.
    public init(wavFiles: [WAVFile]) throws {
        switch wavFiles.count {
        case 1: try self.init(wav: wavFiles[0])
        case 2: try self.init(stereoPair: wavFiles[0], wavFiles[1])
        default: throw ImportError.wrongFileCount(wavFiles.count)
        }
    }

    /// A stereo response of one speaker: the ear on its side is the louder one. Returns the
    /// responses as a left speaker would have them (mirrored when it is a right speaker).
    private static func speaker(_ c: [[Float]]) -> (isLeft: Bool, left: [[Float]]) {
        let isLeft = energy(c[0]) >= energy(c[1])
        return (isLeft, isLeft ? c : [c[1], c[0]])
    }

    private func validate() throws {
        let peak = responses.map { $0.map(abs).max() ?? 0 }
        guard peak[Self.frontLeftToLeft] > 1e-6, peak[Self.frontRightToRight] > 1e-6 else { throw ImportError.silent }
    }

    /// The four responses as a 4-channel WAV, the layout Tapline stores imported sets in.
    public var wav: WAVFile { WAVFile(sampleRate: sampleRate, channels: responses) }

    // MARK: - Preparing for the kernel

    /// Drops the silence before the first and after the last sound (keeping the delays between
    /// the four), then cuts what is left to `maxLength` samples with a short fade-out.
    public func trimmed(maxLength: Int = Int(TL_CONV_MAX_TAPS)) -> BinauralIRs {
        let peak = responses.map { $0.map(abs).max() ?? 0 }.max() ?? 0
        guard peak > 0, length > 0 else { return self }
        // Before the onset (-60 dB) keep 1 ms; after the end keep everything above -100 dB.
        let onsets = responses.compactMap { $0.firstIndex { abs($0) > peak * 1e-3 } }
        let ends = responses.compactMap { $0.lastIndex { abs($0) > peak * 1e-5 } }
        let start = max(0, (onsets.min() ?? 0) - Int(sampleRate / 1000))
        var end = (ends.max() ?? (length - 1)) + 1
        var cut = false
        if end - start > maxLength {
            end = start + maxLength
            cut = true
        }
        var out = responses.map { Array($0[start..<end]) }
        if cut {
            let fade = min(Int(0.005 * sampleRate), maxLength / 8)
            for c in out.indices {
                for i in 0..<fade {
                    out[c][end - start - fade + i] *= Float(0.5 + 0.5 * cos(Double.pi * Double(i + 1) / Double(fade)))
                }
            }
        }
        var result = self
        result.responses = out
        return result
    }

    /// Band-limited resampling (Kaiser-windowed sinc). Scales by the rate ratio so the
    /// filters keep their gain. Stops after `maxLength` output samples.
    public func resampled(to rate: Double, maxLength: Int = Int(TL_CONV_MAX_TAPS)) -> BinauralIRs {
        guard rate > 0, sampleRate > 0, abs(rate - sampleRate) > 1e-9 else { return self }
        let ratio = rate / sampleRate
        let outCount = min(maxLength, Int((Double(length) * ratio).rounded(.up)))
        let resampler = Resampler(ratio: ratio)
        var result = self
        result.sampleRate = rate
        result.responses = responses.map { resampler.run($0, count: outCount) }
        return result
    }

    /// Gain that keeps the level about the same when the effect is switched on: the
    /// geometric mean of the power gains for a centre (mono) source and for wide,
    /// uncorrelated stereo, with equal weight per octave from 100 Hz to 8 kHz. Centre
    /// sources then play a little louder than without the effect, wide ones a little quieter,
    /// as they do from real speakers.
    public var loudnessGain: Float {
        guard length > 0 else { return 1 }
        let n = max(4096, 1 << Int(ceil(log2(Double(length)))))
        let spectra = responses.map { Self.spectrum($0, size: n) }
        var mono = 0.0, wide = 0.0, weights = 0.0
        for k in 1..<(n / 2) {
            let f = Double(k) * sampleRate / Double(n)
            guard f >= 100, f <= 8000 else { continue }
            let w = 1 / f
            func h(_ i: Int) -> (re: Double, im: Double) { (Double(spectra[i].re[k]), Double(spectra[i].im[k])) }
            let ll = h(Self.frontLeftToLeft), rl = h(Self.frontRightToLeft)
            let lr = h(Self.frontLeftToRight), rr = h(Self.frontRightToRight)
            func power(_ a: (re: Double, im: Double)) -> Double { a.re * a.re + a.im * a.im }
            let leftEar = power((ll.re + rl.re, ll.im + rl.im)), rightEar = power((lr.re + rr.re, lr.im + rr.im))
            mono += w * (leftEar + rightEar) / 2
            wide += w * (power(ll) + power(rl) + power(lr) + power(rr)) / 2
            weights += w
        }
        guard weights > 0, mono > 0, wide > 0 else { return 1 }
        let gain = pow((mono / weights) * (wide / weights), -0.25)
        return gain.isFinite ? Float(gain.clamped(to: 0.05...20)) : 1
    }

    /// True DFT of `x` zero-padded to `size` (a power of two), bins 0 ..< size / 2.
    static func spectrum(_ x: [Float], size: Int) -> (re: [Float], im: [Float]) {
        let log2n = vDSP_Length(log2(Double(size)))
        guard let setup = vDSP_create_fftsetup(log2n, FFTRadix(kFFTRadix2)) else {
            return ([Float](repeating: 0, count: size / 2), [Float](repeating: 0, count: size / 2))
        }
        defer { vDSP_destroy_fftsetup(setup) }
        var padded = Array(x.prefix(size)) + [Float](repeating: 0, count: max(0, size - x.count))
        var re = [Float](repeating: 0, count: size / 2)
        var im = [Float](repeating: 0, count: size / 2)
        re.withUnsafeMutableBufferPointer { rp in
            im.withUnsafeMutableBufferPointer { ip in
                var split = DSPSplitComplex(realp: rp.baseAddress!, imagp: ip.baseAddress!)
                padded.withUnsafeMutableBufferPointer { pp in
                    pp.baseAddress!.withMemoryRebound(to: DSPComplex.self, capacity: size / 2) {
                        vDSP_ctoz($0, 2, &split, 1, vDSP_Length(size / 2))
                    }
                }
                vDSP_fft_zrip(setup, &split, 1, log2n, FFTDirection(FFT_FORWARD))
                // zrip's forward transform is twice the DFT.
                var half: Float = 0.5
                vDSP_vsmul(split.realp, 1, &half, split.realp, 1, vDSP_Length(size / 2))
                vDSP_vsmul(split.imagp, 1, &half, split.imagp, 1, vDSP_Length(size / 2))
            }
        }
        return (re, im)
    }

    static func energy(_ x: [Float]) -> Double {
        x.reduce(0) { $0 + Double($1) * Double($1) }
    }
}

/// Kaiser-windowed sinc interpolation from a tabulated kernel.
private struct Resampler {
    let ratio: Double
    /// Kernel half-width in input samples.
    let halfWidth: Double
    let table: [Double]
    static let stepsPerSample = 512.0

    init(ratio: Double) {
        self.ratio = ratio
        // Cut off at 96% of the lower Nyquist frequency.
        let cutoff = min(1, ratio) * 0.96
        let zeroCrossings = 24.0
        halfWidth = zeroCrossings / cutoff
        let beta = 8.0
        let count = Int(halfWidth * Self.stepsPerSample) + 2
        // An impulse response gets `ratio` times as many samples: scale them down so the
        // filter keeps its gain.
        let scale = 1 / ratio
        table = (0..<count).map { i in
            let t = Double(i) / Self.stepsPerSample
            guard t < halfWidth else { return 0 }
            let x = cutoff * t
            let sinc = x == 0 ? 1 : sin(Double.pi * x) / (Double.pi * x)
            let u = t / halfWidth
            let window = Self.besselI0(beta * (1 - u * u).squareRoot()) / Self.besselI0(beta)
            return cutoff * sinc * window * scale
        }
    }

    func kernel(_ t: Double) -> Double {
        let p = abs(t) * Self.stepsPerSample
        let i = Int(p)
        guard i + 1 < table.count else { return 0 }
        let f = p - Double(i)
        return table[i] + f * (table[i + 1] - table[i])
    }

    func run(_ x: [Float], count: Int) -> [Float] {
        var out = [Float](repeating: 0, count: count)
        for n in 0..<count {
            let t = Double(n) / ratio
            let lo = max(0, Int((t - halfWidth).rounded(.up)))
            let hi = min(x.count - 1, Int((t + halfWidth).rounded(.down)))
            guard lo <= hi else { continue }
            var acc = 0.0
            for k in lo...hi { acc += Double(x[k]) * kernel(t - Double(k)) }
            out[n] = Float(acc)
        }
        return out
    }

    static func besselI0(_ x: Double) -> Double {
        var sum = 1.0, term = 1.0
        let q = x * x / 4
        for k in 1..<64 {
            term *= q / Double(k * k)
            sum += term
            if term < sum * 1e-17 { break }
        }
        return sum
    }
}
