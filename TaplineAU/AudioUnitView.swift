import SwiftUI
import TaplineCore

/// The plugin's window, in the app's instrument-panel look: the preset and its curve, Strength
/// and the bypass, then phase, Virtual Speakers and Listen as, and the latency the host compensates.
struct AudioUnitView: View {
    let model: AudioUnitModel

    static let idealSize = CGSize(width: 620, height: 410)

    /// The slider's value while it is dragged; the state follows through the host.
    @State private var draggedStrength: Double?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            header
            AudioUnitCurve(state: model.state)
                .frame(maxWidth: .infinity, minHeight: 140, maxHeight: .infinity)
            controls
            footer
        }
        .padding(14)
        .frame(minWidth: 560, idealWidth: Self.idealSize.width, minHeight: 380, idealHeight: Self.idealSize.height)
        .background(Theme.panelRaised)
        .environment(\.colorScheme, .dark)
        .tint(Theme.accent)
        .task {
            // The user may save a preset (or a headphone correction) in Tapline meanwhile.
            while !Task.isCancelled {
                model.refreshPresets()
                try? await Task.sleep(for: .seconds(3))
            }
        }
    }

    // MARK: - Header

    private var header: some View {
        HStack(spacing: 10) {
            HStack(spacing: 6) {
                Circle().fill(Theme.accent).frame(width: 8, height: 8)
                Text(verbatim: "Tapline").font(.system(size: 13, weight: .semibold))
            }
            .fixedSize()
            presetMenu
            if model.playsSavedCopy {
                Text("Saved in project")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .padding(.horizontal, 6)
                    .padding(.vertical, 2)
                    .overlay(Capsule().strokeBorder(Theme.panelStroke))
                    .fixedSize()
                    .help("This project keeps its own copy of the curve, so it sounds the same on any Mac. Choose a preset again to load its current version from Tapline.")
            }
            Spacer(minLength: 8)
            if let gain = model.bypassGainDB {
                Text("Level-matched \(Format.gain(gain))")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
                    .lineLimit(1)
                    .fixedSize()
            }
            Toggle("EQ", isOn: Binding(get: { model.state.eqEnabled }, set: { model.setEQ($0) }))
                .toggleStyle(.switch)
                .controlSize(.small)
                .fixedSize()
                .help("Bypass EQ (compare with the original sound)")
        }
    }

    private var presetMenu: some View {
        Menu {
            Section("Built-in") {
                ForEach(BuiltInPresets.all) { presetButton($0) }
            }
            Section("From Tapline") {
                if model.userPresets.isEmpty {
                    Text("No presets saved in Tapline yet")
                } else {
                    ForEach(model.userPresets) { presetButton($0) }
                }
            }
            Divider()
            Button("Import ParametricEQ.txt…") { model.chooseFile() }
            Button("Paste ParametricEQ.txt") { model.paste() }
        } label: {
            Text(verbatim: model.state.preset.name).lineLimit(1).truncationMode(.middle)
        }
        .frame(minWidth: 120, maxWidth: 280)
        .help("Presets")
    }

    private func presetButton(_ preset: EQPreset) -> some View {
        Button {
            model.select(preset)
        } label: {
            if model.selectedPreset?.id == preset.id {
                Label { Text(verbatim: preset.name) } icon: { Image(systemName: "checkmark") }
            } else {
                Text(verbatim: preset.name)
            }
        }
    }

    // MARK: - Controls

    private var controls: some View {
        Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 8) {
            GridRow {
                label("Strength")
                HStack(spacing: 8) {
                    Slider(value: Binding(get: { draggedStrength ?? model.state.strength }, set: { value in
                        draggedStrength = value
                        model.setStrength(value)
                    }), in: 0...1) { editing in
                        model.setStrength(draggedStrength ?? model.state.strength, gesture: editing ? .touch : .release)
                        if !editing { draggedStrength = nil }
                    }
                    .controlSize(.small)
                    Text(Format.percent(draggedStrength ?? model.state.strength))
                        .monospacedDigit()
                        .frame(width: 40, alignment: .trailing)
                }
                .help("How much of the correction to apply. At full strength the EQ corrects all the way to the target; lower values go part of the way, like a dry/wet control.")
            }
            GridRow {
                label("Filter Phase")
                Picker("Filter Phase", selection: Binding(get: { model.state.filterPhase }, set: { model.setFilterPhase($0) })) {
                    Text("Minimum phase").tag(FilterPhase.minimum)
                    Text("Low latency · \(model.latencyMilliseconds(of: .linearLowLatency)) ms").tag(FilterPhase.linearLowLatency)
                    Text("High precision · \(model.latencyMilliseconds(of: .linearHighPrecision)) ms").tag(FilterPhase.linearHighPrecision)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .controlSize(.small)
            }
            GridRow {
                label("Virtual Speakers")
                HStack(spacing: 8) {
                    Toggle("Virtual Speakers", isOn: Binding(get: { model.state.virtualSpeakersEnabled }, set: { model.setVirtualSpeakers($0) }))
                        .toggleStyle(.switch)
                        .controlSize(.small)
                        .labelsHidden()
                    Picker("Virtual Speakers", selection: Binding(get: { BundledSpeakers.resolve(model.state.virtualSpeakers).id },
                                                                  set: { model.setVirtualSpeakerSet($0) })) {
                        ForEach(BundledSpeakers.all) { Text($0.name).tag($0.id) }
                    }
                    .labelsHidden()
                    .controlSize(.small)
                    .fixedSize()
                    .disabled(!model.state.virtualSpeakersEnabled)
                    if let problem = model.snapshot.problem {
                        Text(verbatim: problem).font(.caption).foregroundStyle(Theme.warning).lineLimit(1)
                    }
                }
            }
            GridRow {
                label("Listen as")
                Picker("Listen as", selection: Binding(get: { listenChoice }, set: { choose($0) })) {
                    Text("Off").tag(ListenChoice.off)
                    Divider()
                    ForEach(SpeakerProfile.allCases) { Text($0.title).tag(ListenChoice.speaker($0)) }
                    if let headphones = model.headphones {
                        Divider()
                        Text(verbatim: headphones.name).tag(ListenChoice.headphones)
                    }
                }
                .labelsHidden()
                .controlSize(.small)
                .fixedSize()
            }
        }
        .font(.callout)
    }

    private func label(_ title: LocalizedStringKey) -> some View {
        Text(title)
            .foregroundStyle(.secondary)
            .gridColumnAlignment(.trailing)
    }

    private enum ListenChoice: Hashable {
        case off, speaker(SpeakerProfile), headphones
    }

    private var listenChoice: ListenChoice {
        guard let device = model.state.playingDevice else { return .off }
        return device.profile.map(ListenChoice.speaker) ?? .headphones
    }

    private func choose(_ choice: ListenChoice) {
        switch choice {
        case .off: model.listen(as: nil)
        case .speaker(let profile): model.listen(as: profile)
        case .headphones: model.listenAsHeadphones()
        }
    }

    // MARK: - Footer

    private var footer: some View {
        HStack(spacing: 10) {
            Toggle("Match loudness when bypassing", isOn: Binding(get: { model.state.levelMatchedBypass }, set: { model.setLevelMatched($0) }))
                .toggleStyle(.checkbox)
            if let message = model.message {
                Text(verbatim: message).foregroundStyle(Theme.warning).lineLimit(2)
            }
            Spacer(minLength: 8)
            Text("Latency \(String(format: "%.1f", model.latencyMilliseconds)) ms · \(model.snapshot.latency) samples")
                .monospacedDigit()
                .help("Logic Pro and other hosts delay the other tracks by as much, so everything stays in time.")
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        .controlSize(.small)
    }
}

