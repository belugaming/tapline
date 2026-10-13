import Foundation
import OSLog
import Synchronization
import TaplineCore
import TaplineDSP

/// What the view and the Audio Unit show of the engine.
struct EngineSnapshot: Sendable, Equatable {
    var state: AudioUnitState
    /// Samples the kernel delays the sound at `sampleRate`: what the host compensates.
    var latency: Int
    var sampleRate: Double
    /// Seconds the sound goes on after the input stops: Virtual Speakers' room, the FIR's second half.
    var tail: Double
    /// Why the chosen Virtual Speakers could not be loaded.
    var problem: String?

    var latencySeconds: Double { Double(latency) / sampleRate }
}

/// The Virtual Speakers sets built into the extension: the app's two SADIE II sets, same ids.
enum BundledSpeakers {
    struct Set: Identifiable, Sendable {
        let id: String
        let resource: String
        let name: LocalizedStringResource
    }

    static let all = [
        Set(id: EffectsSettings.defaultVirtualSpeakers, resource: "SADIE-II-KU100-Room", name: "Studio room (KU100)"),
        Set(id: "sadie-ku100", resource: "SADIE-II-KU100", name: "Anechoic (KU100)"),
    ]

    /// The set an id stands for; an unknown one (an import in the app) plays the default.
    static func resolve(_ id: String) -> Set { all.first { $0.id == id } ?? all[0] }

    static func load(_ set: Set) throws -> BinauralIRs {
        guard let url = Bundle(for: AudioUnitEngine.self).url(forResource: set.resource, withExtension: "wav") else {
            throw CocoaError(.fileNoSuchFile)
        }
        return try BinauralIRs(wav: WAVFile.parse(Data(contentsOf: url))).trimmed()
    }
}

/// The Audio Unit's side off the render thread: its state, and the kernel kept in step with it.
/// Whatever writes to the kernel runs on one serial queue, as the kernel takes its settings
/// from one non-realtime thread at a time. The state itself is behind a lock, so the host, the
/// view and the queue can all read it.
final class AudioUnitEngine: @unchecked Sendable {
    /// Who changed the state.
    enum Origin: Sendable {
        /// A parameter, set by the host, its automation or the view through the host.
        case host
        /// The view, outside the parameters (the preset, the Virtual Speakers set…).
        case view
        /// The host restored a saved state or chose a preset.
        case restore
        /// The engine itself: the latency or tail changed with the sample rate.
        case engine
    }

    let kernel = DSPKernel()
    let inbox = ParameterInbox()
    /// The app's preferences, read-only (see SharedPresets and TaplineAU.entitlements).
    let appDefaults: UserDefaults?

    private let current: Mutex<EngineSnapshot>
    private let observers = Mutex<[UUID: @Sendable (EngineSnapshot, Origin) -> Void]>([:])
    private let queue = DispatchQueue(label: "io.github.belugaming.Tapline.AudioUnit", qos: .userInitiated)
    private let firQueue = DispatchQueue(label: "io.github.belugaming.Tapline.AudioUnit.linear-phase", qos: .userInitiated)
    private let log = Logger(subsystem: "io.github.belugaming.Tapline", category: "audio-unit")

    // Only on `queue`:
    private var sampleRate = 48_000.0
    private var rendering = false
    private var timer: DispatchSourceTimer?
    private var pushed: AudioUnitState?
    private var loadedSpeakers: [String: BinauralIRs] = [:]
    private var speakersDuration = 0.0
    private var firWanted: LinearPhaseEQ.Spec?
    private var firInFlight: LinearPhaseEQ.Spec?
    private var firStarted = Date.distantPast
    private var firRetryScheduled = false

    init(state: AudioUnitState = AudioUnitState(), appDefaults: UserDefaults? = UserDefaults(suiteName: SharedPresets.domain)) {
        self.appDefaults = appDefaults
        current = Mutex(EngineSnapshot(state: state, latency: 0, sampleRate: 48_000, tail: 0))
        queue.sync { refreshTiming() }
    }

