import SwiftUI
import TaplineCore

/// The Room page's steps on a multichannel output (5.1 … 9.1.6): the speaker layout with a
/// wiring check, a measurement of every speaker, their levels, distances and polarity with the
/// trims and delays that align them at the listening position, and a room correction for each.
struct SpeakerRoomCards: View {
    @Environment(AppModel.self) private var model
    @Environment(RoomMeasurementService.self) private var room

    let layout: SpeakerLayout
    let input: AudioDevice?
    let inputChannel: Int

    // Shared with the stereo steps where they mean the same.
    @AppStorage(DebugSnapshot.key("roomCorrectBelow")) private var correctBelow = 500.0
    @AppStorage(DebugSnapshot.key("roomSurroundCorrectBelow")) private var surroundCorrectBelow = SpeakerRoomCards.defaultSurroundCorrectBelow
    @AppStorage(DebugSnapshot.key("roomMaxBoost")) private var maxBoost = 3.0
    @AppStorage(DebugSnapshot.key("roomTilt")) private var tilt = 0.0
    @AppStorage(DebugSnapshot.key("roomBass")) private var bass = 0.0
    @AppStorage(DebugSnapshot.key("roomFilters")) private var filterCount = 8
    @AppStorage(DebugSnapshot.key("roomAlignSpeakers")) private var alignSpeakers = true

    @State private var fits: Fits?
    @State private var chartChannel: Int?
    @State private var showPositions = false
    @State private var applied = false
    @State private var correctionMessage: String?
    /// Wiring check: the speaker (index into the layout's speakers) being asked about, the answers.
    @State private var wiringStep: Int?
    @State private var wiring: [Int: Bool] = [:]
    @State private var playTask: Task<Void, Never>?

    /// Surrounds and heights are mostly small speakers near walls or the ceiling, crossed over to
    /// the subwoofer by the receiver, and the microphone hears them off-axis: correct their room
    /// modes and boundary bumps, not their treble.
    static let defaultSurroundCorrectBelow = 300.0

