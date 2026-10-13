import Foundation
import TaplineDSP

/// Normalized biquad coefficients (a0 == 1), RBJ Audio EQ Cookbook designs.
public struct BiquadCoefficients: Hashable, Sendable {
    public var b0: Double
    public var b1: Double
    public var b2: Double
    public var a1: Double
    public var a2: Double

    public static let identity = BiquadCoefficients(b0: 1, b1: 0, b2: 0, a1: 0, a2: 0)

    public init(b0: Double, b1: Double, b2: Double, a1: Double, a2: Double) {
        self.b0 = b0
        self.b1 = b1
        self.b2 = b2
        self.a1 = a1
        self.a2 = a2
    }

    public init(band: EQBand, sampleRate: Double) {
        self = Self.design(type: band.type, frequency: band.frequency, gainDB: band.gain, q: band.q, sampleRate: sampleRate)
    }

    public static func design(type: FilterType, frequency: Double, gainDB: Double, q: Double, sampleRate: Double) -> BiquadCoefficients {
        let nyquist = sampleRate / 2
        let f0 = min(max(frequency, 1), nyquist * 0.995)
        let q = max(q, 0.01)
        let a = pow(10, gainDB / 40)
        let w0 = 2 * Double.pi * f0 / sampleRate
        let cosW = cos(w0)
        let sinW = sin(w0)
        let alpha = sinW / (2 * q)

        var b0 = 1.0, b1 = 0.0, b2 = 0.0, a0 = 1.0, a1 = 0.0, a2 = 0.0
        switch type {
        case .peak:
            b0 = 1 + alpha * a
            b1 = -2 * cosW
            b2 = 1 - alpha * a
            a0 = 1 + alpha / a
            a1 = -2 * cosW
            a2 = 1 - alpha / a
        case .lowShelf:
            let s = 2 * sqrt(a) * alpha
            b0 = a * ((a + 1) - (a - 1) * cosW + s)
            b1 = 2 * a * ((a - 1) - (a + 1) * cosW)
            b2 = a * ((a + 1) - (a - 1) * cosW - s)
            a0 = (a + 1) + (a - 1) * cosW + s
            a1 = -2 * ((a - 1) + (a + 1) * cosW)
            a2 = (a + 1) + (a - 1) * cosW - s
        case .highShelf:
            let s = 2 * sqrt(a) * alpha
            b0 = a * ((a + 1) + (a - 1) * cosW + s)
            b1 = -2 * a * ((a - 1) + (a + 1) * cosW)
            b2 = a * ((a + 1) + (a - 1) * cosW - s)
            a0 = (a + 1) - (a - 1) * cosW + s
            a1 = 2 * ((a - 1) - (a + 1) * cosW)
            a2 = (a + 1) - (a - 1) * cosW - s
        case .lowPass:
            b0 = (1 - cosW) / 2
            b1 = 1 - cosW
            b2 = (1 - cosW) / 2
            a0 = 1 + alpha
            a1 = -2 * cosW
            a2 = 1 - alpha
        case .highPass:
            b0 = (1 + cosW) / 2
            b1 = -(1 + cosW)
            b2 = (1 + cosW) / 2
            a0 = 1 + alpha
            a1 = -2 * cosW
            a2 = 1 - alpha
        case .notch:
            b0 = 1
            b1 = -2 * cosW
            b2 = 1
            a0 = 1 + alpha
            a1 = -2 * cosW
            a2 = 1 - alpha
        case .bandPass:
            b0 = alpha
            b1 = 0
            b2 = -alpha
            a0 = 1 + alpha
            a1 = -2 * cosW
            a2 = 1 - alpha
        }
        return BiquadCoefficients(b0: b0 / a0, b1: b1 / a0, b2: b2 / a0, a1: a1 / a0, a2: a2 / a0)
    }

    /// Magnitude response |H(e^jw)| at the given frequency.
    public func magnitude(at frequency: Double, sampleRate: Double) -> Double {
        let w = 2 * Double.pi * frequency / sampleRate
        let cw = cos(w), c2w = cos(2 * w)
        let num = b0 * b0 + b1 * b1 + b2 * b2 + 2 * (b0 * b1 + b1 * b2) * cw + 2 * b0 * b2 * c2w
        let den = 1 + a1 * a1 + a2 * a2 + 2 * (a1 + a1 * a2) * cw + 2 * a2 * c2w
        return sqrt(max(num, 0) / max(den, 1e-30))
    }

    public func magnitudeDB(at frequency: Double, sampleRate: Double) -> Double {
        20 * log10(max(magnitude(at: frequency, sampleRate: sampleRate), 1e-9))
    }

    func cValue(channels: EQChannels = .both) -> tl_biquad {
        let mask: Int32 = switch channels { case .both: 0; case .left: 1; case .right: 2 }
        return tl_biquad(b0: b0, b1: b1, b2: b2, a1: a1, a2: a2, active: 1, channels: mask)
    }
}

public enum ResponseCurve {
    /// Log-spaced frequencies between `low` and `high`.
    public static func logFrequencies(count: Int, low: Double = 20, high: Double = 20_000) -> [Double] {
        guard count > 1 else { return [low] }
        let l = log10(low), h = log10(high)
        return (0..<count).map { pow(10, l + (h - l) * Double($0) / Double(count - 1)) }
    }

