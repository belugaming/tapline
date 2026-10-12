import AppKit

/// Tapline lives in the menu bar. It shows a Dock icon only while the main window is open.
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var observers: [NSObjectProtocol] = []

    func applicationDidFinishLaunching(_ notification: Notification) {
        if !DebugSnapshot.isDevelopmentMode, let other = SingleInstance.otherInstance {
            if SingleInstance.isSameBuild(as: other) {
                // Opened again: the running copy may only live in the menu bar, so show its window.
                SingleInstance.showWindow(of: other) { NSApp.terminate(nil) }
                return
            }
            // A different build, usually an update: it takes over (AppModel starts the audio
            // once the old copy is gone).
            SingleInstance.quit(other)
        }
        if let icon = NSImage(named: NSImage.applicationIconName) {
            NSApp.applicationIconImage = icon
        }
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: NSWindow.didBecomeKeyNotification, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { Self.updateActivationPolicy() }
        })
        observers.append(center.addObserver(forName: NSWindow.willCloseNotification, object: nil, queue: .main) { _ in
            DispatchQueue.main.async {
                MainActor.assumeIsolated { Self.updateActivationPolicy() }
            }
        })

        // Opened by the user (not at login): show the window right away.
        let launchedAtLogin = NSAppleEventManager.shared().currentAppleEvent?.paramDescriptor(forKeyword: keyAEPropData)?.enumCodeValue == keyAELaunchedAsLogInItem
        if !launchedAtLogin {
            DispatchQueue.main.async {
                MainActor.assumeIsolated { Self.showMainWindow() }
            }
        }
        if DebugSnapshot.isActive {
            MainActor.assumeIsolated { DebugSnapshot.run() }
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag { Self.showMainWindow() }
        return true
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    @MainActor
    private static var mainWindow: NSWindow? {
        NSApp.windows.first { isMainWindow($0) }
    }

    /// Whether the main window is on screen: open, not minimized and not entirely covered.
    @MainActor
    static var mainWindowIsOnScreen: Bool {
        NSApp.windows.contains { isMainWindow($0) && $0.occlusionState.contains(.visible) }
    }

    @MainActor
    private static func isMainWindow(_ window: NSWindow) -> Bool {
        window.identifier?.rawValue == "main" || window.title == "Tapline"
    }

    @MainActor
    static func showMainWindow() {
        NSApp.setActivationPolicy(.regular)
        NSApp.activate()
        if let window = mainWindow {
            window.makeKeyAndOrderFront(nil)
        } else {
            OpenWindowBridge.shared.open?("main")
        }
    }

    /// ⌘Q: close the window and leave the Dock, but keep processing audio in the menu bar.
    @MainActor
    static func hideToMenuBar() {
        mainWindow?.close()
        updateActivationPolicy()
    }

    @MainActor
    static func updateActivationPolicy() {
        let hasMainWindow = NSApp.windows.contains { $0.isVisible && $0.canBecomeMain && !($0 is NSPanel) && $0.className != "NSStatusBarWindow" }
        let target: NSApplication.ActivationPolicy = hasMainWindow ? .regular : .accessory
        if NSApp.activationPolicy() != target {
            NSApp.setActivationPolicy(target)
        }
    }
}

/// Lets AppKit code ask SwiftUI to open a scene.
@MainActor
final class OpenWindowBridge {
    static let shared = OpenWindowBridge()
    var open: ((String) -> Void)?
}

/// Only one Tapline may process audio at a time.
enum SingleInstance {
    static var otherInstance: NSRunningApplication? {
        guard let id = Bundle.main.bundleIdentifier else { return nil }
        let me = ProcessInfo.processInfo.processIdentifier
        return NSRunningApplication.runningApplications(withBundleIdentifier: id).first { $0.processIdentifier != me && !$0.isTerminated }
    }

    static var otherInstanceRunning: Bool { otherInstance != nil }

    /// Whether `other` runs the same version and build as this copy. LaunchServices starts a
    /// second copy from the same place only when the app there was replaced (an update), so
    /// that counts as different. Elsewhere, compare the other copy's version and build number.
    static func isSameBuild(as other: NSRunningApplication) -> Bool {
        guard let url = other.bundleURL, url.standardizedFileURL != Bundle.main.bundleURL.standardizedFileURL,
              let info = Bundle(url: url)?.infoDictionary, let mine = Bundle.main.infoDictionary else { return false }
        return ["CFBundleShortVersionString", "CFBundleVersion"].allSatisfy { info[$0] as? String == mine[$0] as? String }
    }

    /// Opens `other` again, which makes it show its main window (a reopen event; works with
    /// every version of Tapline), then calls `done`.
    @MainActor
    static func showWindow(of other: NSRunningApplication, then done: @escaping @MainActor @Sendable () -> Void) {
        guard let url = other.bundleURL else {
            other.activate()
            done()
            return
        }
        NSWorkspace.shared.openApplication(at: url, configuration: NSWorkspace.OpenConfiguration()) { _, _ in
            DispatchQueue.main.async { MainActor.assumeIsolated { done() } }
        }
    }

    /// Asks `other` to quit, and makes sure it does: its taps and aggregate device go with it.
    @MainActor
    static func quit(_ other: NSRunningApplication) {
        let pid = other.processIdentifier
        other.terminate()
        DispatchQueue.main.asyncAfter(deadline: .now() + 5) {
            guard let stuck = NSRunningApplication(processIdentifier: pid), !stuck.isTerminated else { return }
            stuck.forceTerminate()
        }
    }
}
