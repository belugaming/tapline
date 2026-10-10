import SwiftUI
import TaplineCore

struct EqualizerPage: View {
    @Environment(AppModel.self) private var model
    @AppStorage(DebugSnapshot.key("graphRange")) private var graphRange = 12.0
    @State private var savingPreset = false
    @State private var presetName = ""
    @State private var presetIncludesEffects = false

    // Fixed heights for everything except the graph, which gets the rest.
    private static let headerHeight: CGFloat = 28
    private static let stripHeight: CGFloat = 46
    private static let inspectorHeight: CGFloat = 150
    private static let spacing: CGFloat = 12
    private static let padding: CGFloat = 16

    var body: some View {
        GeometryReader { geo in
            let fixed = Self.headerHeight + Self.stripHeight + Self.inspectorHeight + Self.spacing * 3 + Self.padding * 2
            let graphHeight = max(160, geo.size.height - fixed)
            VStack(spacing: Self.spacing) {
                header
                    .frame(height: Self.headerHeight)
                HStack(spacing: 10) {
                    EQGraphView(dbRange: graphRange)
                    meterColumn
                }
                .frame(height: graphHeight)
                BandStrip()
                    .frame(height: Self.stripHeight)
                HStack(alignment: .top, spacing: 12) {
                    BandInspector()
                    PreampPanel()
                }
                .frame(height: Self.inspectorHeight)
            }
            .padding(Self.padding)
            .frame(width: geo.size.width, alignment: .top)
        }
        .sheet(isPresented: $savingPreset) {
            SavePresetSheet(name: $presetName, includeEffects: $presetIncludesEffects) { name in
                model.saveCurrentAsPreset(named: name, includeEffects: presetIncludesEffects)
            }
        }
    }

    private var header: some View {
        HStack(spacing: 10) {
            Toggle(isOn: Binding(get: { model.settings.eqEnabled }, set: { model.setEQEnabled($0) })) {
                Text("EQ")
                    .font(.headline)
            }
            .toggleStyle(.switch)
            .help(Text("Turn the equalizer on or off for this output device"))

            PresetMenu()
                .frame(minWidth: 120, maxWidth: 240)

            Button {
                presetName = model.matchingPreset?.name ?? ""
                savingPreset = true
            } label: {
                Label("Save Preset", systemImage: "square.and.arrow.down")
                    .labelStyle(.iconOnly)
            }
            .help(Text("Save the current curve as a preset"))

            CompareButton()

            Spacer(minLength: 16)

            Toggle(isOn: Binding(get: { model.showPreSpectrum }, set: { model.showPreSpectrum = $0 })) {
                Label("Input Spectrum", systemImage: "waveform")
                    .labelStyle(.iconOnly)
            }
            .toggleStyle(.button)
            .help(Text("Also show the spectrum before the EQ"))
            .padding(.trailing, 14)

            Picker("Range", selection: $graphRange) {
                Text("±6").tag(6.0)
                Text("±12").tag(12.0)
                Text("±18").tag(18.0)
                Text("±24").tag(24.0)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .frame(width: 156)
            .help(Text("Vertical range of the graph"))

            Menu {
                Button("Reset to Flat") { model.resetBands() }
                Button("Copy A to B") { model.copyToOtherSlot() }
                Divider()
                Button("Copy as Equalizer APO Text") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(model.exportParametricEQ(), forType: .string)
                }
            } label: {
                Image(systemName: "ellipsis")
            }
            .menuStyle(.button)
            .menuIndicator(.hidden)
            .fixedSize()
            .padding(.leading, 14)
            .help(Text("More"))
        }
    }

    private var meterColumn: some View {
        VStack(spacing: 10) {
            StereoMeter()
            VStack(spacing: 3) {
                Text("GR")
                    .font(.system(size: 9, weight: .bold))
                    .foregroundStyle(Theme.gridLabel)
                GainReductionBar(db: model.meters.gainReductionDB)
                    .frame(width: 8, height: 60)
            }
            .help(Text("Limiter gain reduction"))
        }
        .padding(.vertical, 12)
        .padding(.horizontal, 8)
        .frame(width: 56)
        .instrumentPanel()
    }
}

struct GainReductionBar: View {
    let db: Float

    var body: some View {
        GeometryReader { geo in
            let n = CGFloat(min(Double(db) / 12, 1))
            ZStack(alignment: .top) {
                RoundedRectangle(cornerRadius: 2).fill(Color.white.opacity(0.07))
                RoundedRectangle(cornerRadius: 2)
                    .fill(Theme.warning)
                    .frame(height: geo.size.height * n)
            }
        }
    }
}

// MARK: - Band strip

