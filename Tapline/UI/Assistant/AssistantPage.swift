import SwiftUI
import TaplineCore
import UniformTypeIdentifiers

struct AssistantPage: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                HeadphoneCorrectionCard()
                HStack(alignment: .top, spacing: 16) {
                    QuickToneCard()
                    SoundAssistantCard()
                }
                PresetLibraryCard()
                ImportExportCard()
            }
            .padding(20)
            .frame(maxWidth: 980)
            .frame(maxWidth: .infinity)
        }
    }
}

// MARK: - Quick tone

private struct QuickToneCard: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let tone = model.settings.tone
        Card(title: "Quick Tone", subtitle: "Broad, musical adjustments layered on top of the EQ.", systemImage: "dial.medium") {
            HStack(spacing: 14) {
                knob("Bass", \.bass, tone.bass, help: "Low shelf at 105 Hz")
                knob("Warmth", \.warmth, tone.warmth, help: "Bell at 250 Hz")
                knob("Clarity", \.clarity, tone.clarity, help: "Bell at 3 kHz")
                knob("Air", \.air, tone.air, help: "High shelf at 10 kHz")
            }
            .frame(maxWidth: .infinity)
            CurveThumbnail(bands: [], tone: tone, range: 12)
                .frame(height: 50)
                .padding(4)
                .instrumentPanel(radius: 8)
            Button("Reset Tone") { model.mutate { $0.tone = ToneSettings() } }
                .disabled(tone.isNeutral)
        }
    }

    private func knob(_ title: LocalizedStringKey, _ key: WritableKeyPath<ToneSettings, Double>, _ value: Double, help: LocalizedStringKey) -> some View {
        Knob(title: title, value: Binding(get: { value }, set: { v in model.mutate(undoable: false) { $0.tone[keyPath: key] = v.rounded(toPlaces: 1) } }),
             range: ToneSettings.range, defaultValue: 0, color: Theme.accent, bipolar: true, size: 50,
             format: { Format.gain($0) }, onEditingChanged: { if $0 { model.checkpoint() } })
            .help(Text(help))
    }
}

// MARK: - Sound assistant

private struct SoundAssistantCard: View {
    @Environment(AppModel.self) private var model

    private struct Action: Identifiable {
        let id: String
        let title: LocalizedStringKey
        let detail: LocalizedStringKey
        let symbol: String
        let run: (inout DSPSettings) -> Void
    }

    private static let step = 1.5

    private let actions: [Action] = [
        Action(id: "bass+", title: "More bass", detail: "Raises the low shelf", symbol: "speaker.wave.3") { $0.tone.bass = ($0.tone.bass + step).clamped(to: ToneSettings.range) },
        Action(id: "bass-", title: "Less bass", detail: "Lowers the low shelf", symbol: "speaker.wave.1") { $0.tone.bass = ($0.tone.bass - step).clamped(to: ToneSettings.range) },
        Action(id: "boom", title: "Less boomy", detail: "Cuts a resonance around 120 Hz", symbol: "waveform.badge.minus") { SoundAssistantCard.adjust(&$0, frequency: 120, q: 1.4, by: -step, tolerance: 0.25) },
        Action(id: "mud", title: "Less muddy", detail: "Cuts the 250 Hz region", symbol: "drop") { $0.tone.warmth = ($0.tone.warmth - step).clamped(to: ToneSettings.range) },
        Action(id: "warm", title: "Warmer", detail: "Adds body around 250 Hz", symbol: "flame") { $0.tone.warmth = ($0.tone.warmth + step).clamped(to: ToneSettings.range) },
        Action(id: "vocal", title: "Clearer vocals", detail: "Lifts presence at 3 kHz", symbol: "person.wave.2") { $0.tone.clarity = ($0.tone.clarity + step).clamped(to: ToneSettings.range) },
        Action(id: "harsh", title: "Less harsh", detail: "Softens 3 kHz", symbol: "ear") { $0.tone.clarity = ($0.tone.clarity - step).clamped(to: ToneSettings.range) },
        Action(id: "sibilance", title: "Less sibilance", detail: "Tames \"s\" sounds near 7 kHz", symbol: "textformat.size.smaller") { SoundAssistantCard.adjust(&$0, frequency: 7000, q: 3, by: -step, tolerance: 0.12) },
        Action(id: "air+", title: "More air", detail: "Raises the 10 kHz shelf", symbol: "sparkles") { $0.tone.air = ($0.tone.air + step).clamped(to: ToneSettings.range) },
        Action(id: "air-", title: "Darker", detail: "Lowers the 10 kHz shelf", symbol: "moon") { $0.tone.air = ($0.tone.air - step).clamped(to: ToneSettings.range) },
    ]

    /// Adjusts an existing bell near `frequency` or adds one.
    private static func adjust(_ s: inout DSPSettings, frequency: Double, q: Double, by delta: Double, tolerance: Double) {
        if let i = s.bands.firstIndex(where: { $0.type == .peak && abs(log10($0.frequency / frequency)) < tolerance }) {
            s.bands[i].gain = (s.bands[i].gain + delta).clamped(to: EQBand.gainRange)
            s.bands[i].isEnabled = true
        } else if s.bands.count < DSPSettings.maxBands {
            s.bands.append(EQBand(type: .peak, frequency: frequency, gain: delta, q: q))
        }
    }

