import Foundation

public enum FilterType: String, Codable, CaseIterable, Sendable, Identifiable {
    case peak
    case lowShelf
    case highShelf
    case lowPass
    case highPass
    case notch
    case bandPass

    public var id: String { rawValue }

    /// Whether the gain parameter has any effect for this filter type.
    public var usesGain: Bool {
        switch self {
        case .peak, .lowShelf, .highShelf: true
        case .lowPass, .highPass, .notch, .bandPass: false
        }
    }
}

/// Which output channels a band processes.
public enum EQChannels: String, Codable, CaseIterable, Sendable, Identifiable {
    case both, left, right

    public var id: String { rawValue }

    /// Whether a band set to `self` processes `channel` (`.left` or `.right`).
    public func includes(_ channel: EQChannels) -> Bool { self == .both || self == channel }
}

public struct EQBand: Codable, Hashable, Identifiable, Sendable {
    public var id: UUID
    public var type: FilterType
    public var frequency: Double
    public var gain: Double
    public var q: Double
    public var isEnabled: Bool
    public var channels: EQChannels

    public init(id: UUID = UUID(), type: FilterType = .peak, frequency: Double, gain: Double = 0, q: Double = 1, isEnabled: Bool = true, channels: EQChannels = .both) {
        self.id = id
        self.type = type
        self.frequency = frequency
        self.gain = gain
        self.q = q
        self.isEnabled = isEnabled
        self.channels = channels
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(UUID.self, forKey: .id)
        type = try c.decode(FilterType.self, forKey: .type)
        frequency = try c.decode(Double.self, forKey: .frequency)
        gain = try c.decode(Double.self, forKey: .gain)
        q = try c.decode(Double.self, forKey: .q)
        isEnabled = try c.decode(Bool.self, forKey: .isEnabled)
        channels = try c.decodeIfPresent(EQChannels.self, forKey: .channels) ?? .both
    }

    public static let frequencyRange: ClosedRange<Double> = 20...20_000
    public static let gainRange: ClosedRange<Double> = -24...24
    public static let qRange: ClosedRange<Double> = 0.1...18

    /// Keeps every parameter in range. NaN becomes a neutral value (1 kHz, 0 dB, Q 1).
    public func clamped() -> EQBand {
        var b = self
        b.frequency = frequency.isNaN ? 1000 : frequency.clamped(to: Self.frequencyRange)
        b.gain = gain.isNaN ? 0 : gain.clamped(to: Self.gainRange)
        b.q = q.isNaN ? 1 : q.clamped(to: Self.qRange)
        return b
    }

    /// The band with its gain scaled by `strength` (0...1), to apply part of a ready-made
    /// correction. Filters without a gain (high-pass, notch…) stay as they are.
    public func scaled(by strength: Double) -> EQBand {
        guard type.usesGain else { return self }
        var b = self
        b.gain = gain * (strength.isNaN ? 1 : strength.clamped(to: 0...1))
        return b
    }
}

/// Simple "macro" tone controls layered on top of the parametric EQ.
public struct ToneSettings: Codable, Hashable, Sendable {
    public var bass: Double = 0      // low shelf 105 Hz
    public var warmth: Double = 0    // peak 250 Hz
    public var clarity: Double = 0   // peak 3 kHz
    public var air: Double = 0       // high shelf 10 kHz

    public init(bass: Double = 0, warmth: Double = 0, clarity: Double = 0, air: Double = 0) {
        self.bass = bass
        self.warmth = warmth
        self.clarity = clarity
        self.air = air
    }

    public static let range: ClosedRange<Double> = -12...12

    public var isNeutral: Bool { bass == 0 && warmth == 0 && clarity == 0 && air == 0 }