    private var outputName: String { model.currentOutput?.name ?? "" }
    private var speakers: [Int] { layout.speakerChannels }
    private var runs: [RoomMeasurementService.SpeakerRun] { room.speakerRuns(for: layout) }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            speakersCard.id("speakers")
            measureCard.id("measure")
            resultsCard.id("check")
            correctionCard.id("correct")
        }
        .task(id: fitKey) { await refit() }
        .onDisappear { playTask?.cancel() }
    }

    // MARK: - Step 2: layout and wiring

    private var speakersCard: some View {
        Card(title: "2. Speakers", subtitle: "Tapline measures and corrects each speaker of this layout. Check first that every speaker plays from its place.", systemImage: "hifispeaker.2") {
            HStack(alignment: .top, spacing: 16) {
                SpeakerMap(layout: layout, playing: playingChannel, asking: wiringStep.map { speakers[$0] }, marks: wiring) { channel in
                    guard !room.isBusy, wiringStep == nil else { return }
                    identify(channel)
                }
                .frame(width: 260, height: 210)
                VStack(alignment: .leading, spacing: 8) {
                    Text(verbatim: "\(outputName) · \(layout.name)")
                        .font(.callout.weight(.semibold))
                    Text(String(format: String(localized: "%d speakers on %d channels. Click a speaker to hear it."), speakers.count, layout.channelCount))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    wiringControls
                }
                Spacer(minLength: 0)
            }
            Text("macOS renders surround and Dolby Atmos (such as Apple Music's) to the layout set in Audio MIDI Setup › Configure Speakers; Tapline reads the same layout. A Dolby bitstream passed through to a receiver cannot be processed.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var playingChannel: Int? {
        if case .identifying(let channel) = room.phase { return channel }
        return room.nowPlayingSpeaker.flatMap { now in layout.positions.firstIndex(of: now.position) }
    }

    @ViewBuilder
    private var wiringControls: some View {
        if let step = wiringStep {
            let position = layout.positions[speakers[step]]
            Text(String(format: String(localized: "Playing: %@ (%d of %d). Does the noise come from that speaker?"), position.name, step + 1, speakers.count))
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Button("Yes") { answer(true) }
                    .keyboardShortcut(.defaultAction)
                Button("No") { answer(false) }
                Button("Play Again") { identify(speakers[step]) }
                    .disabled(room.isBusy)
                Button("Stop") { stopWiring() }
            }
        } else {
            HStack {
                Button {
                    startWiring()
                } label: {
                    Label("Check Wiring", systemImage: "speaker.wave.2")
                }
                .disabled(room.isBusy || model.currentOutput == nil)
                if !wiring.isEmpty {
                    Button("Clear") { wiring = [:] }
                }
            }
            Text("Plays pink noise on each speaker in turn and asks where you hear it.")
                .font(.caption)
                .foregroundStyle(.secondary)
            if !wiring.isEmpty {
                wiringSummary
            }
        }
    }

    @ViewBuilder
    private var wiringSummary: some View {
        let wrong = speakers.filter { wiring[$0] == false }
        if wrong.isEmpty, wiring.count == speakers.count {
            Label("Every speaker plays from its place.", systemImage: "checkmark.circle")
                .font(.callout)
                .foregroundStyle(Theme.accent)
        } else if !wrong.isEmpty {
            Label {
                Text(String(format: String(localized: "Not from its place: %@. Check the speaker cables, the receiver's speaker setup and the layout in Audio MIDI Setup, then check again."),
                            wrong.map { layout.positions[$0].abbreviation }.joined(separator: ", ")))
                    .fixedSize(horizontal: false, vertical: true)
            } icon: {
                Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(Theme.warning)
            }
            .font(.callout)
        }
    }

    /// Plays pink noise on one speaker, after stopping the one playing.
    private func identify(_ channel: Int) {
        guard let output = model.currentOutput else { return }
        let previous = playTask
        previous?.cancel()
        playTask = Task {
            await previous?.value
            guard !Task.isCancelled else { return }
            await room.identify(output: output, channel: channel)
        }
    }

    private func startWiring() {
        wiring = [:]
        wiringStep = 0
        identify(speakers[0])
    }

    private func answer(_ correct: Bool) {
        guard let step = wiringStep else { return }
        wiring[speakers[step]] = correct
        if step + 1 < speakers.count {
            wiringStep = step + 1
            identify(speakers[step + 1])
        } else {
            wiringStep = nil
            playTask?.cancel()
        }
    }

    private func stopWiring() {
        wiringStep = nil
        playTask?.cancel()
    }

    // MARK: - Step 3: measure

    private var measureCard: some View {
        Card(title: "3. Measure", subtitle: "Measure a few positions around your head; Tapline averages them.", systemImage: "waveform.path.ecg") {
            HStack(spacing: 12) {
                Button {
                    guard let output = model.currentOutput, let input else { return }
                    stopWiring()
                    Task { await room.measureSpeakers(output: output, input: input, channel: inputChannel, layout: layout) }
                } label: {
                    Label("Measure All Speakers", systemImage: "play.circle.fill")
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
                if !runs.isEmpty {
                    Button("Clear") { room.clearSpeakerRuns(for: layout); fits = nil }
                }
            }
            if room.isMeasuring {
                HStack(spacing: 12) {
                    if let now = room.nowPlayingSpeaker {
                        Label(now.chirps ? String(localized: "Timing clicks") : String(format: String(localized: "%@ (%d of %d)"), now.position.name, now.index, now.count),
                              systemImage: "speaker.wave.2.fill")
                    }
                    Text("\(room.secondsLeft) s left").monospacedDigit().foregroundStyle(.secondary)
                    InputLevelBar(peakDB: room.inputPeakDB, rmsDB: room.inputRMSDB, active: true)
                        .frame(maxWidth: 240)
                    Spacer()
                }
                .font(.callout)
            }
            Text(String(format: String(localized: "Each speaker plays a %.0f-second sweep in turn, then a short click for timing: about %.0f seconds per position. The subwoofer's click is a low rumble."),
                        SpeakerSweepPlan.defaultSweepSeconds, measureSeconds))
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            if let message = room.lastMessage {
                Text(message).font(.callout).foregroundStyle(.secondary)
            }
            RoomPositionGuide(measured: room.measuredPositions(for: layout), next: room.nextPosition(for: layout))
            if !runs.isEmpty {
                VStack(spacing: 4) {
                    ForEach(Array(runs.enumerated()), id: \.element.id) { index, run in
                        HStack(spacing: 10) {
                            Text("#\(index + 1)").monospacedDigit().foregroundStyle(.secondary)
                            Text(verbatim: layout.name)
                            Text(run.date, style: .time).foregroundStyle(.secondary)
                            QualityBadge(quality: run.quality, differs: false)
                            Text(String(format: String(localized: "SNR %.0f dB · peak %.0f dB"), run.snrDB, run.peakDB))
                                .font(.caption.monospacedDigit())
                                .foregroundStyle(run.snrDB < 20 || run.peakDB > -1 ? Theme.warning : .secondary)
                            Spacer()
                            Button {
                                room.removeSpeakerRun(run.id)
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

    private var measureSeconds: Double {
        let duration = SpeakerSweepPlan.duration(speakers: speakers.map { SpeakerSweepPlan.Speaker(channel: $0, isLFE: layout.positions[$0].isLFE) })
        return (duration / 5).rounded() * 5
    }

    // MARK: - Step 4: levels, distances and polarity

    private var resultsCard: some View {
        Card(title: "4. Levels, Distances and Polarity", subtitle: "Compared with the front left speaker. Tapline turns the louder speakers down to the quietest and holds back the nearer ones until the farthest arrives.", systemImage: "checklist") {
            if let fits {
                SpeakerTable(fits: fits, layout: layout)
                if !fits.advice.isEmpty {
                    VStack(alignment: .leading, spacing: 10) {
                        ForEach(fits.advice) { SpeakerAdviceRow(advice: $0, layout: layout) }
                    }
                    .padding(.top, 4)
                }
            } else {
                Text("Measure to see each speaker's level, distance and polarity.")
                    .foregroundStyle(.secondary)
            }
            VStack(alignment: .leading, spacing: 4) {
                Label("Bass management and crossovers stay the receiver's job: set the speakers to Small or Large and the crossover there. Tapline leaves the subwoofer's level to its own gain or the receiver.", systemImage: "info.circle")
                Label("Switch off the receiver's own room correction (Audyssey, Dirac Live, YPAO) while measuring, and then use either it or Tapline, not both.", systemImage: "info.circle")
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
        }
    }

    // MARK: - Step 5: correct

    private var correctionCard: some View {
        Card(title: "5. Correct", subtitle: "Each speaker gets its own room correction, after the EQ and effects. Cuts room resonances first; dips are only lifted a little.", systemImage: "wand.and.stars") {
            if let fits, let shown = fits.speaker(chartChannel ?? speakers.first ?? 0) ?? fits.speakers.first {
                HStack {
                    Picker("Speaker", selection: Binding(get: { shown.channel }, set: { chartChannel = $0 })) {
                        ForEach(fits.speakers) { s in Text(verbatim: "\(s.position.abbreviation) · \(s.position.name)").tag(s.channel) }
                    }
                    .labelsHidden()
                    .frame(width: 240)
                    Spacer()
                    Text(String(format: String(localized: "Deviation %.1f → %.1f dB"), shown.result.errorBeforeDB, shown.result.errorAfterDB))
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(.secondary)
                }
                let overlay = shown.overlay
                MeasurementChart(result: shown.result, spreadLow: overlay.low, spreadHigh: overlay.high,
                                 positions: showPositions ? overlay.positions : [])
                    .frame(height: 200)
                HStack(spacing: 14) {
                    RoomLegend(color: .gray, text: "Measured")
                    RoomLegend(color: .white, dashed: true, text: "Target")
                    RoomLegend(color: Theme.accent, text: "With EQ")
                    if !overlay.positions.isEmpty {
                        RoomLegend(color: .gray, band: true, text: "Spread")
                        Toggle("Positions", isOn: $showPositions)
                            .toggleStyle(.checkbox)
                    }
                    Spacer()
                    if alignSpeakers {
                        Text(String(format: String(localized: "Trim %@ · delay %.2f ms"), Format.gain(shown.trimDB), shown.delayMS))
                            .font(.caption.monospacedDigit())
                            .foregroundStyle(.secondary)
                    }
                }
                .font(.caption)
                FilterList(bands: shown.result.bands)
                if shown.position.isLFE {
                    Text("The subwoofer is corrected below 120 Hz and never boosted above it.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            } else {
                Text("Measure at least once to see each speaker's response here.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, minHeight: 120)
            }

            Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 6) {
                GridRow {
                    Text("Correct below (front)")
                    Slider(value: logSlider($correctBelow), in: log10(RoomPage.correctBelowRange.lowerBound)...log10(RoomPage.correctBelowRange.upperBound))
                    Text(Format.frequency(correctBelow.rounded())).monospacedDigit().frame(width: 86, alignment: .trailing)
                }
                GridRow {
                    Text("Correct below (surround, height)")
                    Slider(value: logSlider($surroundCorrectBelow), in: log10(RoomPage.correctBelowRange.lowerBound)...log10(RoomPage.correctBelowRange.upperBound))
                    Text(Format.frequency(surroundCorrectBelow.rounded())).monospacedDigit().frame(width: 86, alignment: .trailing)
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
                    Stepper(value: $filterCount, in: 2...12) { Text("\(filterCount)") }
                    Color.clear.frame(width: 1, height: 1)
                }
            }
            .font(.callout)
            HStack {
                Button("Flat") { bass = 0; tilt = 0 }
                Button("Room curve") { bass = 4; tilt = -0.6 }
                    .help(Text("A gentle bass lift and treble roll-off, how well-tuned speakers usually sound in a room"))
                Button("Defaults") { correctBelow = 500; surroundCorrectBelow = Self.defaultSurroundCorrectBelow }
                    .help(Text("500 Hz for the front speakers, 300 Hz for surrounds and heights"))
                Spacer()
                Toggle("Align levels and delays", isOn: $alignSpeakers)
                    .help(Text("Apply each speaker's trim and delay with its correction"))
            }
            HStack {
                Button(action: apply) {
                    Label(applied ? "Applied" : "Apply to \(outputName)", systemImage: applied ? "checkmark" : "wand.and.stars")
                }
                .buttonStyle(.borderedProminent)
                .disabled(fits == nil)
                Button("Copy Filters as Text") {
                    guard let fits else { return }
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(ParametricEQFormat.exportSpeakers(fits.corrections, layoutName: layout.name,
                                                                                      headroomDB: SpeakerSetup.headroomDB(fits.corrections)), forType: .string)
                    correctionMessage = String(localized: "Copied, with a Channel section per speaker for Equalizer APO.")
                }
                .disabled(fits == nil)
                Spacer()
                if model.settings.speakers.layout == layout, !model.settings.speakers.corrections.isEmpty {
                    Toggle("Speaker correction", isOn: Binding(get: { model.settings.speakers.correctionEnabled }, set: { model.setSpeakerCorrection($0) }))
                        .toggleStyle(.switch)
                        .help(Text("Switches every speaker's correction, trim and delay on or off"))
                    Button("Remove") { model.removeSpeakerCorrection(); applied = false }
                }
            }
            if let correctionMessage {
                Text(correctionMessage).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    /// "Correct below" on a log scale, rounded as displayed (see RoomPage).
    private func logSlider(_ value: Binding<Double>) -> Binding<Double> {
        Binding(get: { log10(value.wrappedValue.clamped(to: RoomPage.correctBelowRange)) },
                set: { v in
                    let f = pow(10, v)
                    let step = f < 1000 ? 1.0 : (f < 10_000 ? 10 : 100)
                    value.wrappedValue = ((f / step).rounded() * step).clamped(to: RoomPage.correctBelowRange)
                })
    }

    private func apply() {
        guard let fits else { return }
        model.applySpeakerCorrection(fits.corrections, layout: layout)
        applied = true
        correctionMessage = String(localized: "Applied to \(outputName): every speaker's filters, trim and delay. Measurements bypass Tapline, so measuring again still shows the room without them.")
    }

    // MARK: - Fitting

    struct Fits {
        struct Speaker: Identifiable {
            let channel: Int
            let position: SpeakerPosition
            let result: EQFitter.Result
            /// In its own band, relative to the reference speaker's.
            let levelDB: Double
            /// Metres farther than the reference speaker.
            let distance: Double?
            let oppositePolarity: Bool?
            let trimDB: Double
            let delayMS: Double
            let shapes: [[Double]]

            var id: Int { channel }

            /// The measured positions at the chart's level, and one standard deviation around their mean.
            var overlay: (positions: [[Double]], low: [Double], high: [Double]) {
                let f = result.frequencies
                guard let spread = RoomResponse.spread(shapes), spread.count == f.count else { return ([], [], []) }
                let band = position.isLFE ? (30.0, 80.0) : (200.0, 2000.0)
                let mids = f.indices.filter { f[$0] >= band.0 && f[$0] <= band.1 }
                let level = mids.reduce(0.0) { $0 + result.measurement[$1] } / Double(max(mids.count, 1))
                let positions = shapes.map { shape in shape.map { $0 + level } }
                let mean = f.indices.map { i in positions.reduce(0.0) { $0 + $1[i] } / Double(positions.count) }
                return (positions, zip(mean, spread).map { $0 - $1 }, zip(mean, spread).map { $0 + $1 })
            }
        }

        var speakers: [Speaker]
        var advice: [SpeakerAdvice]
        /// What Apply and Copy Filters as Text use.
        var corrections: [SpeakerCorrection]

        func speaker(_ channel: Int) -> Speaker? { speakers.first { $0.channel == channel } }
    }

    private struct FitKey: Hashable {
        var runs: [UUID]
        var layout: SpeakerLayout
        var correctBelow: Double
        var surroundCorrectBelow: Double
        var maxBoost: Double
        var tilt: Double
        var bass: Double
        var filters: Int
        var align: Bool
        var calibrated: Bool
    }

    private var fitKey: FitKey {
        FitKey(runs: runs.map(\.id), layout: layout, correctBelow: correctBelow.rounded(), surroundCorrectBelow: surroundCorrectBelow.rounded(),
               maxBoost: maxBoost, tilt: tilt, bass: bass, filters: filterCount, align: alignSpeakers, calibrated: room.calibration != nil)
    }

    private func refit() async {
        applied = false
        correctionMessage = nil
        let averages = room.speakerAverages(for: layout)
        guard !averages.isEmpty else { fits = nil; return }
        try? await Task.sleep(for: .milliseconds(150))
        if Task.isCancelled { return }

        var base = EQFitter.Options(maxFilters: filterCount, bassAdjustDB: bass, tiltDBPerOctave: tilt)
        base.maxBoostDB = maxBoost
        base.maxCutDB = 15
        base.maxQ = 10
        base.lowSmoothingOctaves = 1.0 / 12
        let front = correctBelow, surround = surroundCorrectBelow, align = alignSpeakers
        let positions = runs.count, snr = runs.map(\.snrDB).min() ?? 0
        let fitted = await Task.detached(priority: .userInitiated) { () -> Fits in
            let target = RoomResponse.flatTarget()
            let reference = averages.first { $0.position == .frontLeft } ?? averages.first { !$0.position.isLFE } ?? averages[0]
            let referenceLevel = SpeakerAlignment.level(reference.response, position: reference.position)
            let referenceArrival = reference.arrival ?? 0
            let levels = averages.map { SpeakerAlignment.level($0.response, position: $0.position) }
            let alignment = SpeakerAlignment.align(zip(averages, levels).map { a, level in
                SpeakerAlignment.Speaker(position: a.position, levelDB: level, arrival: a.arrival)
            })
            var speakers: [Fits.Speaker] = []
            var corrections: [SpeakerCorrection] = []
            for (i, a) in averages.enumerated() {
                var o = base
                if a.position.isLFE {
                    // The LFE channel carries nothing above 120 Hz: correct below it, never boost above it.
                    o = EQFitter.Options(maxFilters: min(base.maxFilters, 6))
                    o.maxBoostDB = base.maxBoostDB
                    o.maxCutDB = base.maxCutDB
                    o.maxQ = base.maxQ
                    o.lowSmoothingOctaves = base.lowSmoothingOctaves
                    o.correctOnlyBelow = SpeakerSetup.lfeBoostCeiling
                    o.noBoostAbove = SpeakerSetup.lfeBoostCeiling
                    o.noBoostBelow = 30
                    o.levelMatchRange = 30...80
                } else {
                    o.correctOnlyBelow = a.position.group == .front ? front : surround
                    o.noBoostBelow = RoomResponse.lowFrequencyLimit(a.response) * 1.41
                }
                let result = EQFitter.fit(measurement: a.response, target: target, options: o)
                let trim = align ? alignment[i].trimDB : 0
                let delay = align ? alignment[i].delayMS : 0
                speakers.append(Fits.Speaker(channel: a.channel, position: a.position, result: result,
                                             levelDB: levels[i] - referenceLevel,
                                             distance: a.arrival.map { ($0 - referenceArrival) * SpeakerAlignment.speedOfSound },
                                             oppositePolarity: a.oppositePolarity, trimDB: trim, delayMS: delay,
                                             shapes: a.responses.count >= 2 ? RoomResponse.shapes(a.responses) : []))
                corrections.append(SpeakerCorrection(channel: a.channel, position: a.position, bands: result.bands, trimDB: trim, delayMS: delay))
            }
            let advice = SpeakerAdvice.advise(zip(averages, levels).map { a, level in
                SpeakerAdvice.Speaker(channel: a.channel, position: a.position, levelDB: level, arrival: a.arrival, oppositePolarity: a.oppositePolarity)
            }, positions: positions, worstSNRDB: snr)
            return Fits(speakers: speakers, advice: advice, corrections: corrections)
        }.value
        if !Task.isCancelled { fits = fitted }
    }
}

// MARK: - Pieces

/// Every speaker's level, distance and polarity against the front left, and its trim and delay.
private struct SpeakerTable: View {
    let fits: SpeakerRoomCards.Fits
    let layout: SpeakerLayout

    var body: some View {
        Grid(alignment: .leading, horizontalSpacing: 18, verticalSpacing: 5) {
            GridRow {
                Text("Speaker")
                Text("Level").gridColumnAlignment(.trailing)
                Text("Distance").gridColumnAlignment(.trailing)
                Text("Polarity")
                Text("Trim").gridColumnAlignment(.trailing)
                Text("Delay").gridColumnAlignment(.trailing)
            }
            .font(.caption.weight(.semibold))
            .foregroundStyle(.secondary)
            Divider().gridCellUnsizedAxes(.horizontal)
            ForEach(fits.speakers) { s in
                GridRow {
                    HStack(spacing: 6) {
                        Text(s.position.abbreviation)
                            .font(.caption.weight(.semibold).monospacedDigit())
                            .frame(width: 32, alignment: .leading)
                        Text(s.position.name).lineLimit(1)
                    }
                    Text(Format.gain(s.levelDB) + (s.position.isLFE ? "*" : ""))
                    Text(s.distance.map { String(format: "%+.2f m", $0) } ?? "—")
                    polarity(s)
                    Text(Format.gain(s.trimDB)).foregroundStyle(.secondary)
                    Text(String(format: "%.2f ms", s.delayMS)).foregroundStyle(.secondary)
                }
                .font(.callout.monospacedDigit())
            }
        }
        if fits.speakers.contains(where: { $0.position.isLFE }) {
            Text("* The subwoofer's level from 30 to 80 Hz, against the speakers' from 500 Hz to 2 kHz. Its distance includes the delay of its low-pass filter.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    @ViewBuilder
    private func polarity(_ s: SpeakerRoomCards.Fits.Speaker) -> some View {
        switch s.oppositePolarity {
        case true?:
            Label("Reversed", systemImage: "exclamationmark.triangle.fill").foregroundStyle(Theme.warning)
        case false?:
            Text("Same").foregroundStyle(.secondary)
        case nil:
            Text(verbatim: "—").foregroundStyle(.secondary)
        }
    }
}

private struct SpeakerAdviceRow: View {
    let advice: SpeakerAdvice
    let layout: SpeakerLayout

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: icon)
                .foregroundStyle(color)
                .frame(width: 18)
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(.callout.weight(.semibold))
                Text(detail).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
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

    private func name(_ channel: Int) -> String {
        layout.positions.indices.contains(channel) ? layout.positions[channel].name : String(localized: "channel \(channel + 1)")
    }

    private var title: String {
        switch advice.kind {
        case .polarity(let c):
            String(format: String(localized: "%@ has opposite polarity"), name(c))
        case .silent(let c):
            String(format: String(localized: "%@: no timing click heard"), name(c))
        case .level(let c, let db):
            db > 0
                ? String(format: String(localized: "%@ is %.1f dB louder than the others"), name(c), db)
                : String(format: String(localized: "%@ is %.1f dB quieter than the others"), name(c), -db)
        case .distance(let c, let m):
            m > 0
                ? String(format: String(localized: "%@ is %.1f m farther than the others"), name(c), m)
                : String(format: String(localized: "%@ is %.1f m nearer than the others"), name(c), -m)
        case .subwoofer(_, let db):
            String(format: String(localized: "Subwoofer %+.1f dB against the speakers"), db)
        case .noisy(let snr):
            String(format: String(localized: "The measurement is noisy (SNR %.0f dB)"), snr)
        case .fewPositions(let n):
            String(format: String(localized: "Only %d measurement position(s)"), n)
        }
    }

    private var detail: String {
        switch advice.kind {
        case .polarity:
            String(localized: "It moves out while the front left moves in, which thins the sound between them. Check its cable (+ and − swapped) and the receiver's settings, then measure again.")
        case .silent:
            String(localized: "It may not play at all, or play from another channel. Run the wiring check, then measure again.")
        case .level:
            String(localized: "Tapline trims it, but a difference this large is worth fixing with the receiver's channel level or the speaker's own level control.")
        case .distance:
            String(localized: "Tapline delays the nearer speakers to match. Check the speaker's placement, and that the microphone heard this speaker and not a reflection.")
        case .subwoofer:
            String(localized: "Its level is a matter of taste: set it with the subwoofer's gain or the receiver's subwoofer level, where most people like it a few dB above the speakers. Through a receiver the LFE channel also plays 10 dB hotter by design.")
        case .noisy:
            String(localized: "Turn the volume up a little, close windows and switch off fans, then measure again.")
        case .fewPositions:
            String(localized: "Measure 3 to 5 positions within about 30 cm of your head for a more reliable correction.")
        }
    }
}
