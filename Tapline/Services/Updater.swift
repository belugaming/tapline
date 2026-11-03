import Foundation
import Observation
import Sparkle

/// Automatic updates through Sparkle. The feed (SUFeedURL in Info.plist) is the appcast.xml
/// that .github/workflows/release.yml publishes with every release; each update is checked
/// against the EdDSA key in SUPublicEDKey before it is installed.
///
/// Tapline lives in the menu bar, so a scheduled check that finds an update brings its
/// window forward right away (gentle reminders) instead of waiting behind other apps.
@MainActor
@Observable
final class Updater: NSObject {
    static let shared = Updater()

    /// Whether a check can start now (not while one runs). Updated from Sparkle by KVO.
    private(set) var canCheckForUpdates = false

    /// Off in development and snapshot runs: their build number would always be "older".
    let isAvailable: Bool

    @ObservationIgnored private var controller: SPUStandardUpdaterController?
    @ObservationIgnored private var observation: NSKeyValueObservation?

    private override init() {
        #if DEBUG
        isAvailable = false
        #else
        isAvailable = !DebugSnapshot.isDevelopmentMode
        #endif
        super.init()
        guard isAvailable else { return }
        let controller = SPUStandardUpdaterController(startingUpdater: true, updaterDelegate: nil, userDriverDelegate: self)
        self.controller = controller
        observation = controller.updater.observe(\.canCheckForUpdates, options: [.initial, .new]) { [weak self] updater, _ in
            // Sparkle changes it on the main thread.
            MainActor.assumeIsolated { self?.canCheckForUpdates = updater.canCheckForUpdates }
        }
    }

    /// Starts Sparkle (called at launch).
    func start() {}

    func checkForUpdates() {
        controller?.checkForUpdates(nil)
    }

    /// Daily background checks. Sparkle keeps the choice in Tapline's preferences.
    var automaticallyChecks: Bool {
        get {
            access(keyPath: \.automaticallyChecks)
            return controller?.updater.automaticallyChecksForUpdates ?? false
        }
        set {
            withMutation(keyPath: \.automaticallyChecks) {
                controller?.updater.automaticallyChecksForUpdates = newValue
            }
        }
    }
}

extension Updater: SPUStandardUserDriverDelegate {
    nonisolated var supportsGentleScheduledUpdateReminders: Bool { true }

    /// Show a found update at once: a menu bar app has no Dock icon to badge.
    nonisolated func standardUserDriverShouldHandleShowingScheduledUpdate(_ update: SUAppcastItem, andInImmediateFocus immediateFocus: Bool) -> Bool {
        true
    }
}