/// The curve the plugin plays (left and right when they differ), the full-strength curve when
/// Strength is lower, and the device Listen as imitates.
struct AudioUnitCurve: View {
    let state: AudioUnitState

    private static let frequencies = ResponseCurve.logFrequencies(count: 240)
    private static let gridFrequencies: [Double] = [20, 50, 100, 200, 500, 1000, 2000, 5000, 10_000, 20_000]
    private static let sampleRate = 48_000.0

    private struct Curves {
        var left: [Double]
        var right: [Double]?
        var full: [Double]?
        var device: [Double]?
        var range: Double
    }

    private var curves: Curves {
        let played = state.settings
        let bands = played.activeBands.map { $0.clamped() }
        let split = played.hasChannelSpecificBands
        func response(_ bands: [EQBand], _ channel: EQChannels?) -> [Double] {
            ResponseCurve.responseDB(bands: bands, frequencies: Self.frequencies, sampleRate: Self.sampleRate, channel: channel)
        }
        let left = response(bands, split ? .left : nil)
        let right = split ? response(bands, .right) : nil
        var full: [Double]?
        if state.strength < 0.995 {
            var whole = state
            whole.strength = 1
            full = response(whole.settings.activeBands.map { $0.clamped() }, split ? .left : nil)
        }
        let device = state.playingDevice.map { response($0.bands.filter(\.isEnabled).map { $0.clamped() }, nil) }
        // The scale follows the EQ; the device's curve is only a reference and may run off it.
        let peak = [left, right ?? [], full ?? []].joined().map(abs).max() ?? 0
        return Curves(left: left, right: right, full: full, device: device, range: peak <= 11 ? 12 : (peak <= 17 ? 18 : 24))
    }

