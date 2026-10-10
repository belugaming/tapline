import AppKit
import CoreAudio
import Foundation
import Observation

/// One CoreAudio process object (a process that has connected to the audio server).
struct AudioProcess: Hashable, Sendable {
    let objectID: AudioObjectID
    let pid: pid_t
    let bundleID: String?
    let isRunningOutput: Bool
    let outputDevices: [AudioObjectID]
}

/// Processes grouped under the app the user recognises (Chrome's audio helper
/// belongs to Chrome, Safari's WebKit GPU process belongs to Safari, ...).
struct AudioApp: Identifiable, Hashable, Sendable {
    let id: String
    let name: String
    let bundleID: String?
    let pid: pid_t
    let isRegularApp: Bool
    var processes: [AudioProcess]

    var isPlaying: Bool { processes.contains(where: \.isRunningOutput) }
    var processObjectIDs: [AudioObjectID] { processes.map(\.objectID) }
    var outputDevices: Set<AudioObjectID> { Set(processes.flatMap(\.outputDevices)) }

    /// Bundle IDs that belong only to this app (its own and its helpers'), so the
    /// tap can follow the app across relaunches. Shared system helpers such as
    /// com.apple.WebKit.GPU are tapped by process object instead.
    var ownedBundleIDs: [String] {
        guard let bundleID else { return [] }
        let ids = Set(processes.compactMap(\.bundleID).filter { $0 == bundleID || $0.hasPrefix(bundleID + ".") })
        return Array(ids.union([bundleID])).sorted()
    }
}

@MainActor
@Observable
final class AudioProcessCenter {
    private(set) var apps: [AudioApp] = []
    private(set) var selfProcessObjectID: AudioObjectID = 0
    /// This process and any other running copy of Tapline. Always excluded from taps,
    /// otherwise two copies would capture each other's output and feed back.
    private(set) var taplineProcessObjectIDs: [AudioObjectID] = []
    /// Rogue Amoeba's audio daemon is running (SoundSource, Audio Hijack...).
    private(set) var conflictingAppRunning = false

    /// Called after the process list or any process' running state / devices changed.
    @ObservationIgnored var onChange: (@MainActor () -> Void)?

    @ObservationIgnored private var listListener: PropertyListener?
    @ObservationIgnored private var processListeners: [AudioObjectID: PropertyListener] = [:]
    @ObservationIgnored private var icons: [String: NSImage] = [:]
    @ObservationIgnored private var refreshScheduled = false

    private typealias ResponsibleFn = @convention(c) (pid_t) -> pid_t
    private static let responsibleFn: ResponsibleFn? = {
        guard let sym = dlsym(UnsafeMutableRawPointer(bitPattern: -2), "responsibility_get_pid_responsible_for_pid") else { return nil }
        return unsafeBitCast(sym, to: ResponsibleFn.self)
    }()

    init() {
        refresh()
        listListener = PropertyListener(objectID: CA.system, selectors: [kAudioHardwarePropertyProcessObjectList]) { [weak self] in
            self?.scheduleRefresh()
        }
    }

    func icon(for app: AudioApp) -> NSImage? { icons[app.id] }

