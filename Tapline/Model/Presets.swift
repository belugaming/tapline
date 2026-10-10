import Foundation
import TaplineCore

/// Per-app mixer settings, keyed by app id.
struct AppAudioSettings: Codable, Hashable, Sendable {
    var name: String
    var bundleID: String?
    var volume: Double = 1
    var muted = false
    /// Play the app directly, without any Tapline processing.
    var bypass = false

    var gain: Float { muted ? 0 : Float(volume) }
}

/// A saved EQ curve (bands, preamp and tone). Effects stay with the device.
struct EQPreset: Codable, Hashable, Identifiable, Sendable {
    var id: UUID = UUID()
    var name: String
    var bands: [EQBand]
    var preampDB: Double
    var autoPreamp: Bool
    var tone: ToneSettings
    var isBuiltIn = false
    var source: String?

    init(id: UUID = UUID(), name: String, bands: [EQBand], preampDB: Double = 0, autoPreamp: Bool = true, tone: ToneSettings = ToneSettings(), isBuiltIn: Bool = false, source: String? = nil) {
        self.id = id
        self.name = name
        self.bands = bands
        self.preampDB = preampDB
        self.autoPreamp = autoPreamp
        self.tone = tone
        self.isBuiltIn = isBuiltIn
        self.source = source
    }

    init(name: String, from settings: DSPSettings, source: String? = nil) {
        self.init(name: name, bands: settings.bands, preampDB: settings.preampDB, autoPreamp: settings.autoPreamp, tone: settings.tone, source: source)
    }

    func apply(to settings: inout DSPSettings) {
        settings.bands = bands.map { var b = $0; b.id = UUID(); return b }
        settings.preampDB = preampDB
        settings.autoPreamp = autoPreamp
        settings.tone = tone
        settings.eqEnabled = true
    }

    func matches(_ settings: DSPSettings) -> Bool {
        func strip(_ b: [EQBand]) -> [EQBand] { b.map { var x = $0; x.id = UUID(uuidString: "00000000-0000-0000-0000-000000000000")!; return x } }
        return strip(bands) == strip(settings.bands) && tone == settings.tone && autoPreamp == settings.autoPreamp && (autoPreamp || preampDB == settings.preampDB)
    }
}

enum BuiltInPresets {
    private static func id(_ n: Int) -> UUID { UUID(uuidString: String(format: "00000000-0000-0000-0000-%012d", n))! }

    static let all: [EQPreset] = [
        EQPreset(id: id(1), name: String(localized: "Flat"), bands: DSPSettings.defaultBands(), isBuiltIn: true),
        EQPreset(id: id(2), name: String(localized: "Bass Boost"), bands: [
            EQBand(type: .lowShelf, frequency: 110, gain: 5, q: 0.71),
            EQBand(type: .peak, frequency: 250, gain: -1, q: 1.0),
            EQBand(type: .peak, frequency: 1000, gain: 0, q: 1.0),
            EQBand(type: .peak, frequency: 4000, gain: 0, q: 1.0),
            EQBand(type: .highShelf, frequency: 10_000, gain: 0, q: 0.71),
        ], isBuiltIn: true),
        EQPreset(id: id(3), name: String(localized: "Bass Reducer"), bands: [
            EQBand(type: .lowShelf, frequency: 120, gain: -5, q: 0.71),
            EQBand(type: .peak, frequency: 250, gain: -1, q: 1.0),
        ], isBuiltIn: true),
        EQPreset(id: id(4), name: String(localized: "Treble Boost"), bands: [
            EQBand(type: .highShelf, frequency: 6000, gain: 4, q: 0.71),
        ], isBuiltIn: true),
        EQPreset(id: id(5), name: String(localized: "Vocal Clarity"), bands: [
            EQBand(type: .highPass, frequency: 80, q: 0.71),
            EQBand(type: .peak, frequency: 250, gain: -2, q: 1.0),
            EQBand(type: .peak, frequency: 2500, gain: 3, q: 1.0),
            EQBand(type: .peak, frequency: 5000, gain: 1.5, q: 1.2),
        ], isBuiltIn: true),
        EQPreset(id: id(6), name: String(localized: "Warm"), bands: [
            EQBand(type: .lowShelf, frequency: 150, gain: 2.5, q: 0.71),
            EQBand(type: .peak, frequency: 3500, gain: -1.5, q: 1.0),
            EQBand(type: .highShelf, frequency: 9000, gain: -2, q: 0.71),
        ], isBuiltIn: true),
        EQPreset(id: id(7), name: String(localized: "Bright"), bands: [
            EQBand(type: .peak, frequency: 250, gain: -1.5, q: 1.0),
            EQBand(type: .peak, frequency: 4000, gain: 2, q: 0.9),
            EQBand(type: .highShelf, frequency: 10_000, gain: 3, q: 0.71),
        ], isBuiltIn: true),
        EQPreset(id: id(8), name: String(localized: "V-Shape"), bands: [
            EQBand(type: .lowShelf, frequency: 100, gain: 4, q: 0.71),
            EQBand(type: .peak, frequency: 1000, gain: -3, q: 0.7),
            EQBand(type: .highShelf, frequency: 8000, gain: 4, q: 0.71),
        ], isBuiltIn: true),
        EQPreset(id: id(9), name: String(localized: "Loudness (low volume)"), bands: [
            EQBand(type: .lowShelf, frequency: 90, gain: 6, q: 0.71),
            EQBand(type: .peak, frequency: 3000, gain: -1, q: 1.0),
            EQBand(type: .highShelf, frequency: 12_000, gain: 3, q: 0.71),
        ], isBuiltIn: true),
        EQPreset(id: id(10), name: String(localized: "Podcast / Speech"), bands: [
            EQBand(type: .highPass, frequency: 100, q: 0.71),
            EQBand(type: .peak, frequency: 200, gain: -2, q: 0.9),
            EQBand(type: .peak, frequency: 3000, gain: 3, q: 0.8),
            EQBand(type: .lowPass, frequency: 14_000, q: 0.71),
        ], isBuiltIn: true),
    ]
}
