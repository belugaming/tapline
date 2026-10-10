import AppKit
import Carbon.HIToolbox
import CoreAudio
import Foundation
import Observation
import OSLog
import ServiceManagement
import TaplineCore

@MainActor
@Observable
final class AppModel {
    enum Status: Equatable {
        case off
        case requestingPermission
        case permissionDenied
        case running
        case noOutput
        case failed(String)
    }

    enum CompareSlot: String { case a = "A", b = "B" }

    let devices = AudioDeviceCenter()
    let processes = AudioProcessCenter()
    let meters = LiveMeters()
    @ObservationIgnored let engine = TapEngine()

    private(set) var status: Status = .off
    private(set) var powerOn: Bool
    private(set) var profiles: [String: DSPSettings]
    private(set) var appSettings: [String: AppAudioSettings]
    private(set) var userPresets: [EQPreset]
    private(set) var compareSlot: CompareSlot = .a
    private(set) var canUndo = false
    private(set) var canRedo = false
    var selectedBandID: UUID?
    var showPreSpectrum: Bool { didSet { defaults.set(showPreSpectrum, forKey: Keys.showPre) } }
    var analyzerTilt: Double { didSet { defaults.set(analyzerTilt, forKey: Keys.tilt); postAnalyzer.tiltDBPerOctave = analyzerTilt; preAnalyzer.tiltDBPerOctave = analyzerTilt } }
    var launchAtLogin: Bool {
        get { SMAppService.mainApp.status == .enabled }
        set {
            do {
                if newValue { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            } catch {
                lastError = error.localizedDescription
            }
            launchAtLoginRevision += 1
        }
    }
    private(set) var launchAtLoginRevision = 0
    var lastError: String?
    /// Set when the runaway guard switched processing off.
    var safetyMessage: String?
    var globalShortcutsEnabled: Bool {
        didSet {
            defaults.set(globalShortcutsEnabled, forKey: Keys.shortcuts)
            updateShortcuts()
        }
    }

    @ObservationIgnored private let defaults = UserDefaults.standard
    @ObservationIgnored private var alternate: [String: DSPSettings] = [:]
    @ObservationIgnored private var undoStack: [DSPSettings] = []
    @ObservationIgnored private var redoStack: [DSPSettings] = []
    @ObservationIgnored private var saveScheduled = false
    @ObservationIgnored private var reconcileScheduled = false
    @ObservationIgnored private var meterTimer: Timer?
    @ObservationIgnored private var lastMeterTick = Date()
    @ObservationIgnored private var analyzerViewers = 0
    @ObservationIgnored private let postAnalyzer = SpectrumAnalyzer(fftSize: 8192, displayPoints: 320)
    @ObservationIgnored private let preAnalyzer = SpectrumAnalyzer(fftSize: 8192, displayPoints: 320)
    @ObservationIgnored private var analyzerBuffer = [Float](repeating: 0, count: 8192)
    @ObservationIgnored private var tickCount = 0
    @ObservationIgnored private var runawayTicks = 0
    @ObservationIgnored private var lastCycles: UInt64 = 0
    @ObservationIgnored private var stalledTicks = 0
    @ObservationIgnored private var lastWatchdogRestart = Date.distantPast
    @ObservationIgnored private var wakeObserver: NSObjectProtocol?
    @ObservationIgnored private let log = Logger(subsystem: "io.github.belugaming.Tapline", category: "meters")

    private enum Keys {
        static let power = "powerOn"
        static let profiles = "deviceProfiles"
        static let apps = "appSettings"
        static let presets = "userPresets"
        static let showPre = "showPreSpectrum"
        static let tilt = "analyzerTilt"
        static let shortcuts = "globalShortcuts"
    }

    init() {
        powerOn = defaults.bool(forKey: Keys.power)
        profiles = Self.decode([String: DSPSettings].self, from: defaults.data(forKey: Keys.profiles)) ?? [:]
        appSettings = Self.decode([String: AppAudioSettings].self, from: defaults.data(forKey: Keys.apps)) ?? [:]
        userPresets = Self.decode([EQPreset].self, from: defaults.data(forKey: Keys.presets)) ?? []
        showPreSpectrum = defaults.object(forKey: Keys.showPre) as? Bool ?? true
        analyzerTilt = defaults.object(forKey: Keys.tilt) as? Double ?? 3
        globalShortcutsEnabled = defaults.object(forKey: Keys.shortcuts) as? Bool ?? true
        postAnalyzer.tiltDBPerOctave = analyzerTilt
        preAnalyzer.tiltDBPerOctave = analyzerTilt
        preAnalyzer.decay = 0.9
        meters.spectrumFrequencies = postAnalyzer.displayFrequencies

        devices.onChange = { [weak self] in self?.scheduleReconcile() }
        processes.onChange = { [weak self] in self?.scheduleReconcile() }

        // After sleep the HAL often tears down IO; rebuild the audio path.
        wakeObserver = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
                    MainActor.assumeIsolated {
                        guard let self, self.powerOn, self.status == .running else { return }
                        self.log.info("woke from sleep, restarting engine")
                        self.restartEngine()
                    }
                }
            }
        }
        if !DebugSnapshot.isDevelopmentMode { updateShortcuts() }

        if DebugSnapshot.usesDemoData {
            var demo = DSPSettings.flat
            demo.bands = [
                EQBand(type: .highPass, frequency: 24, q: 0.71),
                EQBand(type: .lowShelf, frequency: 105, gain: 4.2, q: 0.7),
                EQBand(type: .peak, frequency: 132, gain: -3.5, q: 1.01),
                EQBand(type: .peak, frequency: 350, gain: 2.1, q: 1.21),
                EQBand(type: .peak, frequency: 1875, gain: 3.4, q: 1.8),
                EQBand(type: .peak, frequency: 5985, gain: -4.8, q: 3.5),
                EQBand(type: .highShelf, frequency: 10_000, gain: 2.5, q: 0.7),
            ]
            profiles[currentDeviceUID] = demo
            selectedBandID = demo.bands[4].id
            meters.fillDemo(frequencies: postAnalyzer.displayFrequencies, settings: demo)
            status = .running
            return
        }
        if powerOn && !DebugSnapshot.isDevelopmentMode && !SingleInstance.otherInstanceRunning {
            Task { await self.setPower(true) }
        }
    }

    // MARK: - Power & engine

    var currentOutput: AudioDevice? { devices.defaultOutput }
    var currentDeviceUID: String { currentOutput?.uid ?? "none" }

    func setPower(_ on: Bool) async {
        if on && DebugSnapshot.isDevelopmentMode { return }
        powerOn = on
        defaults.set(on, forKey: Keys.power)
        if on { safetyMessage = nil }
        guard on else {
            engine.stop()
            status = .off
            stopMeterTimer()
            return
        }
        log.info("power on: preflight=\(AudioCapturePermission.rawPreflight ?? -1)")
        switch AudioCapturePermission.status {
        case .authorized:
            break
        case .denied:
            status = .permissionDenied
            return
        case .unknown:
            status = .requestingPermission
            let granted = await AudioCapturePermission.request()
            log.info("permission request finished: granted=\(granted) status=\(String(describing: AudioCapturePermission.status), privacy: .public)")
            guard powerOn else { return }
            if !granted && AudioCapturePermission.status == .denied {
                status = .permissionDenied
                return
            }
        }
        // Leave the permission states before wiring up the engine; reconcileEngine skips them.
        status = .off
        reconcileEngine(forceRebuild: true)
    }

    func togglePower() {
        Task { await setPower(!powerOn) }
    }

    /// Re-checks permission after the user changed it in System Settings.
    func retryPermission() {
        Task { await setPower(true) }
    }

    func restartEngine() {
        guard powerOn else { return }
        if status == .requestingPermission || status == .permissionDenied {
            retryPermission()
        } else {
            reconcileEngine(forceRebuild: true)
        }
    }

    private func scheduleReconcile() {
        guard !reconcileScheduled else { return }
        reconcileScheduled = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { [weak self] in
            MainActor.assumeIsolated {
                self?.reconcileScheduled = false
                self?.reconcileEngine()
            }
        }
    }

    private func reconcileEngine(forceRebuild: Bool = false) {
        guard powerOn, status != .requestingPermission, status != .permissionDenied else { return }
        guard let output = currentOutput else {
            engine.stop()
            status = .noOutput
            return
        }
        let specs = tapSpecs(for: output)
        let needsRebuild = forceRebuild
            || !engine.isRunning
            || engine.outputDevice?.routingSignature != output.routingSignature
            || engine.sources.map(\.key) != specs.map(\.key)
        if needsRebuild {
            do {
                try engine.start(output: output, sources: specs)
                status = .running
                pushSettings()
                pushGains()
                startMeterTimer()
            } catch {
                status = .failed(error.localizedDescription)
                stopMeterTimer()
            }
        } else {
            for spec in specs { engine.update(source: spec) }
            pushGains()
        }
    }

    /// Source 0 is the system-wide tap; every app with its own mixer settings gets its own tap.
    private func tapSpecs(for output: AudioDevice) -> [TapSourceSpec] {
        let apps = processes.apps
        let byID = Dictionary(apps.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        var excludedProcesses: [AudioObjectID] = processes.taplineProcessObjectIDs
        var excludedBundles: Set<String> = [Bundle.main.bundleIdentifier ?? "io.github.belugaming.Tapline"]
        var appSpecs: [TapSourceSpec] = []

        for (id, s) in appSettings.sorted(by: { $0.key < $1.key }) {
            let app = byID[id]
            let procs = app?.processObjectIDs ?? []
            let bundles = app?.ownedBundleIDs ?? (s.bundleID.map { [$0] } ?? [])
            guard !procs.isEmpty || !bundles.isEmpty else { continue }
            excludedProcesses += procs
            excludedBundles.formUnion(bundles)
            if !s.bypass {
                appSpecs.append(TapSourceSpec(key: id, processes: procs.sorted(), bundleIDs: bundles, exclusive: false))
            }
        }
        // Leave apps that play only to a different device alone.
        for app in apps where appSettings[app.id] == nil {
            for p in app.processes where !p.outputDevices.isEmpty && !p.outputDevices.contains(output.id) {
                excludedProcesses.append(p.objectID)
            }
        }
        let main = TapSourceSpec(key: TapSourceSpec.mainKey, processes: Array(Set(excludedProcesses)).sorted(), bundleIDs: excludedBundles.sorted(), exclusive: true)
        return [main] + appSpecs
    }

    private func pushSettings() {
        guard engine.isRunning else { return }
        engine.kernel.setSettings(settings, sampleRate: engine.sampleRate)
    }

    private func pushGains() {
        for (i, source) in engine.sources.enumerated() {
            let gain: Float = source.key == TapSourceSpec.mainKey ? 1 : (appSettings[source.key]?.gain ?? 1)
            engine.kernel.setSourceGain(gain, at: i)
        }
    }

    // MARK: - Meters & analyzer

    func analyzerAppeared() { analyzerViewers += 1 }
    func analyzerDisappeared() { analyzerViewers = max(0, analyzerViewers - 1) }

    private func startMeterTimer() {
        guard meterTimer == nil else { return }
        lastMeterTick = Date()
        let timer = Timer(timeInterval: 1.0 / 30.0, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.meterTick() }
        }
        RunLoop.main.add(timer, forMode: .common)
        meterTimer = timer
    }

    private func stopMeterTimer() {
        meterTimer?.invalidate()
        meterTimer = nil
        meters.reset()
    }

    private func meterTick() {
        guard engine.isRunning else { return }
        let now = Date()
        let dt = now.timeIntervalSince(lastMeterTick)
        lastMeterTick = now
        let keys = engine.sources.map(\.key)
        let reading = engine.kernel.readMeters(sourceCount: keys.count)
        meters.ingest(reading, sourceKeys: keys, dt: dt)
        guardAgainstRunaway(reading)
        watchdog(reading)
        #if DEBUG
        tickCount += 1
        if tickCount % 60 == 0 {
            let sources = zip(keys, reading.sourcePeaks).map { "\($0)=\(String(format: "%.1f", linearToDB(Double($1))))" }.joined(separator: " ")
            log.info("cycles=\(reading.ioCycles) out L=\(String(format: "%.1f", linearToDB(Double(self.meters.peakL))), privacy: .public) R=\(String(format: "%.1f", linearToDB(Double(self.meters.peakR))), privacy: .public) gr=\(String(format: "%.1f", reading.gainReductionDB), privacy: .public) overloads=\(self.engine.overloads.value) sources: \(sources, privacy: .public)")
        }
        #endif

        guard analyzerViewers > 0 else { return }
        engine.kernel.copyAnalyzer(post: true, into: &analyzerBuffer)
        meters.spectrumPost = postAnalyzer.process(analyzerBuffer, sampleRate: engine.sampleRate)
        if showPreSpectrum {
            engine.kernel.copyAnalyzer(post: false, into: &analyzerBuffer)
            meters.spectrumPre = preAnalyzer.process(analyzerBuffer, sampleRate: engine.sampleRate)
        } else if !meters.spectrumPre.isEmpty {
            meters.spectrumPre = []
        }
    }

    var overloadCount: Int { engine.overloads.value }

    /// Switches processing off if the output keeps slamming into the limiter or a tapped
    /// stream is far above full scale for two seconds: the signature of a feedback loop.
    private func guardAgainstRunaway(_ m: KernelMeters) {
        let hottestSource = m.sourcePeaks.max() ?? 0
        if m.gainReductionDB > 12 || hottestSource > 2.5 {
            runawayTicks += 1
        } else {
            runawayTicks = max(0, runawayTicks - 2)
        }
        guard runawayTicks > 60 else { return }
        runawayTicks = 0
        log.error("runaway guard tripped: gr=\(m.gainReductionDB) source=\(hottestSource)")
        safetyMessage = String(localized: "Tapline switched itself off because the output kept overloading. Another audio tool may be feeding Tapline's output back into it.")
        Task { await setPower(false) }
    }

    /// Restarts the engine if the IO callback stops being called (device reset, driver hiccup).
    private func watchdog(_ m: KernelMeters) {
        if m.ioCycles != lastCycles {
            lastCycles = m.ioCycles
            stalledTicks = 0
            return
        }
        stalledTicks += 1
        guard stalledTicks > 60, Date().timeIntervalSince(lastWatchdogRestart) > 10 else { return }
        stalledTicks = 0
        lastWatchdogRestart = Date()
        log.error("audio IO stalled, restarting engine")
        restartEngine()
    }

    // MARK: - Global shortcuts

    private func updateShortcuts() {
        guard globalShortcutsEnabled else {
            HotKeyCenter.shared.unregisterAll()
            return
        }
        HotKeyCenter.shared.register([
            .init(id: 1, keyCode: UInt32(kVK_ANSI_P), title: "Turn processing on or off") { [weak self] in self?.togglePower() },
            .init(id: 2, keyCode: UInt32(kVK_ANSI_B), title: "Bypass EQ") { [weak self] in
                guard let self else { return }
                self.setEQEnabled(!self.settings.eqEnabled)
            },
            .init(id: 3, keyCode: UInt32(kVK_ANSI_K), title: "Compare A/B") { [weak self] in self?.toggleCompare() },
        ])
    }

    // MARK: - Device settings

    var settings: DSPSettings {
        profiles[currentDeviceUID] ?? .flat
    }

    func settings(forDeviceUID uid: String) -> DSPSettings {
        profiles[uid] ?? .flat
    }

    /// Snapshot for undo before a continuous edit (e.g. a drag) begins.
    func checkpoint() {
        undoStack.append(settings)
        if undoStack.count > 200 { undoStack.removeFirst() }
        redoStack.removeAll()
        updateUndoFlags()
    }

    func mutate(undoable: Bool = true, _ change: (inout DSPSettings) -> Void) {
        var s = settings
        change(&s)
        guard s != settings else { return }
        if undoable { checkpoint() }
        store(s)
    }

    private func store(_ s: DSPSettings, forDeviceUID uid: String? = nil) {
        profiles[uid ?? currentDeviceUID] = s
        pushSettings()
        scheduleSave()
    }

    func undo() {
        guard let previous = undoStack.popLast() else { return }
        redoStack.append(settings)
        store(previous)
        updateUndoFlags()
    }

    func redo() {
        guard let next = redoStack.popLast() else { return }
        undoStack.append(settings)
        store(next)
        updateUndoFlags()
    }

    private func updateUndoFlags() {
        canUndo = !undoStack.isEmpty
        canRedo = !redoStack.isEmpty
    }

    func setEQEnabled(_ on: Bool) { mutate { $0.eqEnabled = on } }

    func toggleCompare() {
        let uid = currentDeviceUID
        let other = alternate[uid] ?? settings
        alternate[uid] = settings
        compareSlot = compareSlot == .a ? .b : .a
        store(other)
    }

    /// Copies the visible slot into the hidden one so A and B start identical.
    func copyToOtherSlot() {
        alternate[currentDeviceUID] = settings
    }

    // MARK: - Bands

    var selectedBand: EQBand? {
        guard let id = selectedBandID else { return nil }
        return settings.bands.first { $0.id == id }
    }

    @discardableResult
    func addBand(frequency: Double, gain: Double = 0, type: FilterType = .peak) -> UUID? {
        guard settings.bands.count < DSPSettings.maxBands else { return nil }
        let band = EQBand(type: type, frequency: frequency, gain: gain, q: type == .peak ? 1.0 : 0.71).clamped()
        mutate { $0.bands.append(band) }
        selectedBandID = band.id
        return band.id
    }

    func removeBand(_ id: UUID) {
        mutate { $0.bands.removeAll { $0.id == id } }
        if selectedBandID == id { selectedBandID = settings.bands.last?.id }
    }

    func updateBand(_ id: UUID, undoable: Bool = true, _ change: (inout EQBand) -> Void) {
        mutate(undoable: undoable) { s in
            guard let i = s.bands.firstIndex(where: { $0.id == id }) else { return }
            change(&s.bands[i])
            s.bands[i] = s.bands[i].clamped()
        }
    }

    func resetBands() {
        mutate { s in
            s.bands = DSPSettings.defaultBands()
            s.tone = ToneSettings()
            s.preampDB = 0
            s.autoPreamp = true
        }
        selectedBandID = nil
    }

    // MARK: - Presets

    var allPresets: [EQPreset] { BuiltInPresets.all + userPresets }

    var matchingPreset: EQPreset? { allPresets.first { $0.matches(settings) } }

    func apply(_ preset: EQPreset) {
        mutate { preset.apply(to: &$0) }
        selectedBandID = nil
    }

    @discardableResult
    func saveCurrentAsPreset(named name: String, source: String? = nil, includeEffects: Bool = false) -> EQPreset {
        let preset = EQPreset(name: name, from: settings, source: source, includeEffects: includeEffects)
        userPresets.append(preset)
        scheduleSave()
        return preset
    }

    func renamePreset(_ id: UUID, to name: String) {
        guard let i = userPresets.firstIndex(where: { $0.id == id }) else { return }
        userPresets[i].name = name
        scheduleSave()
    }

    func deletePreset(_ id: UUID) {
        userPresets.removeAll { $0.id == id }
        scheduleSave()
    }

    /// Imports an Equalizer APO / AutoEQ text, applies it, and saves it as a preset.
    @discardableResult
    func importParametricEQ(_ text: String, name: String, source: String? = nil, saveAsPreset: Bool = true) throws -> EQPreset? {
        let parsed = try ParametricEQFormat.parse(text)
        mutate { s in
            s.bands = Array(parsed.bands.prefix(DSPSettings.maxBands))
            s.tone = ToneSettings()
            s.eqEnabled = true
            if let pre = parsed.preampDB {
                s.autoPreamp = false
                s.preampDB = pre
            } else {
                s.autoPreamp = true
            }
        }
        selectedBandID = nil
        return saveAsPreset ? saveCurrentAsPreset(named: name, source: source) : nil
    }

    /// Applies a generated correction (AutoEQ, squig.link fit…) and saves it as a preset.
    @discardableResult
    func applyCorrection(bands: [EQBand], preampDB: Double? = nil, name: String, source: String?) -> EQPreset {
        mutate { s in
            s.bands = Array(bands.prefix(DSPSettings.maxBands))
            s.tone = ToneSettings()
            s.eqEnabled = true
            if let preampDB {
                s.autoPreamp = false
                s.preampDB = preampDB
            } else {
                s.autoPreamp = true
            }
        }
        selectedBandID = nil
        if let existing = userPresets.firstIndex(where: { $0.name == name }) {
            var updated = EQPreset(name: name, from: settings, source: source)
            updated.id = userPresets[existing].id
            userPresets[existing] = updated
            scheduleSave()
            return userPresets[existing]
        }
        return saveCurrentAsPreset(named: name, source: source)
    }

    func exportParametricEQ() -> String {
        let s = settings
        return ParametricEQFormat.export(bands: s.bands, preampDB: s.effectivePreampDB(sampleRate: engine.sampleRate))
    }

    // MARK: - Mixer

    func appSetting(for app: AudioApp) -> AppAudioSettings {
        appSettings[app.id] ?? AppAudioSettings(name: app.name, bundleID: app.bundleID)
    }

    func setVolume(_ volume: Double, for app: AudioApp) {
        updateApp(app) { $0.volume = volume.clamped(to: 0...2) }
    }

    func setMuted(_ muted: Bool, for app: AudioApp) {
        updateApp(app) { $0.muted = muted }
    }

    func setBypass(_ bypass: Bool, for app: AudioApp) {
        updateApp(app) { $0.bypass = bypass }
    }

    func resetApp(_ app: AudioApp) {
        appSettings[app.id] = nil
        scheduleSave()
        reconcileEngine()
    }

    private func updateApp(_ app: AudioApp, _ change: (inout AppAudioSettings) -> Void) {
        let isNew = appSettings[app.id] == nil
        var s = appSetting(for: app)
        let wasBypass = s.bypass
        change(&s)
        s.name = app.name
        s.bundleID = app.bundleID ?? s.bundleID
        appSettings[app.id] = s
        scheduleSave()
        if isNew || wasBypass != s.bypass {
            reconcileEngine()
        } else {
            pushGains()
        }
    }

    func resetEverything() {
        profiles = [:]
        appSettings = [:]
        userPresets = []
        alternate = [:]
        undoStack = []
        redoStack = []
        updateUndoFlags()
        selectedBandID = nil
        saveNow()
        reconcileEngine(forceRebuild: true)
    }

    // MARK: - Persistence

    private func scheduleSave() {
        guard !saveScheduled else { return }
        saveScheduled = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in
            MainActor.assumeIsolated {
                self?.saveScheduled = false
                self?.saveNow()
            }
        }
    }

    func saveNow() {
        guard !DebugSnapshot.isActive else { return }
        let encoder = JSONEncoder()
        if let data = try? encoder.encode(profiles) { defaults.set(data, forKey: Keys.profiles) }
        if let data = try? encoder.encode(appSettings) { defaults.set(data, forKey: Keys.apps) }
        if let data = try? encoder.encode(userPresets) { defaults.set(data, forKey: Keys.presets) }
    }

    private static func decode<T: Decodable>(_ type: T.Type, from data: Data?) -> T? {
        guard let data else { return nil }
        return try? JSONDecoder().decode(type, from: data)
    }
}
