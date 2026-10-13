import SwiftUI
import TaplineCore

struct SettingsPage: View {
    @Environment(AppModel.self) private var model
    @State private var confirmReset = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Card(title: "General", systemImage: "gearshape") {
                    Toggle("Open Tapline at login", isOn: Binding(get: { _ = model.launchAtLoginRevision; return model.launchAtLogin }, set: { model.launchAtLogin = $0 }))
                    Toggle("Turn on processing when Tapline starts", isOn: Binding(get: { model.powerOn }, set: { _ in model.togglePower() }))
                    Text("The interface language follows your system language (English or Simplified Chinese).")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                Card(title: "EQ Bypass", subtitle: "What you hear when the EQ is switched off (EQ switch, ⌘B, ⌃⌥⌘B, menu bar).", systemImage: "power") {
                    Toggle("Match loudness when bypassing", isOn: Binding(get: { model.levelMatchedBypass }, set: { model.levelMatchedBypass = $0 }))
                    Text("A correction usually lowers the level a few dB to make room for its boosts, so without it the music plays louder, and louder tends to sound better. With this on, the bypassed sound plays at the loudness of the EQ (measured on pink noise, like a LUFS meter), so the comparison is fair. It never raises the level.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                Card(title: "Filter Phase", subtitle: "How the EQ treats phase. Set for each output device; also in the Equalizer's More menu.", systemImage: "waveform.path") {
                    let phase = model.settings.filterPhase
                    // Switching to linear phase starts with low latency.
                    Picker("Filter Phase", selection: Binding(get: { model.playsLinearPhase }, set: { model.setFilterPhase($0 ? .linearLowLatency : .minimum) })) {
                        Text("Minimum phase").tag(false)
                        Text("Linear phase").tag(true)
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    .fixedSize()
                    .disabled(model.speakerLayout != nil)
                    // Only in linear phase: disabled, its selected segment still looked like a choice.
                    if phase.isLinear && model.speakerLayout == nil {
                        Picker("Length", selection: Binding(get: { phase == .linearHighPrecision ? FilterPhase.linearHighPrecision : .linearLowLatency },
                                                            set: { model.setFilterPhase($0) })) {
                            Text("Low latency · \(model.linearPhaseLatencyMS(.linearLowLatency)) ms").tag(FilterPhase.linearLowLatency)
                            Text("High precision · \(model.linearPhaseLatencyMS(.linearHighPrecision)) ms").tag(FilterPhase.linearHighPrecision)
                        }
                        .pickerStyle(.segmented)
                        .labelsHidden()
                        .fixedSize()
                    }
                    if let unavailable = model.linearPhaseUnavailable {
                        Label(unavailable, systemImage: "info.circle")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    if let device = model.currentOutput?.name {
                        Text("For \(device)")
                            .font(.callout)
                    }
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Minimum phase plays without latency. Like an analog EQ, each band shifts the phase a little around its frequency.")
                        Text("Linear phase plays the same curve without any phase shift, but later: video may lose lip sync. Low latency keeps headphone and speaker corrections exact; high precision also resolves narrow bass bands below 45 Hz. The room correction's timing is unchanged, as linear phase only removes the EQ's own phase shift, and room measurements are not delayed.")
                    }
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                }

                Card(title: "Keyboard Shortcuts", subtitle: "Work in every app, even when Tapline is in the background.", systemImage: "command") {
                    Toggle("Enable global shortcuts", isOn: Binding(get: { model.globalShortcutsEnabled }, set: { model.globalShortcutsEnabled = $0 }))
                    Grid(alignment: .leading, horizontalSpacing: 18, verticalSpacing: 4) {
                        GridRow { Text("⌃⌥⌘P").monospaced(); Text("Turn processing on or off") }
                        GridRow { Text("⌃⌥⌘B").monospaced(); Text("Bypass EQ (compare with the original sound)") }
                        GridRow { Text("⌃⌥⌘K").monospaced(); Text("Compare A/B") }
                        GridRow { Text("⌃⌥⌘L").monospaced(); Text("Listen as another device (Assistant → Listen As)") }
                    }
                    .font(.callout)
                    .foregroundStyle(model.globalShortcutsEnabled ? .primary : .secondary)
                }

                Card(title: "Analyzer", systemImage: "waveform") {
                    Toggle("Show input spectrum (before EQ)", isOn: Binding(get: { model.showPreSpectrum }, set: { model.showPreSpectrum = $0 }))
                    LabeledSlider(title: "Tilt", value: Binding(get: { model.analyzerTilt }, set: { model.analyzerTilt = $0.rounded(toPlaces: 1) }), range: 0...6, format: { String(format: "%.1f dB/oct", $0) })
                    Text("Tilt raises high frequencies in the display only, so typical music looks flat. 3 dB/oct is a good default.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                Card(title: "Permission", systemImage: "lock.shield") {
                    HStack {
                        Text(permissionText)
                        Spacer()
                        Button("Open Privacy Settings") { NSWorkspace.shared.open(AudioCapturePermission.settingsURL) }
                    }
                    Text("Tapline uses the macOS audio tap API to process sound on your Mac. Nothing is recorded or sent anywhere.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                Card(title: "Reset", systemImage: "arrow.counterclockwise") {
                    HStack {
                        Text("Remove all device EQs, app volumes and presets.")
                        Spacer()
                        Button("Reset Everything…", role: .destructive) { confirmReset = true }
                    }
                }

                Card(title: "About", systemImage: "info.circle") {
                    let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
                    let build = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "?"
                    Text("Tapline \(version) (\(build))")
                        .font(.headline)
                        .textSelection(.enabled)
                    Text("Open-source system-wide EQ and audio mixer for macOS, built on Core Audio process taps.")
                        .foregroundStyle(.secondary)
                    Text("Copyright © 2026 Tapline contributors. Free software under the GNU General Public License v3.0, with ABSOLUTELY NO WARRANTY.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Text("Headphone measurements are provided by the AutoEQ project by Jaakko Pasanen and its contributors.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    HStack {
                        Link("GitHub", destination: URL(string: "https://github.com/belugaming/tapline")!)
                        Link("AutoEQ", destination: URL(string: "https://github.com/jaakkopasanen/AutoEq")!)
                        Link("License (GPL-3.0)", destination: URL(string: "https://www.gnu.org/licenses/gpl-3.0.html")!)
                    }
                }
            }
            .padding(20)
            .frame(maxWidth: 760)
            .frame(maxWidth: .infinity)
        }
        .confirmationDialog("Reset everything?", isPresented: $confirmReset) {
            Button("Reset", role: .destructive) { model.resetEverything() }
        } message: {
            Text("This cannot be undone.")
        }
    }

    private var permissionText: LocalizedStringKey {
        switch AudioCapturePermission.status {
        case .authorized: "System Audio Recording: allowed"
        case .denied: "System Audio Recording: denied"
        case .unknown: "System Audio Recording: not decided yet"
        }
    }
}