    deinit {
        timer?.cancel()
    }

    var snapshot: EngineSnapshot { current.withLock { $0 } }
    var state: AudioUnitState { current.withLock { $0.state } }

    // MARK: - Observers

    /// `handler` runs on the main thread after every change, once the kernel has it.
    func observe(_ handler: @escaping @Sendable (EngineSnapshot, Origin) -> Void) -> UUID {
        let id = UUID()
        observers.withLock { $0[id] = handler }
        return id
    }

    func removeObserver(_ id: UUID) {
        _ = observers.withLock { $0.removeValue(forKey: id) }
    }

    // MARK: - Changes

    /// Changes the state; the kernel follows on the engine's queue, then the observers.
    func update(from origin: Origin, _ change: (inout AudioUnitState) -> Void) {
        let changed = current.withLock { snapshot -> Bool in
            let before = snapshot.state
            change(&snapshot.state)
            return snapshot.state != before
        }
        if changed { queue.async { self.apply(origin) } }
    }

    /// A parameter from the host (or the view through the host). Changes nothing when the
    /// parameter already has that value.
    func setParameter(_ address: UInt64, _ value: Float, from origin: Origin = .host) {
        guard let parameter = PluginParameter(rawValue: address), value.isFinite else { return }
        let headphones = parameter == .listenAs && value.rounded() == PluginParameter.listenAsHeadphones ? appHeadphones() : nil
        let changed = current.withLock { snapshot -> Bool in
            var next = snapshot.state
            next.set(parameter, to: value, headphones: headphones)
            guard next.value(of: parameter) != snapshot.state.value(of: parameter) else { return false }
            snapshot.state = next
            return true
        }
        if changed { queue.async { self.apply(origin) } }
    }

    func parameterValue(_ address: UInt64) -> Float {
        guard let parameter = PluginParameter(rawValue: address) else { return 0 }
        return state.value(of: parameter)
    }

    /// A state saved by the host.
    func restore(_ state: AudioUnitState) {
        update(from: .restore) { $0 = state }
    }

    // MARK: - Presets

    /// The user's presets from the app, headphone corrections included.
    func userPresets() -> [EQPreset] {
        appDefaults.map(SharedPresets.userPresets(in:)) ?? []
    }

    /// The presets to choose from: the built-in ones, then the user's.
    func presets() -> [EQPreset] { BuiltInPresets.all + userPresets() }

    /// The headphones last chosen for "Listen as" in the app, if any.
    func appHeadphones() -> DeviceSimulation? {
        appDefaults.flatMap(SharedPresets.listenAsHeadphones(in:))
    }

    // MARK: - Rendering

    /// The host allocated its render resources: the kernel starts afresh at `sampleRate`.
    func start(sampleRate: Double) {
        queue.sync {
            self.sampleRate = sampleRate
            kernel.configure(sampleRate: sampleRate, layout: KernelLayout(sourceBufferIndices: [], leftBuffer: -1, leftChannel: 0,
                                                                            rightBuffer: -1, rightChannel: 1))
            rendering = true
            pushed = nil
            push()
            refreshTiming()
            startTimer()
            log.info("rendering at \(Int(sampleRate)) Hz, latency \(self.snapshot.latency) samples")
        }
        notify(.engine)
    }

    /// The host released its render resources.
    func stop() {
        queue.sync {
            rendering = false
            timer?.cancel()
            timer = nil
        }
    }

    private func apply(_ origin: Origin) {
        push()
        refreshTiming()
        notify(origin)
    }

    private func notify(_ origin: Origin) {
        let snapshot = self.snapshot
        let handlers = observers.withLock { Array($0.values) }
        guard !handlers.isEmpty else { return }
        DispatchQueue.main.async {
            for handler in handlers { handler(snapshot, origin) }
        }
    }

