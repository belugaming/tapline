import SwiftUI
import TaplineCore
import UniformTypeIdentifiers

/// Room correction: measure the speakers with a microphone, check the setup,
/// and fit an EQ for Tapline or for the EQ built into an RME ADI-2 DAC.
struct RoomPage: View {
    @Environment(AppModel.self) private var model
    @Environment(RoomMeasurementService.self) private var room

    /// Where the correction runs.
    enum Hardware: String, CaseIterable, Identifiable {
        case tapline, adi2
        var id: String { rawValue }
        var title: LocalizedStringResource {
            switch self {
            case .tapline: "Tapline"
            case .adi2: "RME ADI-2 DAC"
            }
        }
    }

    @AppStorage(DebugSnapshot.key("roomInputUID")) private var inputUID = ""
    @AppStorage(DebugSnapshot.key("roomInputChannel")) private var inputChannel = 0
    @AppStorage(DebugSnapshot.key("roomSpeakers")) private var speakers: RoomMeasurementService.Speakers = .stereo
    @AppStorage(DebugSnapshot.key("roomCorrectBelow")) private var correctBelow = 500.0
    @AppStorage(DebugSnapshot.key("roomMaxBoost")) private var maxBoost = 3.0
    @AppStorage(DebugSnapshot.key("roomTilt")) private var tilt = 0.0
    @AppStorage(DebugSnapshot.key("roomBass")) private var bass = 0.0
    @AppStorage(DebugSnapshot.key("roomFilters")) private var filterCount = 8
    @AppStorage(DebugSnapshot.key("roomHardware")) private var hardware: Hardware = .tapline
    @AppStorage(DebugSnapshot.key("roomSeparate")) private var separate = true

    @State private var fits: Fits?
    @State private var applied = false
    @State private var chartChannel: EQChannels = .left
    @State private var correctionMessage: String?

    private var input: AudioDevice? {
        model.devices.inputDevices.first { $0.uid == inputUID } ?? model.devices.defaultInput
    }

    private var outputName: String { model.currentOutput?.name ?? "" }

    /// Whole hertz below 1 kHz.
    static func hz(_ f: Double) -> String {
        f < 1000 ? "\(Int(f.rounded())) Hz" : Format.frequency(f)
    }

    /// Up to the whole audio band: above about 2 kHz the EQ corrects the speakers
    /// themselves (crossover, tweeter) rather than the room.
    static let correctBelowRange = 100.0...20_000.0