    public var bands: [EQBand] {
        [
            EQBand(id: Self.ids[0], type: .lowShelf, frequency: 105, gain: bass, q: 0.71, isEnabled: bass != 0),
            EQBand(id: Self.ids[1], type: .peak, frequency: 250, gain: warmth, q: 0.8, isEnabled: warmth != 0),
            EQBand(id: Self.ids[2], type: .peak, frequency: 3000, gain: clarity, q: 0.9, isEnabled: clarity != 0),
            EQBand(id: Self.ids[3], type: .highShelf, frequency: 10_000, gain: air, q: 0.71, isEnabled: air != 0),
        ]
    }

    private static let ids: [UUID] = (0..<4).map { _ in UUID() }
}

public struct EffectsSettings: Codable, Hashable, Sendable {
    public var balance: Double = 0          // -1 ... 1
    public var width: Double = 1            // 0 ... 2
    public var mono: Bool = false
    public var swapChannels: Bool = false
    public var crossfeedEnabled: Bool = false
    public var crossfeed: Double = 0.5      // 0 ... 1
    /// Headphones: speakers at +-30 degrees through binaural impulse responses. Excludes crossfeed.
    public var virtualSpeakersEnabled: Bool = false
    /// The impulse responses: a built-in set's id or an imported set's.
    public var virtualSpeakers: String = EffectsSettings.defaultVirtualSpeakers
    public var limiterEnabled: Bool = true
    public var limiterCeilingDB: Double = -0.3
    public var outputGainDB: Double = 0

    public static let outputGainRange: ClosedRange<Double> = -24...12
    public static let defaultVirtualSpeakers = "sadie-ku100-room"

    public init() {}

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = EffectsSettings()
        balance = try c.decodeIfPresent(Double.self, forKey: .balance) ?? d.balance
        width = try c.decodeIfPresent(Double.self, forKey: .width) ?? d.width
        mono = try c.decodeIfPresent(Bool.self, forKey: .mono) ?? d.mono
        swapChannels = try c.decodeIfPresent(Bool.self, forKey: .swapChannels) ?? d.swapChannels
        crossfeedEnabled = try c.decodeIfPresent(Bool.self, forKey: .crossfeedEnabled) ?? d.crossfeedEnabled
        crossfeed = try c.decodeIfPresent(Double.self, forKey: .crossfeed) ?? d.crossfeed
        virtualSpeakersEnabled = try c.decodeIfPresent(Bool.self, forKey: .virtualSpeakersEnabled) ?? d.virtualSpeakersEnabled
        virtualSpeakers = try c.decodeIfPresent(String.self, forKey: .virtualSpeakers) ?? d.virtualSpeakers
        limiterEnabled = try c.decodeIfPresent(Bool.self, forKey: .limiterEnabled) ?? d.limiterEnabled
        limiterCeilingDB = try c.decodeIfPresent(Double.self, forKey: .limiterCeilingDB) ?? d.limiterCeilingDB
        outputGainDB = try c.decodeIfPresent(Double.self, forKey: .outputGainDB) ?? d.outputGainDB
    }
}

/// One speaker's room correction on a multichannel output, from the Room page.
public struct SpeakerCorrection: Codable, Hashable, Sendable {
    /// The output channel (0-based, counted over the whole device).
    public var channel: Int
    public var position: SpeakerPosition
    /// Its room correction. The `channels` of each band is ignored.
    public var bands: [EQBand]
    /// Level trim, never a boost.
    public var trimDB: Double
    /// Holds the speaker back until the farthest one arrives.
    public var delayMS: Double

    public init(channel: Int, position: SpeakerPosition, bands: [EQBand] = [], trimDB: Double = 0, delayMS: Double = 0) {
        self.channel = channel
        self.position = position
        self.bands = bands
        self.trimDB = trimDB
        self.delayMS = delayMS
    }

    public static let maxBands = 16
    public static let trimRange: ClosedRange<Double> = -24...0
    public static let delayRange: ClosedRange<Double> = 0...SpeakerAlignment.maxDelayMS
}

