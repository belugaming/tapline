import SwiftUI
import TaplineCore

/// "Listen as": the music as other headphones, earbuds or everyday speakers would play it,
/// heard through the user's own headphones, like Sonarworks SoundID's Translation Check.
/// Measured headphones come from squig.link; speakers are built-in approximations.
struct ListenAsCard: View {
    enum Section: String, CaseIterable, Identifiable {
        case headphones, speakers
        var id: String { rawValue }

        var title: LocalizedStringKey {
            switch self {
            case .headphones: "Headphones & earbuds"
            case .speakers: "Speakers in devices"
            }
        }
    }

    @Environment(AppModel.self) private var model
    @State private var section: Section?

    /// Opens on the kind of device chosen last.
    private var shown: Section {
        if let section { return section }
        if DebugSnapshot.squigQuery != nil { return .headphones }
        return model.simulation?.kind == .speaker ? .speakers : .headphones
    }

    var body: some View {
        Card(title: "Listen As", subtitle: "Hear how your music would sound on other headphones, earbuds and speakers, through your own headphones. Your EQ and correction stay as they are, and switching is level-matched.", systemImage: "ear") {
            ListenAsSwitch()
            Picker("Device type", selection: Binding(get: { shown }, set: { section = $0 })) {
                ForEach(Section.allCases) { Text($0.title).tag($0) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .fixedSize()

            switch shown {
            case .headphones: SimulatedHeadphonesSection()
            case .speakers: SpeakerProfilesSection()
            }
        }
    }
}

// MARK: - Switch

/// The device that plays, the switch, and a note when the level match falls short.
private struct ListenAsSwitch: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 12) {
                Image(systemName: model.simulation?.symbolName ?? "ear")
                    .font(.system(size: 18))
                    .foregroundStyle(model.simulating ? Theme.accent : .secondary)
                    .frame(width: 26)
                VStack(alignment: .leading, spacing: 1) {
                    if let simulation = model.simulation {
                        Text("Listen as \(simulation.title)")
                            .font(.callout.weight(.semibold))
                            .lineLimit(1)
                        Text(simulation.detail)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    } else {
                        Text("Choose a device below.")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                }
                Spacer()
                Text("⌃⌥⌘L")
                    .font(.caption.monospaced())
                    .foregroundStyle(.secondary)
                    .help(Text("Global shortcut: switches the simulation on or off from any app"))
                Toggle("Listen as", isOn: Binding(get: { model.simulating }, set: { model.setSimulating($0) }))
                    .toggleStyle(.switch)
                    .labelsHidden()
                    .disabled(model.simulation == nil)
            }
            .padding(10)
            .background(model.simulating ? Theme.accent.opacity(0.12) : Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 10))

