import AppKit
import SwiftUI
import TaplineCore

/// Development helper: `TAPLINE_SNAPSHOT_DIR=/path open Tapline.app` renders every
/// page of the main window into PNG files and quits. Used for README screenshots
/// and visual checks without screen-recording permissions.
@MainActor
enum DebugSnapshot {
    static var directory: URL? {
        ProcessInfo.processInfo.environment["TAPLINE_SNAPSHOT_DIR"].map { URL(fileURLWithPath: $0, isDirectory: true) }
    }

    static var isActive: Bool { directory != nil }

    /// Snapshot runs switch pages through their own key so a running copy of Tapline,
    /// which shares the preferences domain, is not affected.
    nonisolated static var pageKey: String { key("page") }

    /// Preference keys used by views; namespaced during snapshot runs.
    nonisolated static func key(_ name: String) -> String {
        ProcessInfo.processInfo.environment["TAPLINE_SNAPSHOT_DIR"] != nil ? "snapshot." + name : name
    }

    /// Snapshot or self-test runs: these never touch real audio.
    static var isDevelopmentMode: Bool {
        isActive || ProcessInfo.processInfo.environment["TAPLINE_SELFTEST_SQUIG"] != nil
    }

    /// TAPLINE_SNAPSHOT_REAL=1 skips the demo curve and spectrum.
    static var usesDemoData: Bool { isActive && ProcessInfo.processInfo.environment["TAPLINE_SNAPSHOT_REAL"] == nil }

    /// TAPLINE_SNAPSHOT_SQUIG="HD 600" pre-fills the squig.link search and selects the first hit.
    static var squigQuery: String? { isActive ? ProcessInfo.processInfo.environment["TAPLINE_SNAPSHOT_SQUIG"] : nil }

    /// TAPLINE_SNAPSHOT_SCROLL=correct scrolls long pages to that section before capturing.
    static var scrollAnchor: String? { isActive ? ProcessInfo.processInfo.environment["TAPLINE_SNAPSHOT_SCROLL"] : nil }

    /// TAPLINE_SNAPSHOT_SCALE=2 renders at twice the resolution even on a 1x display (CI runners).
    static var scale: CGFloat? {
        ProcessInfo.processInfo.environment["TAPLINE_SNAPSHOT_SCALE"].flatMap { Double($0) }.map { CGFloat($0) }
    }

    /// TAPLINE_SNAPSHOT_SIZE=900x600 resizes the window before capturing.
    static var windowSize: CGSize? {
        guard let s = ProcessInfo.processInfo.environment["TAPLINE_SNAPSHOT_SIZE"] else { return nil }
        let parts = s.split(separator: "x").compactMap { Double($0) }
        return parts.count == 2 ? CGSize(width: parts[0], height: parts[1]) : nil
    }

    static func run() {
        guard let directory else { return }
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let pages = Page.allCases
        var index = 0

        func step() {
            guard index < pages.count else {
                captureMenuPanel(in: directory)
                NSApp.terminate(nil)
                return
            }
            let page = pages[index]
            UserDefaults.standard.set(page.rawValue, forKey: pageKey)
            let wait = page == .assistant && squigQuery != nil ? 12.0 : 1.2
            DispatchQueue.main.asyncAfter(deadline: .now() + wait) {
                MainActor.assumeIsolated {
                    capture(name: "\(index)-\(page.rawValue)", in: directory)
                    index += 1
                    step()
                }
            }
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
            MainActor.assumeIsolated {
                if let size = windowSize, let window = NSApp.windows.first(where: { $0.isVisible && $0.canBecomeMain }) {
                    window.setContentSize(size)
                }
                step()
            }
        }
    }

