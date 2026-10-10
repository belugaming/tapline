import AppKit

/// Tapline lives in the menu bar. It shows a Dock icon only while the main window is open.
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var observers: [NSObjectProtocol] = []

    func applicationDidFinishLaunching(_ notification: Notification) {
        if !DebugSnapshot.isDevelopmentMode, let other = SingleInstance.otherInstance {
            other.activate()
            NSApp.terminate(nil)
            return
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
    static func showMainWindow() {
        NSApp.setActivationPolicy(.regular)
        NSApp.activate()
        if let window = NSApp.windows.first(where: { $0.identifier?.rawValue == "main" || $0.title == "Tapline" }) {
            window.makeKeyAndOrderFront(nil)
        } else {
            OpenWindowBridge.shared.open?("main")
        }
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
}