/// How a multichannel output (5.1, 7.1.4…) is processed. The EQ, tone controls and output
/// gain play on every speaker (or on the front pair only); each speaker then gets its own
/// room correction, trim and delay. A stereo output ignores all of this.
public struct SpeakerSetup: Codable, Hashable, Sendable {
    /// The EQ plays on every speaker; off: on the front left and right only. Never boosts
    /// above 120 Hz on LFE channels.
    public var eqOnAllSpeakers = true
    /// Whether the speaker corrections play. Switching off keeps them.
    public var correctionEnabled = true
    public var corrections: [SpeakerCorrection] = []
    /// The layout the corrections were measured on: they only play while the output still has it.
    public var layout: SpeakerLayout?

    public init(eqOnAllSpeakers: Bool = true, correctionEnabled: Bool = true, corrections: [SpeakerCorrection] = [], layout: SpeakerLayout? = nil) {
        self.eqOnAllSpeakers = eqOnAllSpeakers
        self.correctionEnabled = correctionEnabled
        self.corrections = corrections
        self.layout = layout
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        eqOnAllSpeakers = try c.decodeIfPresent(Bool.self, forKey: .eqOnAllSpeakers) ?? true
        correctionEnabled = try c.decodeIfPresent(Bool.self, forKey: .correctionEnabled) ?? true
        corrections = try c.decodeIfPresent([SpeakerCorrection].self, forKey: .corrections) ?? []
        layout = try c.decodeIfPresent(SpeakerLayout.self, forKey: .layout)
    }

    /// The corrections that play on an output with `layout`: none when switched off or measured on other speakers.
    public func activeCorrections(for layout: SpeakerLayout?) -> [SpeakerCorrection] {
        guard correctionEnabled, let layout, layout == self.layout else { return [] }
        return corrections
    }

    /// Headroom for the corrections' boosts, dB (zero or negative). The same for every speaker,
    /// so the trims keep the speakers level-matched.
    public static func headroomDB(_ corrections: [SpeakerCorrection], sampleRate: Double = 48_000) -> Double {
        let boost = corrections.map { ResponseCurve.maxBoostDB(bands: $0.bands.map(Self.mono), sampleRate: sampleRate) }.max() ?? 0
        return -max(0, boost)
    }

    /// A band as a speaker plays it: on its one channel.
    static func mono(_ band: EQBand) -> EQBand {
        var b = band.clamped()
        b.channels = .both
        return b
    }

    /// LFE channels carry nothing above about 120 Hz; the EQ's boosts there would only lift noise.
    public static let lfeBoostCeiling = 120.0

    /// Whether the EQ leaves `band` out on LFE channels: bells and high shelves that boost above 120 Hz.
    public static func skipsOnLFE(_ band: EQBand) -> Bool {
        guard band.gain > 0, band.frequency > lfeBoostCeiling else { return false }
        return band.type == .peak || band.type == .highShelf
    }
}

/// The full processing state for one output device.
public struct DSPSettings: Codable, Hashable, Sendable {
    public static let maxBands = 24
    public static let preampRange: ClosedRange<Double> = -24...12

    public var eqEnabled: Bool = true
    public var preampDB: Double = 0
    public var autoPreamp: Bool = true
    public var bands: [EQBand] = []
    public var tone = ToneSettings()
    public var effects = EffectsSettings()
    /// Multichannel outputs only.
    public var speakers = SpeakerSetup()

    public init(eqEnabled: Bool = true, preampDB: Double = 0, autoPreamp: Bool = true, bands: [EQBand] = [], tone: ToneSettings = ToneSettings(),
                effects: EffectsSettings = EffectsSettings(), speakers: SpeakerSetup = SpeakerSetup()) {
        self.eqEnabled = eqEnabled
        self.preampDB = preampDB
        self.autoPreamp = autoPreamp
        self.bands = bands
        self.tone = tone
        self.effects = effects
        self.speakers = speakers
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        eqEnabled = try c.decodeIfPresent(Bool.self, forKey: .eqEnabled) ?? true
        preampDB = try c.decodeIfPresent(Double.self, forKey: .preampDB) ?? 0
        autoPreamp = try c.decodeIfPresent(Bool.self, forKey: .autoPreamp) ?? true
        bands = try c.decodeIfPresent([EQBand].self, forKey: .bands) ?? []
        tone = try c.decodeIfPresent(ToneSettings.self, forKey: .tone) ?? ToneSettings()
        effects = try c.decodeIfPresent(EffectsSettings.self, forKey: .effects) ?? EffectsSettings()
        speakers = try c.decodeIfPresent(SpeakerSetup.self, forKey: .speakers) ?? SpeakerSetup()
    }

