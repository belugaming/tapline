import Foundation
import TaplineDSP

/// The Tapline app's preferences that the Audio Unit reads: the user's presets (headphone
/// corrections included) and the device last chosen for "Listen as". The app writes them; the
/// sandboxed extension reads them through a read-only temporary-exception entitlement for this
/// one preferences domain (TaplineAU/TaplineAU.entitlements). No app group is involved: those
/// need a team ID, and Tapline is signed with its own certificate.
public enum SharedPresets {
    /// The app's preferences domain, its bundle identifier.
    public static let domain = "io.github.belugaming.Tapline"
    public static let userPresetsKey = "userPresets"
    public static let listenAsKey = "listenAs"

    /// The user's presets as the app stores them. Entries this version cannot read are left out;
    /// empty when there are none or the domain cannot be read.
    public static func userPresets(in defaults: UserDefaults) -> [EQPreset] {
        guard let data = defaults.data(forKey: userPresetsKey) else { return [] }
        var reader = TolerantJSON()
        return reader.decode([EQPreset].self, from: data) ?? []
    }

    /// The headphones or earbuds last chosen for "Listen as" in the app (fitted from a squig.link
    /// measurement), if the device chosen last is one. The built-in speakers need no app.
    public static func listenAsHeadphones(in defaults: UserDefaults) -> DeviceSimulation? {
        guard let data = defaults.data(forKey: listenAsKey),
              let simulation = try? JSONDecoder().decode(DeviceSimulation.self, from: data),
              simulation.kind == .measured, !simulation.bands.isEmpty else { return nil }
        return simulation
    }
}

/// The Audio Unit's automatable parameters, by address.
public enum AudioUnitParameter: UInt64, CaseIterable, Sendable {
    /// 0 ... 100 %: how much of the curve plays.
    case strength = 0
    /// 1: the EQ plays; 0: its (level-matched) bypass.
    case eq = 1
    /// 0 / 1.
    case virtualSpeakers = 2
    /// 0: off, then the built-in speaker profiles in `SpeakerProfile.allCases` order, then the
    /// headphones from the app (`listenAsHeadphones`).
    case listenAs = 3
    /// `FilterPhase.allCases` by index: minimum, linear low latency, linear high precision.
    case filterPhase = 4

    public var identifier: String {
        switch self {
        case .strength: "strength"
        case .eq: "eq"
        case .virtualSpeakers: "virtualSpeakers"
        case .listenAs: "listenAs"
        case .filterPhase: "filterPhase"
        }
    }

    public var range: ClosedRange<Float> {
        switch self {
        case .strength: 0...100
        case .eq, .virtualSpeakers: 0...1
        case .listenAs: 0...Self.listenAsHeadphones
        case .filterPhase: 0...Float(FilterPhase.allCases.count - 1)
        }
    }

    /// The value of `listenAs` that plays the headphones from the app.
    public static let listenAsHeadphones = Float(SpeakerProfile.allCases.count + 1)
}

/// Everything the Audio Unit plays: a copy of the Tapline preset (or imported ParametricEQ.txt)
/// it was set to, how much of it, and the listening effects. Saved in the host's project (the
/// Audio Unit's full state), so a project reopens with the same sound even when the preset has
/// changed since or the Mac it opens on does not have it.
public struct AudioUnitState: Codable, Hashable, Sendable {
    /// Written with the state; a later version can tell what it reads.
    public static let formatVersion = 1

    /// The curve. Its id marks the preset it came from in the preset menu.
    public var preset: EQPreset
    /// 0 ... 1, like a dry/wet control: each band's gain (and a manual preamp) times this, as the
    /// app's correction strength does for ready-made curves, so it never comb-filters.
    public var strength: Double
    /// Off: the bypass plays instead, at the EQ's loudness with `levelMatchedBypass`.
    public var eqEnabled: Bool
    public var levelMatchedBypass: Bool
    public var filterPhase: FilterPhase
    public var virtualSpeakersEnabled: Bool
    /// A built-in set's id.
    public var virtualSpeakers: String
    /// "Listen as": the device chosen last, kept while off so switching fades.
    public var listenAs: DeviceSimulation?
    public var listening: Bool

    public init(preset: EQPreset = BuiltInPresets.all[0], strength: Double = 1, eqEnabled: Bool = true, levelMatchedBypass: Bool = true,
                filterPhase: FilterPhase = .minimum, virtualSpeakersEnabled: Bool = false,
                virtualSpeakers: String = EffectsSettings.defaultVirtualSpeakers, listenAs: DeviceSimulation? = nil, listening: Bool = false) {
        self.preset = preset
        self.strength = strength
        self.eqEnabled = eqEnabled
        self.levelMatchedBypass = levelMatchedBypass
        self.filterPhase = filterPhase
        self.virtualSpeakersEnabled = virtualSpeakersEnabled
        self.virtualSpeakers = virtualSpeakers
        self.listenAs = listenAs
        self.listening = listening
    }

    private enum CodingKeys: String, CodingKey {
        case version, preset, strength, eqEnabled, levelMatchedBypass, filterPhase, virtualSpeakersEnabled, virtualSpeakers, listenAs, listening
    }

