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

// EQPreset and BuiltInPresets live in TaplineCore, shared with the Audio Unit.
