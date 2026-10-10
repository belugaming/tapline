import SwiftUI
import TaplineCore
import UniformTypeIdentifiers

/// Room correction: measure the speakers with a microphone and fit an EQ.
struct RoomPage: View {
    @Environment(AppModel.self) private var model
    @Environment(RoomMeasurementService.self) private var room

    @AppStorage(DebugSnapshot.key("roomInputUID")) private var inputUID = ""
    @AppStorage(DebugSnapshot.key("roomInputChannel")) private var inputChannel = 0
    @AppStorage(DebugSnapshot.key("roomSpeakers")) private var speakers: RoomMeasurementService.Speakers = .both
    @AppStorage(DebugSnapshot.key("roomCorrectBelow")) private var correctBelow = 500.0
    @AppStorage(DebugSnapshot.key("roomMaxBoost")) private var maxBoost = 3.0
    @AppStorage(DebugSnapshot.key("roomTilt")) private var tilt = 0.0
    @AppStorage(DebugSnapshot.key("roomBass")) private var bass = 0.0
    @AppStorage(DebugSnapshot.key("roomFilters")) private var filterCount = 8

    @State private var result: EQFitter.Result?
    @State private var applied = false

    private var input: AudioDevice? {
        model.devices.inputDevices.first { $0.uid == inputUID } ?? model.devices.defaultInput
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                intro
                microphoneCard
                measureCard
                correctionCard
            }
            .padding(20)
            .frame(maxWidth: 980)
            .frame(maxWidth: .infinity)
        }
        .task(id: fitKey) { await refit() }
        .onAppear { if DebugSnapshot.usesDemoData { room.applyDemo() } }
    }

    // MARK: - Intro

    private var intro: some View {
        Card(title: "Room Correction", subtitle: "Measure your speakers where you sit and correct the room's bass resonances. The rest of the sound is left to your speakers.", systemImage: "hifispeaker.2") {
            VStack(alignment: .leading, spacing: 4) {
                Label("Pause music and keep the room quiet while measuring.", systemImage: "speaker.slash")
                Label("Use a measurement or studio condenser microphone at ear height, pointing between the speakers. A calibration file improves accuracy.", systemImage: "mic")
                Label("Measurements play at the current output volume. Start low.", systemImage: "exclamationmark.triangle")
            }
            .font(.callout)
            .foregroundStyle(.secondary)
        }
    }

    // MARK: - Step 1

    private var microphoneCard: some View {
        Card(title: "1. Microphone", systemImage: "mic") {
            Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 10) {
                GridRow {
                    Text("Input").foregroundStyle(.secondary)
                    Picker("Input", selection: Binding(get: { input?.uid ?? "" }, set: { inputUID = $0; inputChannel = 0 })) {
                        ForEach(model.devices.inputDevices) { d in Text(d.name).tag(d.uid) }
                    }
                    .labelsHidden()
                    .frame(maxWidth: 280)
                }
                GridRow {
                    Text("Channel").foregroundStyle(.secondary)
                    Picker("Channel", selection: $inputChannel) {
                        ForEach(0..<max(input?.inputChannels ?? 1, 1), id: \.self) { i in Text("\(i + 1)").tag(i) }
                    }
                    .labelsHidden()
                    .frame(width: 90)
                }
                GridRow {
                    Text("Calibration").foregroundStyle(.secondary)
                    HStack {
                        if let name = room.calibrationName {
                            Label(name, systemImage: "checkmark.seal").lineLimit(1)
                            Button("Remove") { room.clearCalibration() }
                        } else {
                            Text("None").foregroundStyle(.tertiary)
                        }
                        Button("Load File…", action: loadCalibration)
                    }
                }
            }
            .font(.callout)

            HStack(spacing: 12) {
                Button {
                    guard let output = model.currentOutput, let input else { return }
                    Task { await room.checkLevel(output: output, input: input, channel: inputChannel, speakers: speakers) }
                } label: {
                    Label("Check Level", systemImage: "waveform")
                }
                .disabled(room.isBusy || input == nil || model.currentOutput == nil)
                InputLevelBar(peakDB: room.inputPeakDB, rmsDB: room.inputRMSDB, active: room.isBusy)
                    .frame(maxWidth: 320)
            }
            Text("Plays pink noise for three seconds. Aim for peaks between −30 and −10 dB on the microphone.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    // MARK: - Step 2

    private var measureCard: some View {
        Card(title: "2. Measure", subtitle: "Each measurement plays an 8-second sweep. Measure a few positions around your head; Tapline averages them.", systemImage: "waveform.path.ecg") {
            HStack(spacing: 12) {
                Picker("Speakers", selection: $speakers) {
                    ForEach(RoomMeasurementService.Speakers.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(width: 280)
                Button {
                    guard let output = model.currentOutput, let input else { return }
                    Task { await room.measure(output: output, input: input, channel: inputChannel, speakers: speakers) }
                } label: {
                    Label("Measure", systemImage: "play.circle.fill")
                }
                .buttonStyle(.borderedProminent)
                .disabled(room.isBusy || input == nil || model.currentOutput == nil)
                switch room.phase {
                case .measuring(let progress):
                    ProgressView(value: progress).frame(width: 140)
                case .analyzing:
                    ProgressView().controlSize(.small)
                    Text("Analyzing…").font(.caption).foregroundStyle(.secondary)
                default:
                    EmptyView()
                }
                Spacer()
                if !room.measurements.isEmpty {
                    Button("Clear") { room.clearMeasurements(); result = nil }
                }
            }
            if let message = room.lastMessage {
                Text(message).font(.callout).foregroundStyle(.secondary)
            }
            if !room.measurements.isEmpty {
                VStack(spacing: 4) {
                    ForEach(Array(room.measurements.enumerated()), id: \.element.id) { index, m in
                        HStack(spacing: 10) {
                            Text("#\(index + 1)").monospacedDigit().foregroundStyle(.secondary)
                            Text(String(localized: m.speakers.title))
                            Text(m.date, style: .time).foregroundStyle(.secondary)
                            Text(String(format: String(localized: "SNR %.0f dB · peak %.0f dB"), m.snrDB, m.peakDB))
                                .font(.caption.monospacedDigit())
                                .foregroundStyle(m.snrDB < 20 || m.peakDB > -1 ? Theme.warning : .secondary)
                            Spacer()
                            Button {
                                room.remove(m.id)
                            } label: {
                                Image(systemName: "trash")
                            }
                            .buttonStyle(.borderless)
                        }
                        .font(.callout)
                    }
                }
            }
        }
    }

    // MARK: - Step 3

    private var correctionCard: some View {
        Card(title: "3. Correct", subtitle: "Cuts room resonances first. Dips are only lifted a little: they come from sound cancelling itself and boosting them mostly wastes amplifier power.", systemImage: "wand.and.stars") {
            if let result {
                MeasurementChart(result: result)
                    .frame(height: 200)
                HStack(spacing: 14) {
                    Legend(color: .gray, text: "Measured")
                    Legend(color: .white, dashed: true, text: "Target")
                    Legend(color: Theme.accent, text: "With EQ")
                    Spacer()
                    Text(String(format: String(localized: "Deviation %.1f → %.1f dB"), result.errorBeforeDB, result.errorAfterDB))
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(.secondary)
                }
                .font(.caption)
            } else {
                Text("Measure at least once to see the room response here.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, minHeight: 120)
            }

            Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 6) {
                GridRow {
                    Text("Correct below")
                    Slider(value: $correctBelow, in: 100...2000)
                    Text(Format.frequency(correctBelow.rounded())).monospacedDigit().frame(width: 86, alignment: .trailing)
                }
                GridRow {
                    Text("Max boost")
                    Slider(value: $maxBoost, in: 0...6, step: 0.5)
                    Text(Format.gain(maxBoost)).monospacedDigit().frame(width: 86, alignment: .trailing)
                }
                GridRow {
                    Text("Bass")
                    Slider(value: $bass, in: -3...8, step: 0.5)
                    Text(Format.gain(bass)).monospacedDigit().frame(width: 86, alignment: .trailing)
                }
                GridRow {
                    Text("Tilt")
                    Slider(value: $tilt, in: -1.5...0.5, step: 0.1)
                    Text(String(format: "%+.1f dB/oct", tilt)).monospacedDigit().frame(width: 86, alignment: .trailing)
                }
                GridRow {
                    Text("Filters")
                    Stepper(value: $filterCount, in: 2...16) { Text("\(filterCount)") }
                    Color.clear.frame(width: 1, height: 1)
                }
            }
            .font(.callout)
            HStack {
                Button("Flat") { bass = 0; tilt = 0 }
                Button("Room curve") { bass = 4; tilt = -0.6 }
                    .help(Text("A gentle bass lift and treble roll-off, how well-tuned speakers usually sound in a room"))
                Spacer()
            }
            HStack {
                Button {
                    guard let result else { return }
                    model.applyCorrection(bands: result.bands, name: String(localized: "Room · \(model.currentOutput?.name ?? "")"), source: String(localized: "Room measurement"))
                    applied = true
                } label: {
                    Label(applied ? "Applied" : "Apply to \(model.currentOutput?.name ?? "")", systemImage: applied ? "checkmark" : "wand.and.stars")
                }
                .buttonStyle(.borderedProminent)
                .disabled(result == nil)
                Button("Copy Filters as Text") {
                    guard let result else { return }
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(ParametricEQFormat.export(bands: result.bands, preampDB: 0), forType: .string)
                }
                .disabled(result == nil)
                .help(Text("For entering into a hardware EQ, e.g. the parametric EQ of an RME ADI-2 (set Filters to 5)"))
            }
        }
    }

    // MARK: - Fitting

    private struct FitKey: Hashable {
        var count: Int
        var correctBelow: Double
        var maxBoost: Double
        var tilt: Double
        var bass: Double
        var filters: Int
    }

    private var fitKey: FitKey {
        FitKey(count: room.measurements.count, correctBelow: correctBelow.rounded(), maxBoost: maxBoost, tilt: tilt, bass: bass, filters: filterCount)
    }

    private func refit() async {
        applied = false
        guard let measured = room.averagedResponse else { result = nil; return }
        try? await Task.sleep(for: .milliseconds(150))
        if Task.isCancelled { return }
        var options = EQFitter.Options(maxFilters: filterCount, bassAdjustDB: bass, tiltDBPerOctave: tilt)
        options.correctOnlyBelow = correctBelow
        options.maxBoostDB = maxBoost
        options.maxCutDB = 15
        options.maxQ = 10
        options.lowSmoothingOctaves = 1.0 / 12
        let fitted = await Task.detached(priority: .userInitiated) {
            EQFitter.fit(measurement: measured, target: RoomResponse.flatTarget(), options: options)
        }.value
        if !Task.isCancelled { result = fitted }
    }

    private func loadCalibration() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.plainText, .commaSeparatedText, UTType(filenameExtension: "txt") ?? .plainText, UTType(filenameExtension: "cal") ?? .plainText, UTType(filenameExtension: "frd") ?? .plainText]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        if !room.loadCalibration(from: url) {
            room.lastMessage = String(localized: "No calibration data found in that file.")
        }
    }
}

private struct InputLevelBar: View {
    let peakDB: Double
    let rmsDB: Double
    let active: Bool

    var body: some View {
        HStack(spacing: 8) {
            MeterBar(peak: Float(dbToLinear(peakDB)), rms: Float(dbToLinear(rmsDB)), hold: 0, horizontal: true)
                .frame(height: 8)
            Text(active ? String(format: "%.0f dB", max(peakDB, -99)) : "—")
                .font(.caption.monospacedDigit())
                .foregroundStyle(.secondary)
                .frame(width: 48, alignment: .trailing)
        }
    }
}

private struct Legend: View {
    let color: Color
    var dashed = false
    let text: LocalizedStringKey

    var body: some View {
        HStack(spacing: 5) {
            Canvas { ctx, size in
                var p = Path()
                p.move(to: CGPoint(x: 0, y: size.height / 2))
                p.addLine(to: CGPoint(x: size.width, y: size.height / 2))
                ctx.stroke(p, with: .color(color), style: StrokeStyle(lineWidth: 2, dash: dashed ? [3, 2] : []))
            }
            .frame(width: 16, height: 6)
            Text(text).foregroundStyle(.secondary)
        }
    }
}
