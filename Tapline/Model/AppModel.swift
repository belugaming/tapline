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
    let speakers = VirtualSpeakerLibrary()
    @ObservationIgnored let engine = TapEngine()

    private(set) var status: Status = .off
    private(set) var powerOn: Bool
    private(set) var profiles: [String: DSPSettings] = [:]
    private(set) var appSettings: [String: AppAudioSettings] = [:]
    private(set) var userPresets: [EQPreset] = []
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
    /// Why the chosen Virtual Speakers set could not be loaded.
    private(set) var virtualSpeakersProblem: String?
    var globalShortcutsEnabled: Bool {
        didSet {
            defaults.set(globalShortcutsEnabled, forKey: Keys.shortcuts)
            updateShortcuts()
        }
    }
    /// Plays the bypassed sound at the loudness of the EQ, so that switching the EQ off
    /// compares the sound and not the level (a correction's preamp makes it quieter).
    var levelMatchedBypass: Bool {
        didSet {
            defaults.set(levelMatchedBypass, forKey: Keys.levelMatchedBypass)
            pushSettings()
        }
    }
    /// "Listen as": the device last chosen (remembered across launches) and whether it plays
    /// now (off at every launch). Never part of `DSPSettings`, so presets, A/B and undo never
    /// see it; it plays on top of whatever the current output's EQ does.
    private(set) var simulation: DeviceSimulation?
    private(set) var simulating = false

    @ObservationIgnored private let defaults = UserDefaults.standard
    @ObservationIgnored private var alternate: [String: DSPSettings] = [:]
    @ObservationIgnored private var undoStack: [DSPSettings] = []
    @ObservationIgnored private var redoStack: [DSPSettings] = []
    @ObservationIgnored private var saveScheduled = false
    @ObservationIgnored private var storedProfiles = TolerantJSON()
    @ObservationIgnored private var storedApps = TolerantJSON()
    @ObservationIgnored private var storedPresets = TolerantJSON()
    /// Per device: what its headphone correction was fitted to, for "Listen as".
    @ObservationIgnored private var correctionReferences: [String: CorrectionReference] = [:]
    @ObservationIgnored private var reconcileScheduled = false
    @ObservationIgnored private var meterTimer: Timer?
    @ObservationIgnored private var meterInterval: TimeInterval = 0
    @ObservationIgnored private var lastMeterTick = Date()
    @ObservationIgnored private var analyzerViewers = 0
    @ObservationIgnored private let postAnalyzer = SpectrumAnalyzer(fftSize: 8192, displayPoints: 320)
    @ObservationIgnored private let preAnalyzer = SpectrumAnalyzer(fftSize: 8192, displayPoints: 320)
    @ObservationIgnored private var analyzerBuffer = [Float](repeating: 0, count: 8192)
    @ObservationIgnored private var tickCount = 0
    // Linear-phase EQ: FIRs are designed one at a time on this queue, for the newest curve.
    @ObservationIgnored private let firQueue = DispatchQueue(label: "io.github.belugaming.Tapline.linear-phase", qos: .userInitiated)
    @ObservationIgnored private var firWanted: LinearPhaseEQ.Spec?
    @ObservationIgnored private var firInFlight: LinearPhaseEQ.Spec?
    @ObservationIgnored private var firStarted = Date.distantPast
    @ObservationIgnored private var firRetryScheduled = false
    @ObservationIgnored private var runawaySeconds = 0.0
    @ObservationIgnored private var lastCycles: UInt64 = 0
    @ObservationIgnored private var stalledSeconds = 0.0
    @ObservationIgnored private var lastWatchdogRestart = Date.distantPast
    @ObservationIgnored private var wakeObserver: NSObjectProtocol?
    @ObservationIgnored private var terminateObserver: NSObjectProtocol?
    @ObservationIgnored private var takeoverObserver: NSObjectProtocol?
    @ObservationIgnored private let log = Logger(subsystem: "io.github.belugaming.Tapline", category: "meters")
    @ObservationIgnored private let settingsLog = Logger(subsystem: "io.github.belugaming.Tapline", category: "settings")

    private enum Keys {
        static let power = "powerOn"
        static let profiles = "deviceProfiles"
        static let apps = "appSettings"
        /// Read by the Audio Unit too (see SharedPresets).
        static let presets = SharedPresets.userPresetsKey
        static let showPre = "showPreSpectrum"
        static let tilt = "analyzerTilt"
        static let shortcuts = "globalShortcuts"
        static let levelMatchedBypass = "levelMatchedBypass"
        static let listenAs = SharedPresets.listenAsKey
        static let correctionReferences = "correctionReferences"
    }

    init() {
        powerOn = defaults.bool(forKey: Keys.power)
        showPreSpectrum = defaults.object(forKey: Keys.showPre) as? Bool ?? true
        analyzerTilt = defaults.object(forKey: Keys.tilt) as? Double ?? 3
        globalShortcutsEnabled = defaults.object(forKey: Keys.shortcuts) as? Bool ?? true
        levelMatchedBypass = defaults.object(forKey: Keys.levelMatchedBypass) as? Bool ?? true
        loadStoredSettings()
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
        // ⌥⌘Q, the menu bar's quit, logging out: write what the debounced save has not yet.
        terminateObserver = NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification, object: nil, queue: nil) { [weak self] _ in
            MainActor.assumeIsolated { self?.flushPendingSave() }
        }
        if !DebugSnapshot.isDevelopmentMode { updateShortcuts() }

        if DebugSnapshot.usesDemoData {
            devices.applyDemo()
            processes.applyDemo()
            appSettings = [
                "com.apple.Safari": AppAudioSettings(name: "Safari", bundleID: "com.apple.Safari", volume: 0.65),
                "com.apple.logic10": AppAudioSettings(name: "Logic Pro", bundleID: "com.apple.logic10", bypass: true),
            ]
            meters.appPeaks = ["com.apple.Safari": 0.32]
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
            simulation = SpeakerProfile.phone.simulation
            simulating = true
            meters.fillDemo(frequencies: postAnalyzer.displayFrequencies, settings: demo)
            status = .running
            return
        }
        if DebugSnapshot.isDevelopmentMode { return }
        if SingleInstance.otherInstanceRunning {
            waitForOtherInstanceToQuit()
        } else if powerOn {
            Task { await self.setPower(true) }
        }
    }

    /// A newer build was opened while an older one ran (see AppDelegate): once the old
    /// copy has quit, pick up what it saved last and take over the audio.
    private func waitForOtherInstanceToQuit() {
        takeoverObserver = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didTerminateApplicationNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, !SingleInstance.otherInstanceRunning, let observer = self.takeoverObserver else { return }
                NSWorkspace.shared.notificationCenter.removeObserver(observer)
                self.takeoverObserver = nil
                self.powerOn = self.defaults.bool(forKey: Keys.power)
                self.loadStoredSettings()
                self.log.info("other instance quit, taking over")
                if self.powerOn { Task { await self.setPower(true) } }
            }
        }
    }

    // MARK: - Power & engine

    var currentOutput: AudioDevice? { devices.defaultOutput }
    var currentDeviceUID: String { currentOutput?.uid ?? "none" }

    /// `remember: false` switches for this session only; the next launch starts as the user left it.
    func setPower(_ on: Bool, remember: Bool = true) async {
        if on && DebugSnapshot.isDevelopmentMode { return }
        powerOn = on
        if remember { defaults.set(on, forKey: Keys.power) }
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
        let main = TapSourceSpec(key: TapSourceSpec.mainKey, processes: Array(Set(excludedProcesses)).sorted(), bundleIDs: excludedBundles.sorted(), exclusive: true)
        return [main] + appSpecs
    }

    private func pushSettings() {
        guard engine.isRunning else { return }
        engine.kernel.setSettings(settings, sampleRate: engine.sampleRate, levelMatchedBypass: levelMatchedBypass,
                                  simulation: simulation, simulating: simulating)
        engine.kernel.setSpeakers(settings, layout: engine.speakerLayout, firstChannel: engine.speakerFirstChannel, sampleRate: engine.sampleRate)
        pushVirtualSpeakers()
        pushLinearPhase()
    }

    /// Keeps the kernel's linear-phase FIR in step with the curve. A design takes about 10 ms
    /// at 48 kHz, off the main thread. While a band is dragged, one runs at a time, at most
    /// every 50 ms and always for the newest curve; the kernel crossfades each new FIR in over
    /// 50 ms, so dragging stays smooth.
    private func pushLinearPhase() {
        let phase = settings.filterPhase
        let lengths = FilterPhase.allCases.filter(\.isLinear)
        // The FIRs cover the stereo pair: a surround output keeps minimum phase (the kernel
        // would not play them either).
        guard phase.isLinear, speakerLayout == nil else {
            // The kernel fades back to the stages, then frees the FIRs.
            firWanted = nil
            for length in lengths { engine.kernel.removeLinearPhase(length) }
            return
        }
        let spec = LinearPhaseEQ.Spec(settings: settings, sampleRate: engine.sampleRate)
        if spec == engine.kernel.linearPhaseSpec(phase) {
            firWanted = nil
            // The kernel plays the other length until this one's FIR is there; now it can go.
            for length in lengths where length != phase { engine.kernel.removeLinearPhase(length) }
            return
        }
        guard spec != firInFlight else {
            firWanted = nil
            return
        }
        firWanted = spec
        designNextFIR()
    }

    private func designNextFIR() {
        guard firInFlight == nil, let spec = firWanted else { return }
        let wait = 0.05 - Date().timeIntervalSince(firStarted)
        if wait > 0 {
            guard !firRetryScheduled else { return }
            firRetryScheduled = true
            DispatchQueue.main.asyncAfter(deadline: .now() + wait) { [weak self] in
                MainActor.assumeIsolated {
                    self?.firRetryScheduled = false
                    self?.designNextFIR()
                }
            }
            return
        }
        firWanted = nil
        firInFlight = spec
        firStarted = Date()
        firQueue.async { [weak self] in
            let fir = LinearPhaseEQ.prepare(spec)
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    guard let self else { return }
                    self.firInFlight = nil
                    // Nil only without memory for it: retried with the next change.
                    guard self.engine.isRunning, let fir else { return }
                    // Even when the curve moved on meanwhile, it is closer than the FIR playing.
                    // The kernel refuses one made for a rate it no longer runs at.
                    if self.settings.filterPhase == fir.spec.phase { self.engine.kernel.setLinearPhase(fir) }
                    // Then catch up with the curve as it is now (it may also have gone back).
                    self.pushLinearPhase()
                }
            }
        }
    }

    /// Hands the chosen impulse responses to the kernel when the set or the sample rate
    /// changed. Loaded the first time the effect is switched on, then kept while it is off
    /// so switching it on again is instant.
    private func pushVirtualSpeakers() {
        let fx = settings.effects
        guard fx.virtualSpeakersEnabled || engine.kernel.virtualSpeakersKey != nil else { return }
        let set = speakers.resolve(fx.virtualSpeakers)
        let key = VirtualSpeakerLibrary.key(set.id, sampleRate: engine.sampleRate)
        guard key != engine.kernel.virtualSpeakersKey else { return }
        do {
            let prepared = try speakers.prepared(set, sampleRate: engine.sampleRate)
            engine.kernel.setVirtualSpeakers(prepared.irs, gain: prepared.gain, key: prepared.key)
            virtualSpeakersProblem = nil
        } catch {
            // Remembered under the same key, so it is not retried on every settings change.
            engine.kernel.setVirtualSpeakers(nil, gain: 1, key: key)
            virtualSpeakersProblem = String(localized: "Could not load \(set.name): \(VirtualSpeakerLibrary.message(for: error))")
        }
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

    /// Meters tick 30 times a second while the main window is on screen. Otherwise (only the
    /// menu bar icon, or the window minimized or covered) nothing shows them, and four ticks a
    /// second keep the runaway guard and the watchdog going while waking the Mac less.
    private static let visibleMeterInterval = 1.0 / 30.0
    private static let hiddenMeterInterval = 0.25

    private var wantedMeterInterval: TimeInterval {
        AppDelegate.mainWindowIsOnScreen ? Self.visibleMeterInterval : Self.hiddenMeterInterval
    }

    private func startMeterTimer() {
        guard meterTimer == nil else { return }
        lastMeterTick = Date()
        scheduleMeterTimer(interval: wantedMeterInterval)
    }

    private func scheduleMeterTimer(interval: TimeInterval) {
        meterTimer?.invalidate()
        let timer = Timer(timeInterval: interval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.meterTick() }
        }
        // Lets macOS fire it together with other wakeups.
        timer.tolerance = interval / 10
        RunLoop.main.add(timer, forMode: .common)
        meterTimer = timer
        meterInterval = interval
    }

    private func stopMeterTimer() {
        meterTimer?.invalidate()
        meterTimer = nil
        meters.reset()
    }

    private func meterTick() {
        let interval = wantedMeterInterval
        if interval != meterInterval { scheduleMeterTimer(interval: interval) }
        guard engine.isRunning else { return }
        let now = Date()
        let dt = now.timeIntervalSince(lastMeterTick)
        lastMeterTick = now
        // The kernel keeps the highest peak and counts clipped samples until it is read, so
        // a slower tick misses nothing.
        let keys = engine.sources.map(\.key)
        let reading = engine.kernel.readMeters(sourceCount: keys.count)
        engine.kernel.collectReleasedSets()
        meters.ingest(reading, sourceKeys: keys, dt: dt)
        // At most half a second per tick: a main thread that hung must not add up to a
        // runaway or a stall all at once.
        let seconds = min(dt, 0.5)
        guardAgainstRunaway(reading, seconds: seconds)
        watchdog(reading, seconds: seconds)
        #if DEBUG
        tickCount += 1
        if tickCount % 60 == 0 {
            let sources = zip(keys, reading.sourcePeaks).map { "\($0)=\(String(format: "%.1f", linearToDB(Double($1))))" }.joined(separator: " ")
            log.info("cycles=\(reading.ioCycles) out L=\(String(format: "%.1f", linearToDB(Double(self.meters.peakL))), privacy: .public) R=\(String(format: "%.1f", linearToDB(Double(self.meters.peakR))), privacy: .public) gr=\(String(format: "%.1f", reading.gainReductionDB), privacy: .public) overloads=\(self.engine.overloads.value) sources: \(sources, privacy: .public)")
        }
        #endif

        guard analyzerViewers > 0, interval == Self.visibleMeterInterval else { return }
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

    /// Switches processing off if a tapped stream stays far above full scale (or not a number)
    /// for two seconds: the signature of another tool feeding Tapline's output back into it.
    /// Source peaks are read before the app volume, so the user's own gain (output gain,
    /// preamp, app volume above 100 %) never trips it. Limiter gain reduction alone is no
    /// signature either: loud settings cause plenty of it, and a loop through the limiter
    /// looks just like loud music.
    private func guardAgainstRunaway(_ m: KernelMeters, seconds: Double) {
        let hottestSource = m.sourcePeaks.max() ?? 0
        if hottestSource > 2.5 {
            runawaySeconds += seconds
        } else {
            runawaySeconds = max(0, runawaySeconds - 2 * seconds)
        }
        guard runawaySeconds > 2 else { return }
        runawaySeconds = 0
        log.error("runaway guard tripped: gr=\(m.gainReductionDB) source=\(hottestSource)")
        safetyMessage = String(localized: "Tapline switched itself off because the output kept overloading. Another audio tool may be feeding Tapline's output back into it.")
        // Not remembered: a loop is usually gone by the next launch.
        Task { await setPower(false, remember: false) }
    }

    /// Restarts the engine if the IO callback stops being called (device reset, driver hiccup).
    private func watchdog(_ m: KernelMeters, seconds: Double) {
        if m.ioCycles != lastCycles {
            lastCycles = m.ioCycles
            stalledSeconds = 0
            return
        }
        stalledSeconds += seconds
        guard stalledSeconds > 2, Date().timeIntervalSince(lastWatchdogRestart) > 10 else { return }
        stalledSeconds = 0
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
            .init(id: 4, keyCode: UInt32(kVK_ANSI_L), title: "Listen as") { [weak self] in self?.toggleSimulation() },
        ])
    }

    // MARK: - Listen as

    /// Plays `simulation` from now on (or only chooses it, with `play` false) and remembers it.
    func chooseSimulation(_ simulation: DeviceSimulation, play: Bool = true) {
        self.simulation = simulation
        if play { simulating = true }
        if !DebugSnapshot.isActive, let data = try? JSONEncoder().encode(simulation) {
            defaults.set(data, forKey: Keys.listenAs)
        }
        pushSettings()
    }

    func setSimulating(_ on: Bool) {
        simulating = on && simulation != nil
        pushSettings()
    }

    func toggleSimulation() { setSimulating(!simulating) }

    /// How much quieter than level-matched the simulation plays because the limiter is off and
    /// the make-up gain must leave headroom (see `DeviceSimulation.makeupGainDB(after:)`).
    var simulationLevelShortfallDB: Double {
        guard let simulation, !settings.effects.limiterEnabled else { return 0 }
        return simulation.levelShortfallDB(after: settings, sampleRate: engine.sampleRate, levelMatchedBypass: levelMatchedBypass)
    }

    /// What the current output's headphone correction was fitted to, if Tapline fitted it.
    var correctionReference: CorrectionReference? { correctionReferences[currentDeviceUID] }

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

    // MARK: - Filter phase

    func setFilterPhase(_ phase: FilterPhase) { mutate { $0.filterPhase = phase } }

    /// Whether the current output plays the linear-phase setting: stereo outputs only.
    var playsLinearPhase: Bool { settings.filterPhase.isLinear && speakerLayout == nil }

    /// Why linear phase is off on the current output, if it is a surround layout.
    var linearPhaseUnavailable: String? {
        speakerLayout.map { String(format: String(localized: "Linear phase plays on stereo outputs only, so this %@ speaker layout keeps minimum phase."), $0.name) }
    }

    /// Milliseconds `phase` delays the sound on the current device.
    func linearPhaseLatencyMS(_ phase: FilterPhase) -> Int {
        let rate = engine.isRunning ? engine.sampleRate : (currentOutput?.sampleRate ?? 48_000)
        return Int(LinearPhaseEQ.latencyMilliseconds(sampleRate: rate, phase: phase).rounded())
    }

    // MARK: - Multichannel speakers

    /// The current output's 5.1 … 9.1.6 speaker layout, whose speakers Tapline processes one by one.
    var speakerLayout: SpeakerLayout? { currentOutput?.surroundLayout }

    func setEQOnAllSpeakers(_ on: Bool) { mutate { $0.speakers.eqOnAllSpeakers = on } }

    func setSpeakerCorrection(_ on: Bool) { mutate { $0.speakers.correctionEnabled = on } }

    /// Applies a room correction from the Room page: each speaker's filters, trim and delay.
    func applySpeakerCorrection(_ corrections: [SpeakerCorrection], layout: SpeakerLayout) {
        mutate { s in
            s.speakers.corrections = corrections
            s.speakers.layout = layout
            s.speakers.correctionEnabled = true
        }
    }

    func removeSpeakerCorrection() {
        mutate { s in
            s.speakers.corrections = []
            s.speakers.layout = nil
        }
    }

    // MARK: - Crossfeed and Virtual Speakers (one or the other)

    func setCrossfeed(_ on: Bool) {
        mutate {
            $0.effects.crossfeedEnabled = on
            if on { $0.effects.virtualSpeakersEnabled = false }
        }
    }

    func setVirtualSpeakers(_ on: Bool) {
        mutate {
            $0.effects.virtualSpeakersEnabled = on
            if on { $0.effects.crossfeedEnabled = false }
        }
    }

    func chooseVirtualSpeakers(_ id: String) {
        mutate { $0.effects.virtualSpeakers = id }
    }

    /// The set in use on the current device (the default when an imported one was removed).
    var currentVirtualSpeakers: VirtualSpeakerSet { speakers.resolve(settings.effects.virtualSpeakers) }

    /// Imports HeSuVi / Impulcifer files and switches Virtual Speakers on with them.
    func importVirtualSpeakers(from urls: [URL]) throws {
        let set = try speakers.importFiles(urls)
        mutate {
            $0.effects.virtualSpeakers = set.id
            $0.effects.virtualSpeakersEnabled = true
            $0.effects.crossfeedEnabled = false
        }
    }

    func removeVirtualSpeakers(_ id: String) {
        speakers.remove(id)
        if settings.effects.virtualSpeakers == id {
            mutate { $0.effects.virtualSpeakers = EffectsSettings.defaultVirtualSpeakers }
        } else {
            pushSettings()
        }
    }

    func toggleCompare() {
        let uid = currentDeviceUID
        var other = alternate[uid] ?? settings
        alternate[uid] = settings
        compareSlot = compareSlot == .a ? .b : .a
        // The filter phase belongs to the device, not to a slot: A/B must not jump in time.
        other.filterPhase = settings.filterPhase
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
                s.preampDB = pre.clamped(to: DSPSettings.preampRange)
            } else {
                s.autoPreamp = true
            }
        }
        selectedBandID = nil
        return saveAsPreset ? saveCurrentAsPreset(named: name, source: source) : nil
    }

    /// Applies a generated correction (AutoEQ, squig.link fit…) and saves it as a preset.
    /// `reference` records what a squig.link or own-measurement fit aimed at, for "Listen as".
    @discardableResult
    func applyCorrection(bands: [EQBand], preampDB: Double? = nil, name: String, source: String?, reference: CorrectionReference? = nil) -> EQPreset {
        correctionReferences[currentDeviceUID] = reference
        saveCorrectionReferences()
        mutate { s in
            s.bands = Array(bands.prefix(DSPSettings.maxBands))
            s.tone = ToneSettings()
            s.eqEnabled = true
            if let preampDB {
                s.autoPreamp = false
                s.preampDB = preampDB.clamped(to: DSPSettings.preampRange)
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

    /// Imports an EQ file: Equalizer APO / AutoEQ text, or an RME ADI-2 preset (`.adieqpr`).
    @discardableResult
    func importEQFile(_ text: String, name: String) throws -> EQPreset? {
        guard text.contains("<ADI-2") else { return try importParametricEQ(text, name: name) }
        let parsed = try ADI2Preset.parse(text)
        guard !parsed.bands.isEmpty else { throw ParametricEQFormat.ParseError.noFilters }
        mutate { s in
            s.bands = Array(parsed.bands.prefix(DSPSettings.maxBands))
            s.tone = ToneSettings()
            s.eqEnabled = true
            s.autoPreamp = true
        }
        selectedBandID = nil
        return saveCurrentAsPreset(named: parsed.name ?? name, source: "RME ADI-2")
    }

    /// The current EQ (tone controls included) as an RME ADI-2 preset file.
    /// Left and right get their own bands (Dual EQ) when the EQ has channel-specific bands.
    func exportADI2Preset(name: String) throws -> String {
        let bands = settings.bands.filter(\.isEnabled) + settings.tone.bands.filter(\.isEnabled)
        guard settings.hasChannelSpecificBands else { return try ADI2Preset.export(left: bands, name: name) }
        return try ADI2Preset.export(left: bands.filter { $0.channels.includes(.left) },
                                     right: bands.filter { $0.channels.includes(.right) }, name: name)
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
        storedProfiles = TolerantJSON()
        storedApps = TolerantJSON()
        storedPresets = TolerantJSON()
        alternate = [:]
        undoStack = []
        redoStack = []
        updateUndoFlags()
        selectedBandID = nil
        correctionReferences = [:]
        saveCorrectionReferences()
        simulation = nil
        simulating = false
        defaults.removeObject(forKey: Keys.listenAs)
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

    private func saveCorrectionReferences() {
        guard !DebugSnapshot.isActive, let data = try? JSONEncoder().encode(correctionReferences) else { return }
        defaults.set(data, forKey: Keys.correctionReferences)
    }

    func saveNow() {
        guard !DebugSnapshot.isActive else { return }
        if let data = storedProfiles.encode(profiles) { defaults.set(data, forKey: Keys.profiles) }
        if let data = storedApps.encode(appSettings) { defaults.set(data, forKey: Keys.apps) }
        if let data = storedPresets.encode(userPresets) { defaults.set(data, forKey: Keys.presets) }
    }

    private func flushPendingSave() {
        guard saveScheduled else { return }
        saveScheduled = false
        saveNow()
    }

    /// Reads the stored collections entry by entry: an entry this version cannot read is
    /// kept as it is and written back on save (see `TolerantJSON`), never silently dropped.
    private func loadStoredSettings() {
        storedProfiles = TolerantJSON()
        storedApps = TolerantJSON()
        storedPresets = TolerantJSON()
        profiles = load(Keys.profiles) { storedProfiles.decode([String: DSPSettings].self, from: $0) } ?? [:]
        appSettings = load(Keys.apps) { storedApps.decode([String: AppAudioSettings].self, from: $0) } ?? [:]
        userPresets = load(Keys.presets) { storedPresets.decode([EQPreset].self, from: $0) } ?? []
        // Conveniences: unreadable ones are simply forgotten.
        correctionReferences = defaults.data(forKey: Keys.correctionReferences)
            .flatMap { try? JSONDecoder().decode([String: CorrectionReference].self, from: $0) } ?? [:]
        simulation = defaults.data(forKey: Keys.listenAs).flatMap { try? JSONDecoder().decode(DeviceSimulation.self, from: $0) }
        let unreadable = storedProfiles.unreadableCount + storedApps.unreadableCount + storedPresets.unreadableCount
        if unreadable > 0 { settingsLog.error("kept \(unreadable) stored entries this version cannot read") }
    }

    /// Nil when nothing is stored. Data that is not readable at all (not even the outline of
    /// the collection) is copied to "<key>.backup" before the next save replaces it.
    private func load<T>(_ key: String, _ decode: (Data) -> T?) -> T? {
        guard let data = defaults.data(forKey: key) else { return nil }
        if let values = decode(data) { return values }
        settingsLog.error("\(key, privacy: .public) is unreadable, keeping a copy as \(key, privacy: .public).backup")
        defaults.set(data, forKey: key + ".backup")
        return nil
    }
}
