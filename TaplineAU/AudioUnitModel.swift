import AppKit
import AudioToolbox
import Observation
import TaplineCore
import UniformTypeIdentifiers

/// What the plugin's view shows and changes. Parameters go through the host (so that it can
/// record automation), everything else straight to the engine.
@MainActor
@Observable
final class AudioUnitModel {
    private(set) var snapshot: EngineSnapshot
    /// The user's presets from the app.
    private(set) var userPresets: [EQPreset] = []
    /// The headphones last chosen for "Listen as" in the app.
    private(set) var appHeadphones: DeviceSimulation?
    /// An import that went wrong.
    var message: String?

    @ObservationIgnored let engine: AudioUnitEngine
    @ObservationIgnored private let setParameter: @MainActor (PluginParameter, AUValue, AUParameterAutomationEventType) -> Void
    @ObservationIgnored nonisolated(unsafe) private var observer: UUID?

    init(engine: AudioUnitEngine, setParameter: @escaping @MainActor (PluginParameter, AUValue, AUParameterAutomationEventType) -> Void) {
        self.engine = engine
        self.setParameter = setParameter
        snapshot = engine.snapshot
        observer = engine.observe { [weak self] snapshot, _ in
            MainActor.assumeIsolated { self?.snapshot = snapshot }
        }
        refreshPresets()
    }

    deinit {
        if let observer { engine.removeObserver(observer) }
    }

    var state: AudioUnitState { snapshot.state }

    /// Reads the app's presets again: the user may have saved one meanwhile.
    func refreshPresets() {
        let presets = engine.userPresets()
        if presets != userPresets { userPresets = presets }
        let headphones = engine.appHeadphones()
        if headphones != appHeadphones { appHeadphones = headphones }
    }

    // MARK: - EQ

    func select(_ preset: EQPreset) {
        message = nil
        engine.update(from: .view) { $0.preset = preset }
    }

    /// The preset in the menu that the curve came from, when it still is the same curve.
    var selectedPreset: EQPreset? {
        (BuiltInPresets.all + userPresets).first { $0.id == state.preset.id && $0.hasSameCurve(as: state.preset) }
    }

    /// The project plays its own copy of the curve: the preset is gone from Tapline, has changed
    /// since, or was imported into the plugin.
    var playsSavedCopy: Bool { selectedPreset == nil }

    func setEQ(_ on: Bool) { setParameter(.eq, on ? 1 : 0, .value) }

    func setStrength(_ strength: Double, gesture: AUParameterAutomationEventType = .value) {
        setParameter(.strength, AUValue((strength * 100).rounded()), gesture)
    }

    func setLevelMatched(_ on: Bool) { engine.update(from: .view) { $0.levelMatchedBypass = on } }

    func setFilterPhase(_ phase: FilterPhase) {
        setParameter(.filterPhase, AUValue(FilterPhase.allCases.firstIndex(of: phase) ?? 0), .value)
    }

    /// The level the bypass plays at, dB, while the EQ is off and level-matched.
    var bypassGainDB: Double? {
        guard !state.eqEnabled, state.levelMatchedBypass else { return nil }
        let gain = state.settings.bypassGainDB(sampleRate: snapshot.sampleRate)
        return abs(gain) < 0.05 ? nil : gain
    }

    // MARK: - Import

    /// An Equalizer APO / AutoEQ ParametricEQ.txt or an RME ADI-2 preset, as a curve of its own.
    func importText(_ text: String, name: String) {
        do {
            let preset: EQPreset
            if text.contains("<ADI-2") {
                let parsed = try ADI2Preset.parse(text)
                guard !parsed.bands.isEmpty else { throw ParametricEQFormat.ParseError.noFilters }
                preset = EQPreset(name: parsed.name ?? name, bands: Array(parsed.bands.prefix(DSPSettings.maxBands)), source: "RME ADI-2")
            } else {
                let parsed = try ParametricEQFormat.parse(text)
                preset = EQPreset(name: name, bands: Array(parsed.bands.prefix(DSPSettings.maxBands)),
                                  preampDB: parsed.preampDB?.clamped(to: DSPSettings.preampRange) ?? 0,
                                  autoPreamp: parsed.preampDB == nil, source: "ParametricEQ.txt")
            }
            select(preset)
        } catch {
            message = String(localized: "No filters found. Choose or paste an Equalizer APO / AutoEQ ParametricEQ.txt, or an RME ADI-2 preset.")
        }
    }

    func chooseFile() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.allowedContentTypes = [.plainText, .text, UTType(filenameExtension: "adieqpr") ?? .data]
        panel.message = String(localized: "Choose an Equalizer APO / AutoEQ ParametricEQ.txt or an RME ADI-2 preset (.adieqpr).")
        panel.begin { [weak self] response in
            MainActor.assumeIsolated {
                guard response == .OK, let url = panel.url, let self else { return }
                guard let text = (try? String(contentsOf: url, encoding: .utf8)) ?? (try? String(contentsOf: url, encoding: .isoLatin1)) else {
                    self.message = String(localized: "Could not read \(url.lastPathComponent).")
                    return
                }
                self.importText(text, name: url.deletingPathExtension().lastPathComponent)
            }
        }
    }

    func paste() {
        guard let text = NSPasteboard.general.string(forType: .string), !text.isEmpty else {
            message = String(localized: "The clipboard holds no text.")
            return
        }
        importText(text, name: String(localized: "Pasted EQ"))
    }

    // MARK: - Effects

    func setVirtualSpeakers(_ on: Bool) { setParameter(.virtualSpeakers, on ? 1 : 0, .value) }

    func setVirtualSpeakerSet(_ id: String) { engine.update(from: .view) { $0.virtualSpeakers = id } }

    /// Off (nil) or one of the built-in speakers.
    func listen(as profile: SpeakerProfile?) {
        let index = profile.flatMap { SpeakerProfile.allCases.firstIndex(of: $0) }.map { $0 + 1 } ?? 0
        setParameter(.listenAs, AUValue(index), .value)
    }

    /// The headphones from the app (or those this project already plays).
    func listenAsHeadphones() { setParameter(.listenAs, PluginParameter.listenAsHeadphones, .value) }

    /// The headphones "Listen as" can play: the app's, else those the project keeps.
    var headphones: DeviceSimulation? {
        appHeadphones ?? (state.listenAs?.kind == .measured ? state.listenAs : nil)
    }

    // MARK: - Latency

    var latencyMilliseconds: Double { snapshot.latencySeconds * 1000 }

    /// Milliseconds a linear-phase length adds at the current rate.
    func latencyMilliseconds(of phase: FilterPhase) -> Int {
        Int(LinearPhaseEQ.latencyMilliseconds(sampleRate: snapshot.sampleRate, phase: phase).rounded())
    }
}