private struct BandStrip: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 6) {
                ForEach(Array(model.settings.bands.enumerated()), id: \.element.id) { index, band in
                    BandChip(band: band, index: index, selected: model.selectedBandID == band.id)
                        .onTapGesture { model.selectedBandID = band.id }
                }
                Button {
                    let f = nextFreeFrequency()
                    model.addBand(frequency: f)
                } label: {
                    Label("Add Band", systemImage: "plus")
                }
                .buttonStyle(.bordered)
                .disabled(model.settings.bands.count >= DSPSettings.maxBands)
                .help(Text("Add a band. You can also double-click the graph."))
            }
            .padding(.vertical, 2)
        }
    }

    private func nextFreeFrequency() -> Double {
        let used = model.settings.bands.map { log10($0.frequency) }
        let candidates = stride(from: log10(40.0), through: log10(16_000.0), by: 0.1).map { $0 }
        let best = candidates.max { a, b in
            (used.map { abs($0 - a) }.min() ?? 9) < (used.map { abs($0 - b) }.min() ?? 9)
        } ?? 3
        return pow(10, best)
    }
}

private struct BandChip: View {
    @Environment(AppModel.self) private var model
    let band: EQBand
    let index: Int
    let selected: Bool

    var body: some View {
        let color = Theme.bandColor(index)
        HStack(spacing: 6) {
            Text("\(index + 1)")
                .font(.system(size: 9, weight: .heavy, design: .rounded))
                .foregroundStyle(.black.opacity(0.75))
                .frame(width: 15, height: 15)
                .background(band.isEnabled ? color : color.opacity(0.3), in: Circle())
            FilterGlyph(type: band.type, lineWidth: 1.3)
                .foregroundStyle(.primary.opacity(0.7))
                .frame(width: 18, height: 11)
            VStack(alignment: .leading, spacing: 0) {
                Text(Format.frequency(band.frequency))
                    .font(.system(size: 11, weight: .semibold).monospacedDigit())
                if band.type.usesGain {
                    Text(Format.gain(band.gain))
                        .font(.system(size: 10).monospacedDigit())
                        .foregroundStyle(.secondary)
                }
            }
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 5)
        .background(selected ? color.opacity(0.18) : Color.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(selected ? color : .clear, lineWidth: 1.2))
        .opacity(band.isEnabled ? 1 : 0.55)
        .contentShape(Rectangle())
        .contextMenu {
            Button(band.isEnabled ? "Disable Band" : "Enable Band") { model.updateBand(band.id) { $0.isEnabled.toggle() } }
            Button("Delete Band", role: .destructive) { model.removeBand(band.id) }
        }
    }
}

// MARK: - Inspector

private struct BandInspector: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if let band = model.selectedBand, let index = model.settings.bands.firstIndex(where: { $0.id == band.id }) {
                editor(band: band, index: index)
            } else {
                VStack(spacing: 8) {
                    Image(systemName: "hand.point.up.left")
                        .font(.title2)
                        .foregroundStyle(.secondary)
                    Text("Select a band to edit it")
                        .foregroundStyle(.secondary)
                    Text("Drag the points on the graph · scroll for Q · double-click to add")
                        .font(.caption)
                        .foregroundStyle(.tertiary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .padding(14)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(.background.secondary, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(.separator.opacity(0.6)))
    }

    private func editor(band: EQBand, index: Int) -> some View {
        let color = Theme.bandColor(index)
        let id = band.id
        return HStack(alignment: .center, spacing: 18) {
            VStack(alignment: .leading, spacing: 10) {
                HStack(spacing: 8) {
                    Text("\(index + 1)")
                        .font(.system(size: 11, weight: .heavy, design: .rounded))
                        .foregroundStyle(.black.opacity(0.75))
                        .frame(width: 20, height: 20)
                        .background(color, in: Circle())
                    Text(band.type.displayName).font(.headline)
                    Spacer()
                    Toggle("Enabled", isOn: Binding(get: { band.isEnabled }, set: { v in model.updateBand(id) { $0.isEnabled = v } }))
                        .toggleStyle(.switch)
                        .controlSize(.small)
                        .labelsHidden()
                        .help(Text("Enable or bypass this band"))
                    Button(role: .destructive) {
                        model.removeBand(id)
                    } label: {
                        Image(systemName: "trash")
                    }
                    .buttonStyle(.borderless)
                    .help(Text("Delete band"))
                }
                HStack(spacing: 4) {
                    ForEach(FilterType.allCases) { type in
                        Button {
                            model.updateBand(id) { $0.type = type }
                        } label: {
                            FilterGlyph(type: type, lineWidth: 1.4)
                                .frame(width: 22, height: 13)
                                .padding(.vertical, 5)
                                .padding(.horizontal, 6)
                                .background(band.type == type ? color.opacity(0.25) : Color.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 6))
                                .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(band.type == type ? color : .clear))
                        }
                        .buttonStyle(.plain)
                        .help(Text(type.displayName))
                    }
                }
            }
            .frame(minWidth: 260)

            Divider().frame(height: 80)

            Knob(title: "Frequency", value: Binding(get: { band.frequency }, set: { v in model.updateBand(id, undoable: false) { $0.frequency = v } }),
                 range: EQBand.frequencyRange, defaultValue: 1000, scale: .logarithmic, color: color,
                 format: { Format.frequency($0) }, parse: KnobParsers.frequency, onEditingChanged: { if $0 { model.checkpoint() } })
            Knob(title: "Gain", value: Binding(get: { band.gain }, set: { v in model.updateBand(id, undoable: false) { $0.gain = v.rounded(toPlaces: 1) } }),
                 range: EQBand.gainRange, defaultValue: 0, color: color, bipolar: true,
                 format: { Format.gain($0) }, onEditingChanged: { if $0 { model.checkpoint() } })
                .disabled(!band.type.usesGain)
                .opacity(band.type.usesGain ? 1 : 0.4)
            Knob(title: "Q", value: Binding(get: { band.q }, set: { v in model.updateBand(id, undoable: false) { $0.q = v } }),
                 range: EQBand.qRange, defaultValue: band.type == .peak ? 1 : 0.71, scale: .logarithmic, color: color,
                 format: { Format.q($0) }, onEditingChanged: { if $0 { model.checkpoint() } })
            Spacer(minLength: 0)
        }
    }
}