    private func scheduleRefresh() {
        guard !refreshScheduled else { return }
        refreshScheduled = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) { [weak self] in
            MainActor.assumeIsolated {
                self?.refreshScheduled = false
                self?.refresh()
            }
        }
    }

    @ObservationIgnored private var demoMode = false

    /// A few well-known apps instead of the user's real ones (snapshot mode only).
    func applyDemo() {
        demoMode = true
        let entries: [(id: String, fallback: String, playing: Bool)] = [
            ("com.apple.Music", "Music", true),
            ("com.apple.Safari", "Safari", true),
            ("com.apple.podcasts", "Podcasts", false),
            ("com.apple.logic10", "Logic Pro", false),
        ]
        apps = entries.enumerated().map { i, e in
            var name = e.fallback
            let byName = (try? FileManager.default.contentsOfDirectory(atPath: "/Applications"))?
                .first { $0.hasPrefix(e.fallback) && $0.hasSuffix(".app") }
                .map { URL(fileURLWithPath: "/Applications/" + $0) }
            if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: e.id) ?? byName {
                icons[e.id] = NSWorkspace.shared.icon(forFile: url.path)
                name = FileManager.default.displayName(atPath: url.path).replacingOccurrences(of: ".app", with: "")
                if e.id == "com.apple.logic10" { name = "Logic Pro" }
            }
            let process = AudioProcess(objectID: AudioObjectID(9000 + i), pid: pid_t(90_000 + i), bundleID: e.id, isRunningOutput: e.playing, outputDevices: [])
            return AudioApp(id: e.id, name: name, bundleID: e.id, pid: process.pid, isRegularApp: true, processes: [process])
        }
        conflictingAppRunning = false
    }

    func refresh() {
        guard !demoMode else { return }
        let ids = CA.readArray(CA.system, kAudioHardwarePropertyProcessObjectList, of: AudioObjectID.self)
        let myPID = getpid()
        let myBundleID = Bundle.main.bundleIdentifier ?? "io.github.belugaming.Tapline"
        var processes: [AudioProcess] = []
        var taplines: [AudioObjectID] = []
        for id in ids {
            let pid = CA.read(id, kAudioProcessPropertyPID, default: pid_t(-1))
            guard pid > 0 else { continue }
            if pid == myPID {
                selfProcessObjectID = id
                taplines.append(id)
                continue
            }
            if CA.readString(id, kAudioProcessPropertyBundleID) == myBundleID {
                taplines.append(id)
                continue
            }
            processes.append(AudioProcess(
                objectID: id,
                pid: pid,
                bundleID: CA.readString(id, kAudioProcessPropertyBundleID).flatMap { $0.isEmpty ? nil : $0 },
                isRunningOutput: CA.read(id, kAudioProcessPropertyIsRunningOutput, default: UInt32(0)) != 0,
                outputDevices: CA.readArray(id, kAudioProcessPropertyDevices, kAudioObjectPropertyScopeOutput, of: AudioObjectID.self)
            ))
        }
        if selfProcessObjectID == 0 {
            selfProcessObjectID = CA.read(CA.system, kAudioHardwarePropertyTranslatePIDToProcessObject, default: AudioObjectID(0))
            if selfProcessObjectID != 0 { taplines.append(selfProcessObjectID) }
        }
        taplineProcessObjectIDs = taplines
        // Rogue Amoeba's ARK daemon stays loaded after SoundSource quits; it only
        // conflicts while it is actually playing audio or one of its apps is open.
        let conflictingApps: Set<String> = ["com.rogueamoeba.soundsource", "com.rogueamoeba.audiohijack", "com.rogueamoeba.AudioHijack"]
        let appOpen = NSWorkspace.shared.runningApplications.contains { conflictingApps.contains($0.bundleIdentifier ?? "") }
        let daemonActive = processes.contains { $0.bundleID == "com.rogueamoeba.arkaudiod" && $0.isRunningOutput }
        conflictingAppRunning = !DebugSnapshot.isActive && (appOpen || daemonActive)

        // Listen to running state / device changes of every process.
        let live = Set(processes.map(\.objectID))
        for id in processListeners.keys where !live.contains(id) { processListeners[id] = nil }
        for p in processes where processListeners[p.objectID] == nil {
            processListeners[p.objectID] = PropertyListener(objectID: p.objectID, addresses: [
                CA.address(kAudioProcessPropertyIsRunningOutput),
                CA.address(kAudioProcessPropertyDevices, kAudioObjectPropertyScopeOutput),
            ]) { [weak self] in
                self?.scheduleRefresh()
            }
        }

        var groups: [String: AudioApp] = [:]
        for p in processes {
            let owner = owningApp(for: p)
            if var existing = groups[owner.id] {
                existing.processes.append(p)
                groups[owner.id] = existing
            } else {
                var app = owner
                app.processes = [p]
                groups[owner.id] = app
            }
        }
        let sorted = groups.values.sorted {
            if $0.isPlaying != $1.isPlaying { return $0.isPlaying }
            return $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
        }
        if sorted != apps {
            apps = sorted
            onChange?()
        }
    }

    private func owningApp(for p: AudioProcess) -> AudioApp {
        var candidates: [pid_t] = []
        if let responsible = Self.responsibleFn?(p.pid), responsible > 0, responsible != p.pid {
            candidates.append(responsible)
        }
        candidates.append(p.pid)

        for pid in candidates {
            guard let running = NSRunningApplication(processIdentifier: pid),
                  running.activationPolicy != .prohibited || pid == p.pid,
                  let name = running.localizedName else { continue }
            let id = running.bundleIdentifier ?? "pid:\(pid)"
            if icons[id] == nil, let icon = running.icon { icons[id] = icon }
            return AudioApp(id: id, name: name, bundleID: running.bundleIdentifier, pid: pid,
                            isRegularApp: running.activationPolicy == .regular, processes: [])
        }

        let fallbackName: String = {
            if let b = p.bundleID { return b.split(separator: ".").last.map(String.init) ?? b }
            var buffer = [CChar](repeating: 0, count: 256)
            proc_name(p.pid, &buffer, UInt32(buffer.count))
            let name = String(decoding: buffer.prefix { $0 != 0 }.map { UInt8(bitPattern: $0) }, as: UTF8.self)
            return name.isEmpty ? "PID \(p.pid)" : name
        }()
        let id = p.bundleID ?? "pid:\(p.pid)"
        return AudioApp(id: id, name: fallbackName, bundleID: p.bundleID, pid: p.pid, isRegularApp: false, processes: [])
    }
}
