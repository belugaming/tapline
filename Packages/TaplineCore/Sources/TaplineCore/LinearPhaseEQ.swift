import Accelerate
import Foundation
import TaplineDSP

/// How the EQ handles phase.
public enum FilterPhase: String, Codable, CaseIterable, Sendable, Identifiable {
    /// Biquads, as in an analog EQ: no latency, and each band shifts the phase around it.
    case minimum
    /// A symmetric FIR with the same magnitude, no phase shift: a quarter as long as high
    /// precision, 176 ms late at 48 kHz, exact for typical headphone and speaker corrections.
    case linearLowLatency
    /// The same with a FIR of 1.25 s or more, 704 ms late at 48 kHz, which also resolves
    /// narrow bells at the bottom of the range.
    case linearHighPrecision

    public var id: String { rawValue }

    public var isLinear: Bool { self != .minimum }

    /// The kernel's `tl_params.linear_phase`.
    var kernelMode: Int32 {
        switch self {
        case .minimum: 0
        case .linearLowLatency: TL_LIN_LOW_LATENCY
        case .linearHighPrecision: TL_LIN_HIGH_PRECISION
        }
    }
}

/// The linear-phase EQ: a symmetric FIR with the magnitude of the EQ's biquads (bands, tone
/// controls and preamp, per channel), played by the kernel's partitioned convolution.
///
/// Design: the magnitude of the biquad cascade is sampled at 4 x taps points, transformed into
/// its zero-phase impulse response, cut to `taps` samples around the centre with a Tukey window
/// (flat over the middle half, cosine tapers at the ends) and delayed by taps / 2. The Tukey
/// window keeps more of a bass band's ringing than a Hann or Blackman window, which miss the
/// bottom octave by up to twice as much, without leaking into the rest of the curve.
///
/// A narrow bass bell rings for long, and the FIR's length is what resolves it; the latency is
/// half the FIR plus one partition. Two lengths (see LinearPhaseEQTests for the errors):
/// - High precision, 1.25 s or more (65,536 taps at 44.1 and 48 kHz, 704 ms late at 48 kHz),
///   keeps a one-octave bell at 20 Hz within 0.1 dB of the biquads and bells up to Q 10 and
///   ±12 dB from 40 Hz within 0.15 dB. Only the narrowest bells at the very bottom lose some
///   depth: Q 18 at 20 Hz is about 1 dB short at ±6 dB.
/// - Low latency, a quarter of that (16,384 taps, 176 ms late at 48 kHz), the shortest length
///   that keeps typical corrections exact: shelves, high-passes and AutoEQ-style curves within
///   0.01 dB, bells up to Q 2 from 20 Hz and Q 4 from 45 Hz (±6 dB) within 0.2 dB. Q 4 at
///   30 Hz is 0.4 dB short, Q 18 at 20 Hz about 4 dB.
public enum LinearPhaseEQ {
    /// Length of the FIR of `phase` at `sampleRate`, a power of two (see tl_linear_phase_taps);
    /// 0 for minimum phase.
    public static func taps(sampleRate: Double, phase: FilterPhase) -> Int {
        phase.isLinear ? Int(tl_linear_phase_taps(sampleRate, phase.kernelMode)) : 0
    }

    /// Samples of delay `phase` adds: the FIR's own (taps / 2) plus one partition of the
    /// convolution; 0 for minimum phase.
    public static func latency(sampleRate: Double, phase: FilterPhase) -> Int {
        phase.isLinear ? Int(tl_linear_phase_latency(sampleRate, phase.kernelMode)) : 0
    }

    /// The same in milliseconds, as the settings show it.
    public static func latencyMilliseconds(sampleRate: Double, phase: FilterPhase) -> Double {
        let rate = sampleRate >= 8000 && sampleRate.isFinite ? sampleRate : 48_000
        return Double(latency(sampleRate: rate, phase: phase)) / rate * 1000
    }

    /// Share of the FIR, at each end, that the window tapers.
    static let taper = 0.5
    /// Magnitude samples per FIR tap: the zero-phase response is computed over 4 x taps
    /// samples, so the ringing it folds back onto the FIR lies far beyond its ends.
    static let oversampling = 4

    /// What a FIR is made from: its length, the biquads that play on each channel, and the preamp.
    public struct Spec: Hashable, Sendable {
        public var sampleRate: Double
        /// A linear phase: which length.
        public var phase: FilterPhase
        /// Linear preamp.
        public var gain: Double
        public var left: [BiquadCoefficients]
        public var right: [BiquadCoefficients]

        public var taps: Int { LinearPhaseEQ.taps(sampleRate: sampleRate, phase: phase) }

        public init(sampleRate: Double, phase: FilterPhase, gain: Double, left: [BiquadCoefficients], right: [BiquadCoefficients]) {
            self.sampleRate = sampleRate
            self.phase = phase.isLinear ? phase : .linearLowLatency
            self.gain = gain
            self.left = left
            self.right = right
        }

        /// The EQ of `settings` as the kernel's stages play it, whether or not it is bypassed,
        /// at the length of `phase` (by default the settings' own; low latency for minimum phase).
        public init(settings: DSPSettings, sampleRate: Double, phase: FilterPhase? = nil) {
            let rate = sampleRate >= 8000 && sampleRate.isFinite ? sampleRate : 48_000
            self.init(sampleRate: rate, phase: phase ?? settings.filterPhase,
                      gain: dbToLinear(settings.effectivePreampDB(sampleRate: rate)),
                      left: settings.playedStages(channel: .left, sampleRate: rate),
                      right: settings.playedStages(channel: .right, sampleRate: rate))
        }
    }