            let shortfall = model.simulationLevelShortfallDB
            if model.simulation != nil, shortfall >= 0.5 {
                Text(String(format: String(localized: "With the limiter off, the simulation plays %.1f dB below the loudness of your sound so that it cannot clip. Switch the limiter on (Effects) for an exact level match."), shortfall))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

// MARK: - Speakers

private struct SpeakerProfilesSection: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Approximations of typical devices, written from general knowledge of how such speakers behave: small drivers lose the bass, small boxes resonate, a car cabin lifts the bass. They are not measurements of any product.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 160), spacing: 10)], spacing: 10) {
                ForEach(SpeakerProfile.allCases) { tile($0) }
            }
        }
    }

    private func tile(_ profile: SpeakerProfile) -> some View {
        let chosen = model.simulation?.profile == profile
        let playing = chosen && model.simulating
        return Button {
            if playing { model.setSimulating(false) } else { model.chooseSimulation(profile.simulation) }
        } label: {
            VStack(alignment: .leading, spacing: 6) {
                CurveThumbnail(bands: profile.bands, range: 24, color: playing ? Theme.accent : Theme.spectrumPost)
                    .frame(height: 44)
                    .padding(4)
                    .instrumentPanel(radius: 6)
                HStack(spacing: 6) {
                    Image(systemName: profile.symbolName)
                        .foregroundStyle(.secondary)
                        .frame(width: 16)
                    Text(profile.title).lineLimit(1)
                    Spacer(minLength: 0)
                    if playing { Image(systemName: "speaker.wave.2.fill").foregroundStyle(Theme.accent) }
                }
                .font(.callout)
                Text(profile.channelsTitle)
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
            .padding(8)
            .background(playing ? Theme.accent.opacity(0.1) : Color.primary.opacity(0.035), in: RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(chosen ? Theme.accent.opacity(playing ? 0.6 : 0.3) : .clear))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(playing ? Text("Click to stop listening") : Text("Click to listen as this device"))
    }
}

// MARK: - Headphones and earbuds

private struct SimulatedHeadphonesSection: View {
    @Environment(SquigService.self) private var squig
    @AppStorage(DebugSnapshot.key("listenAsKind")) private var kindFilter = "all"
    @State private var query = ""
    @State private var results: [SquigService.Phone] = []
    @State private var selected: SquigService.Phone?

    /// What the results depend on; a change restarts the search.
    private struct SearchInput: Equatable {
        var query: String
        var kindFilter: String
        var phoneCount: Int
    }

    private var kinds: Set<SquigService.Kind> {
        if let k = SquigService.Kind(rawValue: kindFilter) { return [k] }
        return Set(SquigService.Kind.allCases)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Measurements from squig.link. Tapline fits an EQ that turns the sound your headphone correction aims at into the sound of the device you pick.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 10) {
                SearchField(text: $query, prompt: "Device, e.g. AirPods Pro 2, Apple EarPods, Galaxy Buds", busy: squig.isLoading)
                Picker("Type", selection: $kindFilter) {
                    Text("All types").tag("all")
                    ForEach(SquigService.Kind.allCases) { Text($0.title).tag($0.rawValue) }
                }
                .labelsHidden()
                .frame(width: 140)
            }
            if let error = squig.errorMessage {
                HStack {
                    Text("Could not load squig.link: \(error)").foregroundStyle(Theme.danger).font(.caption)
                    Button("Retry") { Task { await squig.loadAll(force: true) } }
                }
            }

            HStack(alignment: .top, spacing: 14) {
                List(results, selection: Binding(get: { selected?.id }, set: { id in selected = results.first { $0.id == id } })) { phone in
                    ResultRow(symbol: phone.database.kind == .headphones ? "headphones" : "earbuds",
                              title: phone.displayName,
                              subtitle: "\(phone.database.siteName) · \(String(localized: phone.database.kind.title))")
                        .tag(phone.id)
                }
                .listStyle(.bordered)
                .frame(width: 330, height: 330)
                .overlay { if results.isEmpty { EmptyHint(query: query) } }

                if let selected {
                    SimulationWorkbench(phone: selected)
                        .id(selected.id)
                } else {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Tips").font(.headline)
                        Text("• Correct your own headphones first (Headphone Correction above): the simulation starts from the target that correction aims at.")
                        Text("• Pick the mode people actually use, e.g. noise cancelling on.")
                        Text("• Above 10 kHz only the broad trend is simulated, because measurement rigs disagree up there.")
                    }
                    .font(.callout)
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
        }
        .task {
            await squig.loadAll()
            if let q = DebugSnapshot.squigQuery, query.isEmpty {
                query = q
                selected = squig.search(q, kinds: kinds).first
            }
        }
        .task(id: SearchInput(query: query, kindFilter: kindFilter, phoneCount: squig.phones.count)) {
            // As in Headphone Correction: wait for a pause in typing, then search off the main thread.
            let text = query, wanted = kinds, phones = squig.phones
            guard !text.isEmpty else { results = []; return }
            try? await Task.sleep(for: .milliseconds(150))
            guard !Task.isCancelled else { return }
            let found = await Task.detached(priority: .userInitiated) {
                SquigService.search(text, in: phones, kinds: wanted)
            }.value
            if !Task.isCancelled { results = found }
        }
    }
}

/// The comparison, the fitted curve and the button for one measured device.
private struct SimulationWorkbench: View {
    @Environment(AppModel.self) private var model
    @Environment(SquigService.self) private var squig

    let phone: SquigService.Phone

    @State private var device: FrequencyResponse?
    @State private var comparison: DeviceSimulation.Comparison = .crossRig
    /// Where the reference target comes from: the correction's database on the same rig, else
    /// the device's own database, or a fallback of the same kind when it has no targets.
    @State private var referenceDatabase: SquigService.Database?
    @State private var targets: [String] = []
    @State private var targetName: String?
    @State private var result: EQFitter.Result?
    @State private var computing = false
    @State private var error: String?

    private struct FitKey: Hashable {
        var target: String?
        var ready: Bool
    }