    var body: some View {
        let curves = self.curves
        let frequencies = Self.frequencies, gridFrequencies = Self.gridFrequencies
        Canvas { ctx, size in
            let plot = CGRect(x: 30, y: 8, width: max(1, size.width - 40), height: max(1, size.height - 26))
            let range = curves.range
            func x(_ f: Double) -> CGFloat { plot.minX + CGFloat((log10(f) - log10(20)) / 3) * plot.width }
            func y(_ db: Double) -> CGFloat { plot.minY + CGFloat((range - db.clamped(to: -range...range)) / (2 * range)) * plot.height }

            for f in gridFrequencies {
                var line = Path()
                line.move(to: CGPoint(x: x(f), y: plot.minY))
                line.addLine(to: CGPoint(x: x(f), y: plot.maxY))
                let major = f == 100 || f == 1000 || f == 10_000
                ctx.stroke(line, with: .color(major ? Theme.gridMajor : Theme.gridMinor), lineWidth: 1)
                ctx.draw(Text(Format.gridFrequency(f)).font(.system(size: 9)).foregroundStyle(Theme.gridLabel),
                         at: CGPoint(x: x(f), y: plot.maxY + 9), anchor: .center)
            }
            for db in stride(from: -range, through: range, by: range / 2) {
                var line = Path()
                line.move(to: CGPoint(x: plot.minX, y: y(db)))
                line.addLine(to: CGPoint(x: plot.maxX, y: y(db)))
                ctx.stroke(line, with: .color(db == 0 ? Theme.gridMajor : Theme.gridMinor), lineWidth: 1)
                ctx.draw(Text(db > 0 ? "+\(Int(db))" : "\(Int(db))").font(.system(size: 9)).foregroundStyle(Theme.gridLabel),
                         at: CGPoint(x: plot.minX - 5, y: y(db)), anchor: .trailing)
            }

            func path(_ values: [Double]) -> Path {
                var p = Path()
                for (i, f) in frequencies.enumerated() {
                    let point = CGPoint(x: x(f), y: y(values[i]))
                    if i == 0 { p.move(to: point) } else { p.addLine(to: point) }
                }
                return p
            }
            let opacity = state.eqEnabled ? 1.0 : 0.35
            if let device = curves.device {
                ctx.stroke(path(device), with: .color(Theme.warning.opacity(0.85)), style: StrokeStyle(lineWidth: 1.4, dash: [4, 3]))
            }
            if let full = curves.full {
                ctx.stroke(path(full), with: .color(Theme.curve.opacity(0.3 * opacity)), style: StrokeStyle(lineWidth: 1.2, dash: [3, 3]))
            }
            var fill = path(curves.left)
            fill.addLine(to: CGPoint(x: plot.maxX, y: y(0)))
            fill.addLine(to: CGPoint(x: plot.minX, y: y(0)))
            fill.closeSubpath()
            ctx.fill(fill, with: .color(Theme.accent.opacity(0.14 * opacity)))
            if let right = curves.right {
                ctx.stroke(path(right), with: .color(Theme.curveRight.opacity(opacity)), style: StrokeStyle(lineWidth: 1.8, lineJoin: .round))
            }
            ctx.stroke(path(curves.left), with: .color(Theme.curve.opacity(opacity)), style: StrokeStyle(lineWidth: 1.8, lineJoin: .round))
        }
        .overlay(alignment: .topTrailing) { legend(curves).padding(8) }
        .instrumentPanel(radius: 10)
    }

    private func legend(_ curves: Curves) -> some View {
        VStack(alignment: .trailing, spacing: 3) {
            if curves.right != nil {
                item(Theme.curve, Text("Left"))
                item(Theme.curveRight, Text("Right"))
            }
            if curves.full != nil { item(Theme.curve.opacity(0.4), Text(verbatim: "100 %"), dashed: true) }
            if let device = state.playingDevice {
                item(Theme.warning, device.profile.map { Text($0.title) } ?? Text(verbatim: device.name), dashed: true)
            }
        }
        .font(.system(size: 10))
    }

    private func item(_ color: Color, _ text: Text, dashed: Bool = false) -> some View {
        HStack(spacing: 5) {
            Canvas { ctx, size in
                var line = Path()
                line.move(to: CGPoint(x: 0, y: size.height / 2))
                line.addLine(to: CGPoint(x: size.width, y: size.height / 2))
                ctx.stroke(line, with: .color(color), style: StrokeStyle(lineWidth: 1.5, dash: dashed ? [3, 2] : []))
            }
            .frame(width: 14, height: 6)
            text.foregroundStyle(Theme.gridLabel)
        }
    }
}