    /// "Correct below" on a log scale, so the bass end stays as fine as before.
    /// Whole hertz below 1 kHz, 10 Hz below 10 kHz, 100 Hz above, as displayed.
    private var correctBelowSlider: Binding<Double> {
        Binding(get: { log10(correctBelow.clamped(to: Self.correctBelowRange)) },
                set: { value in
                    let f = pow(10, value)
                    let step = f < 1000 ? 1.0 : (f < 10_000 ? 10 : 100)
                    correctBelow = ((f / step).rounded() * step).clamped(to: Self.correctBelowRange)
                })
    }

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    intro
                    microphoneCard
                    measureCard
                    checkCard.id("check")
                    correctionCard.id("correct")
                }
                .padding(20)
                .frame(maxWidth: 980)
                .frame(maxWidth: .infinity)
            }
            .onAppear {
                guard let anchor = DebugSnapshot.scrollAnchor else { return }
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) { proxy.scrollTo(anchor, anchor: .top) }
            }
        }
        .task(id: fitKey) { await refit() }
        .onAppear { if DebugSnapshot.usesDemoData { room.applyDemo() } }
    }

    // MARK: - Intro

    private var intro: some View {
        Card(title: "Room Correction", subtitle: "Measure your speakers where you sit, fix what the setup can fix, and correct the room's bass resonances. The rest of the sound is left to your speakers.", systemImage: "hifispeaker.2") {
            VStack(alignment: .leading, spacing: 4) {
                Label("Pause music and keep the room quiet while measuring.", systemImage: "speaker.slash")
                Label("Use a measurement or studio condenser microphone at ear height, pointing between the speakers. A calibration file improves accuracy.", systemImage: "mic")
                Label("Switch off any EQ, Bass/Treble or Loudness in the DAC (such as the RME ADI-2) while measuring, so Tapline sees the speakers as they are.", systemImage: "slider.horizontal.3")
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
        Card(title: "2. Measure", subtitle: "Measure a few positions around your head; Tapline averages them.", systemImage: "waveform.path.ecg") {
            HStack(spacing: 12) {
                Picker("Speakers", selection: $speakers) {
                    ForEach(RoomMeasurementService.Speakers.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(width: 360)
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
                    Button("Clear") { room.clearMeasurements(); fits = nil }
                }
            }
            Group {
                if speakers == .stereo {
                    Text("Left & right: an 8-second sweep on each speaker, then three short clicks for timing (about 22 seconds). Needed for balance, distance and polarity checks, and for correcting each speaker separately.")
                } else {
                    Text("One 8-second sweep on the chosen speakers.")
                }
            }
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
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
                            if let dt = m.arrivalDifference {
                                Text(String(format: String(localized: "R − L %+.2f ms"), dt * 1000))
                                    .font(.caption.monospacedDigit())
                                    .foregroundStyle(.secondary)
                            }
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

    private var checkCard: some View {
        Card(title: "3. Check the Setup", subtitle: "Fix these at the speakers first: the EQ then has less to do and works better.", systemImage: "checklist") {
            if let fits {
                if fits.advice.isEmpty {
                    Label("Nothing to fix at the speakers. The setup looks good.", systemImage: "checkmark.circle")
                        .foregroundStyle(Theme.accent)
                } else {
                    VStack(alignment: .leading, spacing: 10) {
                        ForEach(fits.advice) { AdviceRow(advice: $0) }
                    }
                }
            } else {
                Text("Measure to see suggestions for balance, speaker distance, polarity, subwoofer level and the speakers' room switches.")
                    .foregroundStyle(.secondary)
            }
        }
    }

    // MARK: - Step 4

    private var correctionCard: some View {
        Card(title: "4. Correct", subtitle: "Cuts room resonances first. Dips are only lifted a little: they come from sound cancelling itself and boosting them mostly wastes amplifier power.", systemImage: "wand.and.stars") {
            HStack(spacing: 12) {
                Text("Run on").foregroundStyle(.secondary)
                Picker("Run on", selection: $hardware) {
                    ForEach(Hardware.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(width: 260)
                Toggle("Correct left and right separately", isOn: $separate)
                    .disabled(room.averagedLeft == nil || room.averagedRight == nil)
                    .help(Text("Needs a left & right measurement. On the ADI-2 this uses Dual EQ."))
                Spacer()
            }
            .font(.callout)
            Group {
                if hardware == .adi2 {
                    Text("Fits the ADI-2's own parametric EQ: five bands per channel (bands 4 and 5 from 200 Hz), 0.5 dB steps. The DAC then corrects every source, not only this Mac.")
                } else {
                    Text("Tapline corrects what this Mac plays, on the current output.")
                }
            }
                .font(.caption)
                .foregroundStyle(.secondary)

            if let fits {
                if fits.isSeparate {
                    Picker("Channel", selection: $chartChannel) {
                        Text("Left").tag(EQChannels.left)
                        Text("Right").tag(EQChannels.right)
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    .frame(width: 160)
                }
                let shown = fits.result(for: chartChannel)
                MeasurementChart(result: shown)
                    .frame(height: 200)
                HStack(spacing: 14) {
                    Legend(color: .gray, text: "Measured")
                    Legend(color: .white, dashed: true, text: "Target")
                    Legend(color: Theme.accent, text: "With EQ")
                    Spacer()
                    Text(String(format: String(localized: "Deviation %.1f → %.1f dB"), shown.errorBeforeDB, shown.errorAfterDB))
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(.secondary)
                }
                .font(.caption)
                FilterList(bands: fits.isSeparate ? shown.bands : fits.combined.bands)
            } else {
                Text("Measure at least once to see the room response here.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, minHeight: 120)
            }

            Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 6) {
                GridRow {
                    Text("Correct below")
                    Slider(value: correctBelowSlider, in: log10(Self.correctBelowRange.lowerBound)...log10(Self.correctBelowRange.upperBound))
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
                    if hardware == .adi2 {
                        Text("5 per channel (ADI-2)").foregroundStyle(.secondary)
                    } else {
                        Stepper(value: $filterCount, in: 2...12) { Text("\(filterCount)") }
                    }
                    Color.clear.frame(width: 1, height: 1)
                }
            }
            .font(.callout)
            if correctBelow > 2000 {
                Text("Above 2 kHz the EQ also corrects the speakers themselves (crossover, tweeter). Measure several positions, preferably with a calibration file. If the treble ends up too bright, choose Room curve or lower the Tilt.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack {
                Button("Flat") { bass = 0; tilt = 0 }
                Button("Room curve") { bass = 4; tilt = -0.6 }
                    .help(Text("A gentle bass lift and treble roll-off, how well-tuned speakers usually sound in a room"))
                Spacer()
            }
            HStack {
                if hardware == .adi2 {
                    Button(action: exportADI2) {
                        Label("Export for ADI-2 Remote…", systemImage: "square.and.arrow.down")
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(fits == nil)
                    Button("Preview in Tapline", action: applyInTapline)
                        .disabled(fits == nil)
                        .help(Text("Listen to the same correction through Tapline before loading it into the ADI-2"))
                } else {
                    Button(action: applyInTapline) {
                        Label(applied ? "Applied" : "Apply to \(outputName)", systemImage: applied ? "checkmark" : "wand.and.stars")
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(fits == nil)
                }
                Button("Copy Filters as Text") {
                    guard let fits else { return }
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(ParametricEQFormat.export(bands: fits.bands, preampDB: 0), forType: .string)
                    correctionMessage = String(localized: "Copied.")
                }
                .disabled(fits == nil)
            }
            if let correctionMessage {
                Text(correctionMessage).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            if hardware == .adi2 {
                Text("Load the correction either into the ADI-2 or in Tapline, not both, or the room is corrected twice.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private func applyInTapline() {
        guard let fits else { return }
        model.applyCorrection(bands: fits.bands, name: String(localized: "Room · \(outputName)"), source: String(localized: "Room measurement"))
        applied = true
        correctionMessage = String(localized: "Applied to \(outputName). Measurements bypass Tapline, so measuring again still shows the room without this EQ.")
    }

    private func exportADI2() {
        guard let fits else { return }
        let name = String(localized: "Room correction")
        do {
            let text = fits.isSeparate
                ? try ADI2Preset.export(left: fits.left?.bands ?? [], right: fits.right?.bands ?? [], name: "Tapline Room L/R")
                : try ADI2Preset.export(left: fits.combined.bands, name: "Tapline Room")
            if try ADI2Export.save(text, suggestedName: name) {
                correctionMessage = String(localized: ADI2Export.loadHint)
            }
        } catch {
            correctionMessage = ADI2Export.message(for: error)
        }
    }

    // MARK: - Fitting

    struct Fits {
        var combined: EQFitter.Result
        var left: EQFitter.Result?
        var right: EQFitter.Result?
        var advice: [RoomAdvice]

        var isSeparate: Bool { left != nil && right != nil }

        func result(for channel: EQChannels) -> EQFitter.Result {
            guard isSeparate else { return combined }
            return (channel == .right ? right : left) ?? combined
        }

        /// The correction as Tapline bands: per channel when separate.
        var bands: [EQBand] {
            guard let left, let right else { return combined.bands }
            return left.bands.map { var b = $0; b.channels = .left; return b }
                + right.bands.map { var b = $0; b.channels = .right; return b }
        }
    }

    private struct FitKey: Hashable {
        var measurements: [UUID]
        var correctBelow: Double
        var maxBoost: Double
        var tilt: Double
        var bass: Double
        var filters: Int
        var hardware: Hardware
        var separate: Bool
        var calibrated: Bool
    }

    private var fitKey: FitKey {
        FitKey(measurements: room.measurements.map(\.id), correctBelow: correctBelow.rounded(), maxBoost: maxBoost, tilt: tilt, bass: bass,
               filters: filterCount, hardware: hardware, separate: separate, calibrated: room.calibration != nil)
    }

    private func refit() async {
        applied = false
        correctionMessage = nil
        guard let measured = room.averagedResponse else { fits = nil; return }
        try? await Task.sleep(for: .milliseconds(150))
        if Task.isCancelled { return }

        var options = EQFitter.Options(maxFilters: filterCount, bassAdjustDB: bass, tiltDBPerOctave: tilt)
        options.correctOnlyBelow = correctBelow
        options.maxBoostDB = maxBoost
        options.maxCutDB = 15
        options.maxQ = 10
        options.lowSmoothingOctaves = 1.0 / 12
        if hardware == .adi2 { options = ADI2Preset.fitOptions(options) }
        let baseOptions = options

        let left = room.averagedLeft, right = room.averagedRight
        let perChannel = separate && left != nil && right != nil
        let arrival = room.arrivalDifference, polarity = room.oppositePolarity
        let positions = room.measurements.count, snr = room.worstSNRDB, calibrated = room.calibration != nil
        let fitted = await Task.detached(priority: .userInitiated) { () -> Fits in
            let target = RoomResponse.flatTarget()
            // Each response gets its own low-frequency limit: no boosting below the
            // speakers' roll-off, nor in the half octave above it where they already strain.
            func fit(_ response: FrequencyResponse) -> EQFitter.Result {
                var o = baseOptions
                o.noBoostBelow = RoomResponse.lowFrequencyLimit(response) * 1.41
                return EQFitter.fit(measurement: response, target: target, options: o)
            }
            let combined = fit(measured)
            let grid = combined.frequencies
            let advice = RoomAdvisor.advise(RoomAdvisor.Input(
                measurement: FrequencyResponse(frequencies: grid, levels: combined.measurement),
                target: FrequencyResponse(frequencies: grid, levels: combined.target),
                left: left, right: right, arrivalDifference: arrival, oppositePolarity: polarity,
                positions: positions, worstSNRDB: snr, calibrated: calibrated))
            guard perChannel, let left, let right else { return Fits(combined: combined, advice: advice) }
            return Fits(combined: combined, left: fit(left), right: fit(right), advice: advice)
        }.value
        if !Task.isCancelled { fits = fitted }
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

// MARK: - Advice

private struct AdviceRow: View {
    @Environment(AppModel.self) private var model
    let advice: RoomAdvice

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: icon)
                .foregroundStyle(color)
                .frame(width: 18)
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(.callout.weight(.semibold))
                Text(detail).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                if case .balance(let louder, let db) = advice.kind {
                    balanceAction(louder: louder, dB: db)
                }
            }
        }
    }

    private var icon: String {
        switch advice.severity {
        case .warning: "exclamationmark.triangle.fill"
        case .suggestion: "lightbulb"
        case .info: "info.circle"
        }
    }

    private var color: Color {
        switch advice.severity {
        case .warning: Theme.warning
        case .suggestion: Theme.accent
        case .info: .secondary
        }
    }

    /// Balance that turns the louder side down by `dB` (Tapline's law: the other side is scaled by 1 - |b|).
    static func balance(louder: EQChannels, dB: Double) -> Double {
        let cut = 1 - pow(10, -dB / 20)
        return louder == .right ? -cut : cut
    }

    @ViewBuilder
    private func balanceAction(louder: EQChannels, dB: Double) -> some View {
        let wanted = Self.balance(louder: louder, dB: dB)
        let current = model.settings.effects.balance
        if abs(current - wanted) < 0.01 {
            Label("Balanced in Tapline. Measurements bypass Tapline, so this stays listed.", systemImage: "checkmark")
                .font(.caption)
                .foregroundStyle(Theme.accent)
        } else {
            Button("Set Balance in Tapline") { model.mutate { $0.effects.balance = wanted } }
                .controlSize(.small)
                .padding(.top, 2)
        }
    }

    private func side(_ c: EQChannels) -> String {
        c == .right ? String(localized: "right") : String(localized: "left")
    }

    private var title: String {
        switch advice.kind {
        case .polarity:
            String(localized: "Left and right have opposite polarity")
        case .noisy(let snr):
            String(format: String(localized: "The measurement is noisy (SNR %.0f dB)"), snr)
        case .balance(let louder, let db):
            String(format: String(localized: "The %@ speaker is %.1f dB louder"), side(louder), db)
        case .distance(let farther, let cm):
            String(format: String(localized: "The %@ speaker is %.0f cm farther away"), side(farther), cm)
        case .channelMismatch(let db, let f):
            String(format: String(localized: "Left and right differ by up to %.0f dB around %@"), db, RoomPage.hz(f))
        case .subwoofer(let db):
            db > 0
                ? String(format: String(localized: "Too much deep bass (%+.1f dB below 90 Hz)"), db)
                : String(format: String(localized: "Too little deep bass (%+.1f dB below 90 Hz)"), db)
        case .bassTilt(let db, _):
            String(format: String(localized: "Boomy lower bass (%+.1f dB from 100 to 400 Hz)"), db)
        case .desktop(let f, let db):
            String(format: String(localized: "Desk bounce around %@ (%+.1f dB)"), RoomPage.hz(f), db)
        case .roomMode(let f, let db, _):
            String(format: String(localized: "Room resonance at %@ (%+.1f dB)"), RoomPage.hz(f), db)
        case .cancellation(let f, let depth, _, _):
            String(format: String(localized: "Deep dip at %@ (−%.0f dB)"), RoomPage.hz(f), depth)
        case .treble(let db, _, _):
            db > 0
                ? String(format: String(localized: "Bright treble (%+.1f dB above 4 kHz)"), db)
                : String(format: String(localized: "Dull treble (%+.1f dB above 4 kHz)"), db)
        case .fewPositions(let n):
            String(format: String(localized: "Only %d measurement position(s)"), n)
        }
    }

    private var detail: String {
        switch advice.kind {
        case .polarity:
            String(localized: "One speaker moves out while the other moves in, which thins the bass and blurs the centre. Check the cable wiring (XLR pins 2 and 3) and any polarity or phase switch, then measure again.")
        case .noisy:
            String(localized: "Turn the volume up a little, close windows and switch off fans, then measure again.")
        case .balance(_, let db):
            String(format: String(localized: "Turn that speaker down by %.1f dB with its level control, or set the balance in the DAC (on the RME ADI-2: VOL screen, Balance)."), db)
        case .distance(let farther, let cm):
            String(format: String(localized: "Move the %@ speaker about %.0f cm closer, or the other one back, so both reach you at the same time. The centre image gets sharper."), side(farther), cm)
        case .channelMismatch:
            String(localized: "Normal near walls and corners. Correcting left and right separately takes care of it.")
        case .subwoofer(let db):
            db > 0
                ? String(format: String(localized: "Turn the subwoofer level down by about %.0f dB before correcting, so the EQ has less to cut."), abs(db))
                : String(format: String(localized: "Turn the subwoofer level up by about %.0f dB. Without a subwoofer this is where the speakers run out."), abs(db))
        case .bassTilt(_, let step):
            String(format: String(localized: "Typical of speakers close to a wall. On Genelec monitors set the Bass Tilt switches to %.0f dB; other speakers have a similar bass or boundary switch."), step)
        case .desktop:
            String(localized: "Sound bouncing off the desk or console adds a bump. On Genelec monitors switch on Desktop Control (−4 dB at 160 Hz). Stands, or moving the speakers to the front edge, also help.")
        case .roomMode(_, _, let m):
            String(format: String(localized: "Fits a room dimension of about %.1f m. The correction cuts it."), m)
        case .cancellation(_, _, let cm, let crossover):
            crossover
                ? String(localized: "Near the subwoofer crossover this is often the subwoofer and the speakers cancelling. Try the subwoofer's phase switch or another subwoofer position, then measure again. EQ cannot fill it.")
                : String(format: String(localized: "A reflection cancelling the direct sound, for example from a wall about %.0f cm behind the speakers or behind you. EQ cannot fill it: move the speakers or your seat by 20 to 30 cm and measure again."), cm)
        case .treble(_, let step, let calibrated):
            String(format: String(localized: "If it also sounds that way, set the speakers' Treble Tilt to %+.0f dB."), step)
                + (calibrated ? "" : " " + String(localized: "Without a calibration file this may be the microphone."))
        case .fewPositions:
            String(localized: "Measure 3 to 5 positions within about 30 cm of your head for a more reliable correction.")
        }
    }
}

// MARK: - Pieces

/// The fitted filters, as they would be entered into a hardware EQ.
private struct FilterList: View {
    let bands: [EQBand]

    var body: some View {
        if !bands.isEmpty {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 126), spacing: 8, alignment: .leading)], alignment: .leading, spacing: 8) {
                ForEach(Array(bands.enumerated()), id: \.offset) { i, b in
                    HStack(spacing: 5) {
                        FilterGlyph(type: b.type, lineWidth: 1.2)
                            .foregroundStyle(.secondary)
                            .frame(width: 14, height: 9)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(verbatim: "\(i + 1) · \(RoomPage.hz(b.frequency))").font(.caption.weight(.semibold).monospacedDigit())
                            Text(verbatim: "\(Format.gain(b.gain)) · Q \(String(format: "%.1f", b.q))").font(.caption2.monospacedDigit()).foregroundStyle(.secondary)
                        }
                        .lineLimit(1)
                        .fixedSize()
                        Spacer(minLength: 0)
                    }
                    .help(Text(b.type.displayName))
                    .padding(.horizontal, 7)
                    .padding(.vertical, 4)
                    .background(Color.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 6))
                }
            }
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