    private var playingThis: Bool { model.simulating && model.simulation?.id == phone.id }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                Text(phone.displayName).font(.headline).lineLimit(2)
                HStack(spacing: 4) {
                    Text("Measurement by \(phone.database.siteName)")
                    Link(destination: phone.database.baseURL) { Image(systemName: "arrow.up.right.square") }
                        .help(Text("Open the reviewer's database"))
                }
                .font(.caption)
                .foregroundStyle(.secondary)
            }

            HStack {
                Picker("Compared with", selection: Binding(get: { targetName ?? "" }, set: { targetName = $0 })) {
                    if targets.isEmpty { Text("Loading…").tag("") }
                    ForEach(targets, id: \.self) { Text($0).tag($0) }
                }
                .frame(maxWidth: 320)
                if computing { ProgressView().controlSize(.small) }
            }
            if let note {
                Text(note)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if let result {
                MeasurementChart(result: result)
                    .frame(height: 170)
                HStack(spacing: 14) {
                    ChartLegend(color: .gray, dashed: false, text: "Your sound")
                    ChartLegend(color: .white, dashed: true, text: "Device")
                    ChartLegend(color: Theme.accent, dashed: false, text: "Simulated")
                    Spacer()
                }
                .font(.caption)
            } else if let error {
                Text(error).font(.caption).foregroundStyle(Theme.danger)
            } else {
                ProgressView().frame(maxWidth: .infinity, minHeight: 170)
            }

            Button {
                if playingThis {
                    model.setSimulating(false)
                } else if let result {
                    model.chooseSimulation(simulation(from: result))
                }
            } label: {
                Label(playingThis ? "Stop Listening" : "Listen as This Device", systemImage: playingThis ? "stop.fill" : "ear")
            }
            .buttonStyle(.borderedProminent)
            .disabled(result == nil || computing)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .task { await load() }
        .task(id: FitKey(target: targetName, ready: device != nil && referenceDatabase != nil)) {
            await refit()
        }
    }

    /// Which comparison applies, in a sentence.
    private var note: String? {
        guard let referenceDatabase, let targetName, !targetName.isEmpty else { return nil }
        switch comparison {
        case .sameRig:
            return String(localized: "Same database as your headphone correction, so Tapline uses the raw difference between this measurement and your target.")
        case .crossRig where model.correctionReference != nil:
            return String(localized: "Measured on another rig than your headphones: Tapline compares it with \(referenceDatabase.siteName)'s own \(targetName) target, so the two rigs' differences cancel.")
        case .crossRig:
            return String(localized: "Tapline compares this measurement with \(referenceDatabase.siteName)'s own \(targetName) target, so the rig's colouring cancels. Most accurate when your headphones are corrected to the same kind of target.")
        }
    }

    private func simulation(from result: EQFitter.Result) -> DeviceSimulation {
        DeviceSimulation(id: phone.id, name: phone.displayName, kind: .measured, bands: result.bands, source: phone.database.siteName)
    }

    private func load() async {
        do {
            device = try await squig.measurement(for: phone)
        } catch {
            self.error = error.localizedDescription
        }
        let correction = model.correctionReference
        comparison = DeviceSimulation.comparison(deviceDatabase: phone.database.id, correction: correction)
        var db = phone.database
        if comparison == .sameRig, let correction, let own = squig.databases.first(where: { $0.id == correction.targetDatabase }) {
            db = own
        }
        var names = await squig.targets(for: db)
        if names.isEmpty, let fallback = await squig.fallbackTargetDatabase(for: db.kind) {
            db = fallback
            names = await squig.targets(for: fallback)
        }
        targets = names
        referenceDatabase = db
        targetName = DeviceSimulation.plan(deviceDatabase: phone.database.id, targetDatabase: db.id, targets: names, correction: correction)?.referenceTarget
        if names.isEmpty { error = String(localized: "This database does not publish target curves.") }
    }

    private func refit() async {
        guard let device, let referenceDatabase, let targetName, !targetName.isEmpty else { return }
        try? await Task.sleep(for: .milliseconds(150))
        if Task.isCancelled { return }
        computing = true
        defer { computing = false }
        do {
            let reference = try await squig.target(named: targetName, in: referenceDatabase)
            let fitted = await Task.detached(priority: .userInitiated) {
                DeviceSimulation.fit(device: device, reference: reference)
            }.value
            if Task.isCancelled { return }
            result = fitted
            error = nil
            // Already listening to this device: follow the new reference right away.
            if playingThis { model.chooseSimulation(simulation(from: fitted)) }
        } catch {
            self.error = error.localizedDescription
        }
    }
}

private struct ChartLegend: View {
    let color: Color
    let dashed: Bool
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

// MARK: - Names

extension SpeakerProfile {
    var title: LocalizedStringResource {
        switch self {
        case .phone: "Phone speaker"
        case .laptop: "Laptop speakers"
        case .tv: "TV speakers"
        case .bluetoothSpeaker: "Small Bluetooth speaker"
        case .car: "Car"
        }
    }

    var symbolName: String {
        switch self {
        case .phone: "iphone"
        case .laptop: "laptopcomputer"
        case .tv: "tv"
        case .bluetoothSpeaker: "hifispeaker"
        case .car: "car"
        }
    }

    /// How the device's speakers handle the two channels.
    var channelsTitle: LocalizedStringResource {
        if width == 0 { return "Mono" }
        return width < 0.5 ? "Narrow stereo" : "Narrower stereo"
    }
}

extension DeviceSimulation {
    /// The name to show: built-in profiles in the user's language.
    var title: String { profile.map { String(localized: $0.title) } ?? name }

    var symbolName: String { profile?.symbolName ?? "headphones" }

    /// "Approximation · Mono", or who measured it.
    var detail: String {
        if let profile { return String(localized: "Approximation · \(String(localized: profile.channelsTitle))") }
        return source.map { String(localized: "Measurement by \($0)") } ?? ""
    }
}