    /// Summed response in dB of all bands at the given frequencies. With `channel`
    /// (`.left` or `.right`), only the bands that process that channel count.
    public static func responseDB(bands: [EQBand], frequencies: [Double], sampleRate: Double, channel: EQChannels? = nil) -> [Double] {
        let coeffs = bands
            .filter { $0.isEnabled && (channel.map($0.channels.includes) ?? true) }
            .map { BiquadCoefficients(band: $0, sampleRate: sampleRate) }
        return frequencies.map { f in
            coeffs.reduce(0) { $0 + $1.magnitudeDB(at: f, sampleRate: sampleRate) }
        }
    }

    public static func responseDB(band: EQBand, frequencies: [Double], sampleRate: Double) -> [Double] {
        let c = BiquadCoefficients(band: band, sampleRate: sampleRate)
        return frequencies.map { c.magnitudeDB(at: $0, sampleRate: sampleRate) }
    }

    private static let probe = logFrequencies(count: 480)

    /// Largest boost of the summed curve of either channel, used for automatic preamp.
    public static func maxBoostDB(bands: [EQBand], sampleRate: Double) -> Double {
        guard bands.contains(where: \.isEnabled) else { return 0 }
        guard bands.contains(where: { $0.isEnabled && $0.channels != .both }) else {
            return responseDB(bands: bands, frequencies: probe, sampleRate: sampleRate).max() ?? 0
        }
        return [EQChannels.left, .right].map {
            responseDB(bands: bands, frequencies: probe, sampleRate: sampleRate, channel: $0).max() ?? 0
        }.max() ?? 0
    }

    // Pink noise has equal power per octave, so every point of a log-spaced grid carries the
    // same power. Each point is weighted by the K-weighting filter of ITU-R BS.1770 (the
    // loudness of LUFS meters): a high-pass around 40 Hz and +4 dB above 2 kHz.
    private static let loudnessGrid = logFrequencies(count: 320)
    private static let loudnessWeights: [Double] = {
        let shelf = BiquadCoefficients(b0: 1.53512485958697, b1: -2.69169618940638, b2: 1.19839281085285,
                                       a1: -1.69065929318241, a2: 0.73248077421585)
        let highPass = BiquadCoefficients(b0: 1, b1: -2, b2: 1, a1: -1.99004745483398, a2: 0.99007225036621)
        return loudnessGrid.map { f in
            let g = shelf.magnitude(at: f, sampleRate: 48_000) * highPass.magnitude(at: f, sampleRate: 48_000)
            return g * g
        }
    }()

    /// How much the bands change the loudness of pink noise, in dB (LU): positive when the
    /// curve makes music louder. With `channel`, only the bands that process that channel count.
    public static func loudnessChangeDB(bands: [EQBand], sampleRate: Double, channel: EQChannels? = nil) -> Double {
        let response = responseDB(bands: bands, frequencies: loudnessGrid, sampleRate: sampleRate, channel: channel)
        var power = 0.0, total = 0.0
        for (db, w) in zip(response, loudnessWeights) {
            power += w * pow(10, db / 10)
            total += w
        }
        return 10 * log10(max(power / total, 1e-12))
    }
}

extension DSPSettings {
    /// Builds the C parameter block for the realtime kernel. With `levelMatchedBypass`, the
    /// bypassed signal plays at the loudness of the EQ (see `bypassGainDB`), else at unity.
    public func kernelParams(sampleRate: Double, levelMatchedBypass: Bool = true) -> tl_params {
        var p = tl_params()
        tl_params_init(&p)
        p.eq_enabled = eqEnabled ? 1 : 0
        let preampDB = effectivePreampDB(sampleRate: sampleRate)
        p.preamp = Float(dbToLinear(preampDB))
        if levelMatchedBypass {
            p.bypass_gain = Float(dbToLinear(Self.bypassGainDB(loudnessDB: preampDB + curveLoudnessDB(sampleRate: sampleRate))))
        }

        // EQ bands keep their slot index so filter state survives edits.
        // Tone macros live in the last four slots.
        var stages = [tl_biquad](repeating: tl_biquad(b0: 1, b1: 0, b2: 0, a1: 0, a2: 0, active: 0, channels: 0), count: Int(TL_MAX_STAGES))
        for (i, band) in bands.prefix(Self.maxBands).enumerated() where band.isEnabled {
            stages[i] = BiquadCoefficients(band: band.clamped(), sampleRate: sampleRate).cValue(channels: band.channels)
        }
        let toneBase = Int(TL_MAX_STAGES) - 4
        for (i, band) in tone.bands.enumerated() where band.isEnabled {
            stages[toneBase + i] = BiquadCoefficients(band: band, sampleRate: sampleRate).cValue()
        }
        withUnsafeMutableBytes(of: &p.stages) { raw in
            let ptr = raw.bindMemory(to: tl_biquad.self)
            for i in 0..<Int(TL_MAX_STAGES) { ptr[i] = stages[i] }
        }

        p.output_gain = Float(dbToLinear(effects.outputGainDB.clamped(to: EffectsSettings.outputGainRange)))
        p.balance = Float(effects.balance.clamped(to: -1...1))
        p.width = Float(effects.width.clamped(to: 0...2))
        p.mono = effects.mono ? 1 : 0
        p.swap_channels = effects.swapChannels ? 1 : 0
        p.crossfeed_enabled = effects.crossfeedEnabled ? 1 : 0
        p.crossfeed = Float(effects.crossfeed.clamped(to: 0...1))
        p.limiter_enabled = effects.limiterEnabled ? 1 : 0
        p.limiter_ceiling = Float(dbToLinear(effects.limiterCeilingDB.clamped(to: -12...0)))
        return p
    }
}