    var body: some View {
        Card(title: "Sound Assistant", subtitle: "Describe what you hear. Each click makes a small change you can undo (⌘Z).", systemImage: "wand.and.stars") {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 150), spacing: 8)], spacing: 8) {
                ForEach(actions) { action in
                    Button {
                        model.mutate(action.run)
                    } label: {
                        HStack(spacing: 8) {
                            Image(systemName: action.symbol)
                                .frame(width: 18)
                                .foregroundStyle(Theme.accent)
                            Text(action.title)
                                .lineLimit(1)
                            Spacer(minLength: 0)
                        }
                        .padding(.horizontal, 10)
                        .padding(.vertical, 8)
                        .background(Color.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 8))
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .help(Text(action.detail))
                }
            }
            HStack {
                Button("Undo") { model.undo() }.disabled(!model.canUndo)
                Spacer()
            }
        }
    }
}

// MARK: - Presets

private struct PresetLibraryCard: View {
    @Environment(AppModel.self) private var model
    @State private var renaming: EQPreset?
    @State private var newName = ""

    var body: some View {
        Card(title: "Presets", subtitle: "Click a preset to apply it to the current output device.", systemImage: "square.grid.2x2") {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 170), spacing: 10)], spacing: 10) {
                ForEach(model.allPresets) { preset in
                    let active = model.matchingPreset?.id == preset.id
                    Button {
                        model.apply(preset)
                    } label: {
                        VStack(alignment: .leading, spacing: 6) {
                            CurveThumbnail(bands: preset.bands, tone: preset.tone, range: 12, color: active ? Theme.accent : Theme.spectrumPost)
                                .frame(height: 44)
                                .padding(4)
                                .instrumentPanel(radius: 6)
                            HStack(spacing: 4) {
                                Text(preset.name).lineLimit(1)
                                if active { Image(systemName: "checkmark").foregroundStyle(Theme.accent) }
                            }
                            .font(.callout)
                            Text(preset.isBuiltIn ? String(localized: "Built-in") : (preset.source ?? String(localized: "My preset")))
                                .font(.caption2)
                                .foregroundStyle(.secondary)
                                .lineLimit(1)
                        }
                        .padding(8)
                        .background(active ? Theme.accent.opacity(0.1) : Color.primary.opacity(0.035), in: RoundedRectangle(cornerRadius: 10))
                        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(active ? Theme.accent.opacity(0.6) : .clear))
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .contextMenu {
                        if !preset.isBuiltIn {
                            Button("Rename…") { newName = preset.name; renaming = preset }
                            Button("Delete", role: .destructive) { model.deletePreset(preset.id) }
                        }
                    }
                }
            }
        }
        .sheet(item: $renaming) { preset in
            SavePresetSheet(name: $newName) { name in model.renamePreset(preset.id, to: name) }
        }
    }
}

// MARK: - Import / export

private struct ImportExportCard: View {
    @Environment(AppModel.self) private var model
    @State private var pasted = ""
    @State private var message: String?

    var body: some View {
        Card(title: "Import & Export", subtitle: "Compatible with Equalizer APO, AutoEQ, Peace, Wavelet and other apps that use the ParametricEQ.txt format.", systemImage: "arrow.up.arrow.down.square") {
            HStack(alignment: .top, spacing: 12) {
                TextEditor(text: $pasted)
                    .font(.system(size: 11, design: .monospaced))
                    .frame(height: 90)
                    .scrollContentBackground(.hidden)
                    .padding(6)
                    .background(Color.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 8))
                    .overlay(alignment: .topLeading) {
                        if pasted.isEmpty {
                            Text("Paste text such as\nPreamp: -6.4 dB\nFilter 1: ON LSC Fc 105 Hz Gain 4.2 dB Q 0.70")
                                .font(.system(size: 11, design: .monospaced))
                                .foregroundStyle(.tertiary)
                                .padding(11)
                                .allowsHitTesting(false)
                        }
                    }
                VStack(alignment: .leading, spacing: 8) {
                    Button("Apply Pasted Text") {
                        importText(pasted, name: String(localized: "Imported preset"))
                    }
                    .disabled(pasted.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    Button("Import File…", action: importFile)
                    Button("Export Current EQ…", action: exportFile)
                    if let message {
                        Text(message).font(.caption).foregroundStyle(.secondary)
                    }
                }
                .frame(width: 200, alignment: .leading)
            }
        }
    }

    private func importText(_ text: String, name: String) {
        do {
            try model.importParametricEQ(text, name: name)
            message = String(localized: "Imported and saved as a preset.")
            pasted = ""
        } catch {
            message = String(localized: "No filters found in that text.")
        }
    }

    private func importFile() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.plainText, UTType(filenameExtension: "txt") ?? .plainText]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url, let text = try? String(contentsOf: url, encoding: .utf8) else { return }
        importText(text, name: url.deletingPathExtension().lastPathComponent)
    }

    private func exportFile() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.plainText]
        panel.nameFieldStringValue = "\(model.matchingPreset?.name ?? "Tapline") ParametricEQ.txt"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try model.exportParametricEQ().write(to: url, atomically: true, encoding: .utf8)
            message = String(localized: "Exported.")
        } catch {
            message = error.localizedDescription
        }
    }
}
