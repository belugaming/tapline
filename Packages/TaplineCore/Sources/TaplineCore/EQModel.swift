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
    public var limiterEnabled: Bool = true
    public var limiterCeilingDB: Double = -0.3
    public var outputGainDB: Double = 0

    public static let outputGainRange: ClosedRange<Double> = -24...12

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
        limiterEnabled = try c.decodeIfPresent(Bool.self, forKey: .limiterEnabled) ?? d.limiterEnabled
        limiterCeilingDB = try c.decodeIfPresent(Double.self, forKey: .limiterCeilingDB) ?? d.limiterCeilingDB
        outputGainDB = try c.decodeIfPresent(Double.self, forKey: .outputGainDB) ?? d.outputGainDB
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

    public init(eqEnabled: Bool = true, preampDB: Double = 0, autoPreamp: Bool = true, bands: [EQBand] = [], tone: ToneSettings = ToneSettings(), effects: EffectsSettings = EffectsSettings()) {
        self.eqEnabled = eqEnabled
        self.preampDB = preampDB
        self.autoPreamp = autoPreamp
        self.bands = bands
        self.tone = tone
        self.effects = effects
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        eqEnabled = try c.decodeIfPresent(Bool.self, forKey: .eqEnabled) ?? true
        preampDB = try c.decodeIfPresent(Double.self, forKey: .preampDB) ?? 0
        autoPreamp = try c.decodeIfPresent(Bool.self, forKey: .autoPreamp) ?? true
        bands = try c.decodeIfPresent([EQBand].self, forKey: .bands) ?? []
        tone = try c.decodeIfPresent(ToneSettings.self, forKey: .tone) ?? ToneSettings()
        effects = try c.decodeIfPresent(EffectsSettings.self, forKey: .effects) ?? EffectsSettings()
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