    /// Reads what it can: a value it does not know (from a newer version) keeps its default.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = AudioUnitState()
        preset = (try? c.decodeIfPresent(EQPreset.self, forKey: .preset)) ?? d.preset
        strength = ((try? c.decodeIfPresent(Double.self, forKey: .strength)) ?? d.strength).clamped(to: 0...1)
        eqEnabled = (try? c.decodeIfPresent(Bool.self, forKey: .eqEnabled)) ?? d.eqEnabled
        levelMatchedBypass = (try? c.decodeIfPresent(Bool.self, forKey: .levelMatchedBypass)) ?? d.levelMatchedBypass
        filterPhase = (try? c.decodeIfPresent(FilterPhase.self, forKey: .filterPhase)) ?? d.filterPhase
        virtualSpeakersEnabled = (try? c.decodeIfPresent(Bool.self, forKey: .virtualSpeakersEnabled)) ?? d.virtualSpeakersEnabled
        virtualSpeakers = (try? c.decodeIfPresent(String.self, forKey: .virtualSpeakers)) ?? d.virtualSpeakers
        listenAs = try? c.decodeIfPresent(DeviceSimulation.self, forKey: .listenAs)
        listening = ((try? c.decodeIfPresent(Bool.self, forKey: .listening)) ?? d.listening) && listenAs != nil
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(Self.formatVersion, forKey: .version)
        try c.encode(preset, forKey: .preset)
        try c.encode(strength, forKey: .strength)
        try c.encode(eqEnabled, forKey: .eqEnabled)
        try c.encode(levelMatchedBypass, forKey: .levelMatchedBypass)
        try c.encode(filterPhase, forKey: .filterPhase)
        try c.encode(virtualSpeakersEnabled, forKey: .virtualSpeakersEnabled)
        try c.encode(virtualSpeakers, forKey: .virtualSpeakers)
        try c.encodeIfPresent(listenAs, forKey: .listenAs)
        try c.encode(listening, forKey: .listening)
    }

    /// The state as the host saves it.
    public func encoded() -> Data {
        (try? JSONEncoder().encode(self)) ?? Data()
    }

    /// Nil when `data` is not a saved state at all.
    public init?(encoded data: Data) {
        guard let state = try? JSONDecoder().decode(Self.self, from: data) else { return nil }
        self = state
    }

    /// The device "Listen as" plays, nil while off.
    public var playingDevice: DeviceSimulation? { listening ? listenAs : nil }

    /// The settings the kernel plays: the curve at `strength`, the phase and Virtual Speakers;
    /// the other effects at the app's defaults (the limiter on at -0.3 dBFS).
    public var settings: DSPSettings {
        let s = strength.isNaN ? 1 : strength.clamped(to: 0...1)
        var d = DSPSettings()
        d.eqEnabled = eqEnabled
        d.bands = preset.bands.prefix(DSPSettings.maxBands).map { $0.scaled(by: s) }
        let t = preset.tone
        d.tone = ToneSettings(bass: t.bass * s, warmth: t.warmth * s, clarity: t.clarity * s, air: t.air * s)
        d.autoPreamp = preset.autoPreamp
        d.preampDB = preset.preampDB * s
        d.filterPhase = filterPhase
        d.effects.virtualSpeakersEnabled = virtualSpeakersEnabled
        d.effects.virtualSpeakers = virtualSpeakers
        return d
    }

    /// The kernel's parameter block at `sampleRate`.
    public func kernelParams(sampleRate: Double) -> tl_params {
        settings.kernelParams(sampleRate: sampleRate, levelMatchedBypass: levelMatchedBypass,
                              simulation: listenAs, simulating: listening && listenAs != nil)
    }

    /// The value of a parameter for the host.
    public func value(of parameter: AudioUnitParameter) -> Float {
        switch parameter {
        case .strength:
            return Float((strength.isNaN ? 1 : strength.clamped(to: 0...1)) * 100)
        case .eq:
            return eqEnabled ? 1 : 0
        case .virtualSpeakers:
            return virtualSpeakersEnabled ? 1 : 0
        case .listenAs:
            guard let device = playingDevice else { return 0 }
            if let profile = device.profile, let i = SpeakerProfile.allCases.firstIndex(of: profile) { return Float(i + 1) }
            return AudioUnitParameter.listenAsHeadphones
        case .filterPhase:
            return Float(FilterPhase.allCases.firstIndex(of: filterPhase) ?? 0)
        }
    }

    /// Sets a parameter from the host. `headphones` are the app's (`SharedPresets.listenAsHeadphones`),
    /// for the last value of `listenAs`; without them it plays the headphones it already has, if any.
    public mutating func set(_ parameter: AudioUnitParameter, to value: Float, headphones: DeviceSimulation? = nil) {
        guard value.isFinite else { return }
        let v = value.clamped(to: parameter.range)
        switch parameter {
        case .strength:
            strength = Double(v) / 100
        case .eq:
            eqEnabled = v >= 0.5
        case .virtualSpeakers:
            virtualSpeakersEnabled = v >= 0.5
        case .listenAs:
            let index = Int(v.rounded())
            if index == 0 {
                listening = false
            } else if index <= SpeakerProfile.allCases.count {
                listenAs = SpeakerProfile.allCases[index - 1].simulation
                listening = true
            } else if let device = headphones ?? (listenAs?.kind == .measured ? listenAs : nil) {
                listenAs = device
                listening = true
            }
        case .filterPhase:
            filterPhase = FilterPhase.allCases[Int(v.rounded())]
        }
    }
}