    /// Bands that actually shape the signal: parametric bands followed by tone macros.
    public var activeBands: [EQBand] {
        bands.filter(\.isEnabled) + tone.bands.filter(\.isEnabled)
    }

    /// Whether left and right get different curves.
    public var hasChannelSpecificBands: Bool {
        bands.contains { $0.isEnabled && $0.channels != .both }
    }

    /// Preamp that is actually applied: either the manual value, or enough
    /// headroom to cancel the largest boost of the curve.
    public func effectivePreampDB(sampleRate: Double = 48_000) -> Double {
        guard autoPreamp else { return preampDB.clamped(to: Self.preampRange) }
        let boost = ResponseCurve.maxBoostDB(bands: activeBands, sampleRate: sampleRate)
        return -max(0, boost)
    }

    /// How much the whole EQ (bands, tone controls and preamp) changes the loudness of
    /// pink noise, in dB (LU, K-weighted as in ITU-R BS.1770). A correction with a -6 dB
    /// preamp typically comes out a few dB quieter than the original.
    public func eqLoudnessDB(sampleRate: Double = 48_000) -> Double {
        effectivePreampDB(sampleRate: sampleRate) + curveLoudnessDB(sampleRate: sampleRate)
    }

    /// Gain that takes the place of the EQ while it is bypassed, so that switching it off
    /// changes the sound but not the loudness: the EQ's loudness change, but never a boost,
    /// so bypass cannot drive the limiter harder than the original sound does.
    public func bypassGainDB(sampleRate: Double = 48_000) -> Double {
        Self.bypassGainDB(loudnessDB: eqLoudnessDB(sampleRate: sampleRate))
    }

    static func bypassGainDB(loudnessDB: Double) -> Double {
        loudnessDB.isFinite ? loudnessDB.clamped(to: preampRange.lowerBound...0) : 0
    }

    /// Loudness change of the bands and tone controls as the kernel plays them. Left and
    /// right count equally when they get different curves.
    func curveLoudnessDB(sampleRate: Double) -> Double {
        let bands = activeBands.map { $0.clamped() }
        guard !bands.isEmpty else { return 0 }
        guard hasChannelSpecificBands else { return ResponseCurve.loudnessChangeDB(bands: bands, sampleRate: sampleRate) }
        let power = [EQChannels.left, .right].reduce(0.0) {
            $0 + pow(10, ResponseCurve.loudnessChangeDB(bands: bands, sampleRate: sampleRate, channel: $1) / 10)
        }
        return 10 * log10(power / 2)
    }

    /// The default five-band layout used for new profiles.
    public static func defaultBands() -> [EQBand] {
        [
            EQBand(type: .lowShelf, frequency: 80, gain: 0, q: 0.71),
            EQBand(type: .peak, frequency: 250, gain: 0, q: 1.0),
            EQBand(type: .peak, frequency: 1000, gain: 0, q: 1.0),
            EQBand(type: .peak, frequency: 4000, gain: 0, q: 1.0),
            EQBand(type: .highShelf, frequency: 10_000, gain: 0, q: 0.71),
        ]
    }

    public static let flat = DSPSettings(bands: defaultBands())
}

extension Comparable {
    /// NaN, which compares false with everything, ends up at the lower bound.
    public func clamped(to range: ClosedRange<Self>) -> Self {
        if self > range.upperBound { return range.upperBound }
        return self >= range.lowerBound ? self : range.lowerBound
    }
}

public func dbToLinear(_ db: Double) -> Double { pow(10, db / 20) }
public func linearToDB(_ linear: Double) -> Double { 20 * log10(max(linear, 1e-12)) }