    /// Magnitude of `stages` times `gain` at the bin frequencies k * sampleRate / count,
    /// k = 0 ... count / 2.
    static func magnitude(_ stages: [BiquadCoefficients], gain: Double, count: Int) -> [Double] {
        let bins = count / 2 + 1
        var cw = [Double](repeating: 0, count: bins)
        for k in 0..<bins { cw[k] = cos(2 * Double.pi * Double(k) / Double(count)) }
        let c2w = cw.map { 2 * $0 * $0 - 1 }
        var power = [Double](repeating: gain * gain, count: bins)
        for s in stages {
            let n0 = s.b0 * s.b0 + s.b1 * s.b1 + s.b2 * s.b2, n1 = 2 * (s.b0 * s.b1 + s.b1 * s.b2), n2 = 2 * s.b0 * s.b2
            let d0 = 1 + s.a1 * s.a1 + s.a2 * s.a2, d1 = 2 * (s.a1 + s.a1 * s.a2), d2 = 2 * s.a2
            for k in 0..<bins {
                let num = n0 + n1 * cw[k] + n2 * c2w[k]
                let den = d0 + d1 * cw[k] + d2 * c2w[k]
                power[k] *= max(num, 0) / max(den, 1e-30)
            }
        }
        return power.map { $0.squareRoot() }
    }

    /// The FIR of one channel: `taps` samples (a power of two), symmetric about taps / 2
    /// (exactly: both halves are the same numbers), tap 0 zero.
    public static func design(_ stages: [BiquadCoefficients], gain: Double, taps: Int) -> [Float] {
        let half = taps / 2
        let count = taps * oversampling
        let log2n = vDSP_Length(count.trailingZeroBitCount)
        let target = magnitude(stages, gain: gain, count: count)

        // Zero-phase impulse response: the inverse transform of the real, even spectrum.
        var re = [Double](repeating: 0, count: count / 2)
        var im = [Double](repeating: 0, count: count / 2)
        for k in 0..<(count / 2) { re[k] = target[k] }
        im[0] = target[count / 2]   // Nyquist, in vDSP's packed layout
        var response = [Double](repeating: 0, count: count)
        guard let setup = vDSP_create_fftsetupD(log2n, FFTRadix(kFFTRadix2)) else { return [Float](repeating: 0, count: taps) }
        defer { vDSP_destroy_fftsetupD(setup) }
        re.withUnsafeMutableBufferPointer { r in
            im.withUnsafeMutableBufferPointer { i in
                var split = DSPDoubleSplitComplex(realp: r.baseAddress!, imagp: i.baseAddress!)
                vDSP_fft_zripD(setup, &split, 1, log2n, FFTDirection(kFFTDirection_Inverse))
                response.withUnsafeMutableBufferPointer { t in
                    t.baseAddress!.withMemoryRebound(to: DSPDoubleComplex.self, capacity: count / 2) {
                        vDSP_ztocD(&split, 1, $0, 2, vDSP_Length(count / 2))
                    }
                }
            }
        }

        var fir = [Float](repeating: 0, count: taps)
        let flat = Double(half) * (1 - taper)
        for n in 0..<half {
            let x = Double(n)
            let w = x <= flat ? 1 : 0.5 + 0.5 * cos(Double.pi * (x - flat) / (Double(half) - flat))
            let v = Float(response[n] / Double(count) * w)
            fir[half + n] = v
            fir[half - n] = v
        }
        fir[0] = 0
        return fir
    }

    /// Both channels' FIRs, ready for the kernel. Takes a few tens of milliseconds: call it off
    /// the main thread. Nil when the kernel's convolution cannot take them.
    public static func prepare(_ spec: Spec) -> PreparedFIR? {
        let left = design(spec.left, gain: spec.gain, taps: spec.taps)
        let right = spec.right == spec.left ? nil : design(spec.right, gain: spec.gain, taps: spec.taps)
        let set: OpaquePointer? = left.withUnsafeBufferPointer { l in
            if let right {
                return right.withUnsafeBufferPointer { r in
                    tl_fir_set_create(l.baseAddress!, r.baseAddress!, UInt32(l.count), spec.sampleRate)
                }
            }
            return tl_fir_set_create(l.baseAddress!, nil, UInt32(l.count), spec.sampleRate)
        }
        return set.map { PreparedFIR(set: $0, spec: spec) }
    }
}

/// The linear-phase EQ's FIRs, transformed for the kernel. Owns them until they are handed
/// to a kernel (`DSPKernel.setLinearPhase`).
public final class PreparedFIR: @unchecked Sendable {
    public let spec: LinearPhaseEQ.Spec
    private var set: OpaquePointer?

    init(set: OpaquePointer, spec: LinearPhaseEQ.Spec) {
        self.set = set
        self.spec = spec
    }

    deinit {
        if let set { tl_ir_set_destroy(set) }
    }

    /// Gives up ownership of the kernel set.
    func take() -> OpaquePointer? {
        defer { set = nil }
        return set
    }
}

extension DSPSettings {
    /// The biquads that play on `channel` (`.left` or `.right`), in the kernel's order: the
    /// bands, clamped as the kernel gets them, then the tone controls.
    public func playedStages(channel: EQChannels, sampleRate: Double) -> [BiquadCoefficients] {
        let parametric = bands.prefix(Self.maxBands)
            .filter { $0.isEnabled && $0.channels.includes(channel) }
            .map { BiquadCoefficients(band: $0.clamped(), sampleRate: sampleRate) }
        return parametric + tone.bands.filter(\.isEnabled).map { BiquadCoefficients(band: $0, sampleRate: sampleRate) }
    }
}