    /// TAPLINE_SELFTEST_SQUIG="query;query" runs the squig.link pipeline headless and prints the fits.
    static func runSquigSelfTest(_ service: SquigService) {
        guard let spec = ProcessInfo.processInfo.environment["TAPLINE_SELFTEST_SQUIG"] else { return }
        func print(_ line: String) {
            FileHandle.standardError.write(Data((line + "\n").utf8))
        }
        Task { @MainActor in
            let start = Date()
            await service.loadAll()
            print("SELFTEST databases=\(service.databases.count) phones=\(service.phones.count) in \(Int(Date().timeIntervalSince(start)))s error=\(service.errorMessage ?? "-")")
            for query in spec.split(separator: ";").map(String.init) {
                let hits = service.search(query, kinds: Set(SquigService.Kind.allCases))
                print("SELFTEST '\(query)': \(hits.count) hits")
                for phone in hits.prefix(4) {
                    var targetDB = phone.database
                    var targets = await service.targets(for: targetDB)
                    if targets.isEmpty, let fallback = await service.fallbackTargetDatabase(for: targetDB.kind) {
                        targetDB = fallback
                        targets = await service.targets(for: fallback)
                    }
                    let targetName = targets.first { $0.localizedCaseInsensitiveContains("harman") } ?? targets.first
                    do {
                        let m = try await service.measurement(for: phone)
                        guard let targetName else { print("  \(phone.displayName) @ \(phone.database.siteName): no targets"); continue }
                        let t = try await service.target(named: targetName, in: targetDB)
                        let r = EQFitter.fit(measurement: m, target: t)
                        print("  \(phone.displayName) @ \(phone.database.siteName) [\(phone.database.kind.rawValue)] target='\(targetName)'@\(targetDB.siteName) bands=\(r.bands.count) dev \(String(format: "%.1f→%.1f", r.errorBeforeDB, r.errorAfterDB))")
                    } catch {
                        print("  \(phone.displayName) @ \(phone.database.siteName): \(error.localizedDescription) (targets: \(targets.prefix(3)))")
                    }
                }
            }
            NSApp.terminate(nil)
        }
    }

    /// Set by MainView so the menu bar panel can be rendered with the live model.
    static var makeMenuPanel: (() -> NSView)?

    private static func captureMenuPanel(in directory: URL) {
        guard let content = makeMenuPanel?() else { return }
        let size = content.fittingSize
        let view = NSView(frame: CGRect(origin: .zero, size: size))
        view.wantsLayer = true
        view.layer?.backgroundColor = NSColor(white: 0.16, alpha: 1).cgColor
        view.appearance = NSAppearance(named: .darkAqua)
        content.frame = view.bounds
        view.addSubview(content)
        let window = NSWindow(contentRect: CGRect(origin: .zero, size: size), styleMask: [.borderless], backing: .buffered, defer: false)
        window.appearance = NSAppearance(named: .darkAqua)
        window.contentView = view
        window.alphaValue = 0.01
        window.orderFront(nil)
        view.layoutSubtreeIfNeeded()
        view.display()
        guard let rep = bitmap(for: view) else { return }
        view.cacheDisplay(in: view.bounds, to: rep)
        if let data = rep.representation(using: .png, properties: [:]) {
            try? data.write(to: directory.appendingPathComponent("menubar.png"))
        }
        window.orderOut(nil)
    }

    private static func capture(name: String, in directory: URL) {
        guard let window = NSApp.windows.first(where: { $0.isVisible && $0.canBecomeMain }),
              let view = window.contentView?.superview ?? window.contentView,
              let rep = bitmap(for: view) else { return }
        view.cacheDisplay(in: view.bounds, to: rep)
        guard let data = rep.representation(using: .png, properties: [:]) else { return }
        try? data.write(to: directory.appendingPathComponent("\(name).png"))
    }

    /// The view's own backing resolution, or `scale` when that is higher.
    private static func bitmap(for view: NSView) -> NSBitmapImageRep? {
        let backing = view.window?.backingScaleFactor ?? 1
        guard let scale, scale > backing else { return view.bitmapImageRepForCachingDisplay(in: view.bounds) }
        let size = view.bounds.size
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int((size.width * scale).rounded()), pixelsHigh: Int((size.height * scale).rounded()),
                                   bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                   colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)
        rep?.size = size
        return rep
    }
}