private struct PreampPanel: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let s = model.settings
        let effective = s.effectivePreampDB(sampleRate: model.engine.sampleRate)
        VStack(spacing: 8) {
            Knob(title: "Preamp", value: Binding(get: { s.autoPreamp ? effective : s.preampDB }, set: { v in model.mutate(undoable: false) { $0.preampDB = v.rounded(toPlaces: 1); $0.autoPreamp = false } }),
                 range: -24...12, defaultValue: 0, color: Theme.warning, bipolar: true,
                 format: { Format.gain($0) }, onEditingChanged: { if $0 { model.checkpoint() } })
            Toggle("Auto", isOn: Binding(get: { s.autoPreamp }, set: { v in model.mutate { $0.autoPreamp = v; if !v { $0.preampDB = effective.rounded(toPlaces: 1) } } }))
                .toggleStyle(.checkbox)
                .help(Text("Automatically lower the level by the largest boost so the EQ never clips"))
        }
        .padding(14)
        .frame(width: 120)
        .frame(maxHeight: .infinity)
        .background(.background.secondary, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(.separator.opacity(0.6)))
    }
}

// MARK: - Presets

struct PresetMenu: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Menu {
            Section("Built-in") {
                ForEach(BuiltInPresets.all) { preset in
                    Button(preset.name) { model.apply(preset) }
                }
            }
            if !model.userPresets.isEmpty {
                Section("My Presets") {
                    ForEach(model.userPresets) { preset in
                        Button(preset.name) { model.apply(preset) }
                    }
                }
                Divider()
                Menu("Delete Preset") {
                    ForEach(model.userPresets) { preset in
                        Button(preset.name, role: .destructive) { model.deletePreset(preset.id) }
                    }
                }
            }
        } label: {
            Label {
                Text(model.matchingPreset?.name ?? String(localized: "Custom"))
                    .lineLimit(1)
            } icon: {
                Image(systemName: "list.bullet.rectangle")
            }
        }
        .help(Text("Choose a preset"))
    }
}

struct CompareButton: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Button {
            model.toggleCompare()
        } label: {
            HStack(spacing: 2) {
                Text("A").fontWeight(model.compareSlot == .a ? .bold : .regular)
                    .foregroundStyle(model.compareSlot == .a ? Theme.accent : .secondary)
                Text("/").foregroundStyle(.tertiary)
                Text("B").fontWeight(model.compareSlot == .b ? .bold : .regular)
                    .foregroundStyle(model.compareSlot == .b ? Theme.accent : .secondary)
            }
            .monospaced()
        }
        .help(Text("Switch between two EQ settings to compare them"))
    }
}

struct SavePresetSheet: View {
    @Binding var name: String
    var includeEffects: Binding<Bool>? = nil
    let onSave: (String) -> Void
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Save Preset").font(.headline)
            TextField("Preset name", text: $name)
                .textFieldStyle(.roundedBorder)
                .frame(width: 280)
                .onSubmit(save)
            if let includeEffects {
                Toggle(isOn: includeEffects) {
                    VStack(alignment: .leading, spacing: 1) {
                        Text("Include effects")
                        Text("Crossfeed, limiter, balance and width switch together with the curve.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
                .toggleStyle(.checkbox)
                .frame(width: 280, alignment: .leading)
            }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Save", action: save)
                    .keyboardShortcut(.defaultAction)
                    .disabled(name.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .padding(20)
    }

    private func save() {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return }
        onSave(trimmed)
        dismiss()
    }
}