    private func refreshTiming() {
        let state = self.state
        let latency = kernel.latency(virtualSpeakers: state.virtualSpeakersEnabled, phase: state.filterPhase)
        let fir = state.filterPhase.isLinear ? Double(LinearPhaseEQ.taps(sampleRate: sampleRate, phase: state.filterPhase) / 2) / sampleRate : 0
        let tail = (state.virtualSpeakersEnabled ? speakersDuration : 0) + fir
        let rate = sampleRate
        current.withLock {
            $0.latency = latency
            $0.sampleRate = rate
            $0.tail = tail
        }
    }

    /// Picks up automation from the render thread, frees what the kernel is done with.
    private func startTimer() {
        timer?.cancel()
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 0.025, repeating: 0.025, leeway: .milliseconds(5))
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            for (parameter, value) in self.inbox.take() { self.setParameter(parameter.rawValue, value) }
            self.kernel.collectReleasedSets()
        }
        timer.resume()
        self.timer = timer
    }

    private func push() {
        guard rendering else { return }
        let state = self.state
        if state != pushed {
            var params = state.kernelParams(sampleRate: sampleRate)
            tl_kernel_set_params(kernel.pointer, &params)
            pushed = state
        }
        pushVirtualSpeakers(state)
        pushLinearPhase(state)
    }

    /// Hands the chosen set to the kernel when the set or the rate changed. Loaded the first
    /// time the effect is switched on, then kept while it is off, so switching back is instant.
    private func pushVirtualSpeakers(_ state: AudioUnitState) {
        guard state.virtualSpeakersEnabled || kernel.virtualSpeakersKey != nil else { return }
        let set = BundledSpeakers.resolve(state.virtualSpeakers)
        let key = "\(set.id)@\(sampleRate)"
        guard key != kernel.virtualSpeakersKey else { return }
        do {
            let raw = try loadedSpeakers[set.id] ?? BundledSpeakers.load(set)
            loadedSpeakers[set.id] = raw
            let irs = raw.prepared(sampleRate: sampleRate)
            kernel.setVirtualSpeakers(irs, gain: irs.loudnessGain, key: key)
            speakersDuration = irs.duration
            current.withLock { $0.problem = nil }
        } catch {
            // Remembered under the same key, so it is not retried on every change.
            kernel.setVirtualSpeakers(nil, gain: 1, key: key)
            speakersDuration = 0
            log.error("could not load \(set.resource, privacy: .public): \(error.localizedDescription, privacy: .public)")
            let name = String(localized: set.name)
            current.withLock { $0.problem = String(localized: "Could not load \(name).") }
        }
    }

    /// Keeps the kernel's linear-phase FIR in step with the curve, as the app does: one design
    /// at a time off the queue, at most every 50 ms, always for the newest curve; the kernel
    /// crossfades each new FIR in, and plays the stages until the first one is there.
    private func pushLinearPhase(_ state: AudioUnitState) {
        let phase = state.filterPhase
        let lengths = FilterPhase.allCases.filter(\.isLinear)
        guard phase.isLinear else {
            firWanted = nil
            for length in lengths { kernel.removeLinearPhase(length) }
            return
        }
        let spec = LinearPhaseEQ.Spec(settings: state.settings, sampleRate: sampleRate)
        if spec == kernel.linearPhaseSpec(phase) {
            firWanted = nil
            // The kernel plays the other length until this one's FIR is there; now it can go.
            for length in lengths where length != phase { kernel.removeLinearPhase(length) }
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
            queue.asyncAfter(deadline: .now() + wait) {
                self.firRetryScheduled = false
                self.designNextFIR()
            }
            return
        }
        firWanted = nil
        firInFlight = spec
        firStarted = Date()
        firQueue.async {
            let fir = LinearPhaseEQ.prepare(spec)
            self.queue.async {
                self.firInFlight = nil
                // Nil only without memory for it: retried with the next change.
                guard self.rendering, let fir else { return }
                // Even when the curve moved on meanwhile, it is closer than the FIR playing. The
                // kernel refuses one made for a rate it no longer runs at.
                let state = self.state
                if state.filterPhase == fir.spec.phase { self.kernel.setLinearPhase(fir) }
                // Then catch up with the curve as it is now.
                self.pushLinearPhase(state)
            }
        }
    }
}
