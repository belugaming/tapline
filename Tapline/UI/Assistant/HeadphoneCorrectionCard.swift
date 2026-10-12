import SwiftUI
import TaplineCore
import UniformTypeIdentifiers

/// Headphone correction from three sources: ready-made AutoEQ profiles,
/// raw squig.link measurements fitted on the fly, or the user's own file.
struct HeadphoneCorrectionCard: View {
    enum Source: String, CaseIterable, Identifiable {
        case autoEQ, squig, file
        var id: String { rawValue }

        var title: LocalizedStringKey {
            switch self {
            case .autoEQ: "AutoEQ presets"
            case .squig: "squig.link measurements"
            case .file: "My measurement"
            }
        }
    }

    @AppStorage(DebugSnapshot.key("correctionSource")) private var source: Source = .autoEQ

    var body: some View {
        Card(title: "Headphone Correction", subtitle: "Correct your headphones towards a neutral target using published measurements.", systemImage: "headphones") {
            Picker("Source", selection: $source) {
                ForEach(Source.allCases) { Text($0.title).tag($0) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .frame(maxWidth: 520)

            switch source {
            case .autoEQ: AutoEQSection()
            case .squig: SquigSection()
            case .file: FileMeasurementSection()
            }
        }
    }
}

// MARK: - AutoEQ

private struct AutoEQSection: View {
    @Environment(AppModel.self) private var model
    @Environment(AutoEQService.self) private var autoEQ
    @AppStorage(DebugSnapshot.key("correctionKeepBass")) private var keepBass = false
    @State private var query = ""
    @State private var selected: AutoEQService.Entry?
    @State private var parsed: ParametricEQFormat.Parsed?
    @State private var loading = false
    @State private var failed = false
    @State private var applied = false

    private var previewBands: [EQBand] {
        guard let parsed else { return [] }
        return keepBass ? parsed.bands.filter { $0.frequency >= 250 } : parsed.bands
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Ready-made correction curves for 5,000+ headphones, computed by the AutoEQ project. The database was last updated in July 2025.")
                .font(.callout)
                .foregroundStyle(.secondary)
            SearchField(text: $query, prompt: "Headphone model, e.g. Bose 700, Sony WH-1000XM5", busy: autoEQ.isLoading)
            if let error = autoEQ.errorMessage {
                HStack {
                    Text("Could not load the database: \(error)").foregroundStyle(Theme.danger).font(.caption)
                    Button("Retry") { Task { await autoEQ.loadIndex(force: true) } }
                }
            }
            let results = autoEQ.search(query)
            HStack(alignment: .top, spacing: 14) {
                List(results, selection: Binding(get: { selected?.id }, set: { id in select(results.first { $0.id == id }) })) { entry in
                    ResultRow(symbol: entry.formFactor == .inEar || entry.formFactor == .earbud ? "earbuds" : "headphones",
                              title: entry.name,
                              subtitle: entry.rig.map { "\(entry.source) · \($0)" } ?? entry.source)
                        .tag(entry.id)
                }
                .listStyle(.bordered)
                .frame(width: 330, height: 280)
                .overlay { if results.isEmpty { EmptyHint(query: query) } }

                VStack(alignment: .leading, spacing: 10) {
                    if let selected {
                        Text(selected.name).font(.headline).lineLimit(2)
                        Text(selected.rig.map { "\(selected.source) · \($0)" } ?? selected.source)
                            .font(.caption).foregroundStyle(.secondary)
                        if let parsed {
                            CurveThumbnail(bands: previewBands, range: 12)
                                .frame(height: 100)
                                .padding(6)
                                .instrumentPanel(radius: 8)
                            Text("\(previewBands.count) of \(parsed.bands.count) filters")
                                .font(.caption).foregroundStyle(.secondary)
                        } else if loading {
                            ProgressView().frame(maxWidth: .infinity, minHeight: 100)
                        } else if failed {
                            Text("No parametric EQ available for this entry.").font(.caption).foregroundStyle(Theme.danger)
                        }
                        KeepBassToggle(isOn: $keepBass)
                        HStack {
                            Button {
                                model.applyCorrection(bands: previewBands, name: "AutoEQ · \(selected.name)", source: selected.source)
                                applied = true
                            } label: {
                                Label(applied ? "Applied" : "Apply Correction", systemImage: applied ? "checkmark" : "wand.and.stars")
                            }
                            .buttonStyle(.borderedProminent)
                            .disabled(previewBands.isEmpty)
                            if let url = selected.pageURL {
                                Link(destination: url) { Image(systemName: "arrow.up.right.square") }
                                    .help(Text("Open the measurement on GitHub"))
                            }
                        }
                    } else {
                        Tips()
                    }
                }
                .font(.callout)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .onChange(of: keepBass) { applied = false }
        .task { await autoEQ.loadIndex() }
    }

    private func select(_ entry: AutoEQService.Entry?) {
        selected = entry
        parsed = nil
        failed = false
        applied = false
        guard let entry else { return }
        loading = true
        Task {
            defer { loading = false }
            do {
                let text = try await autoEQ.fetchProfile(entry)
                guard selected == entry else { return }
                parsed = try ParametricEQFormat.parse(text)
            } catch {
                failed = true
            }
        }
    }
}

// MARK: - squig.link

private struct SquigSection: View {
    @Environment(SquigService.self) private var squig
    @AppStorage(DebugSnapshot.key("squigKind")) private var kindFilter = "all"
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
            Text("Raw measurements from \(squig.databases.count) reviewer databases on squig.link, updated continuously. Tapline computes the EQ for the target you choose.")
                .font(.callout)
                .foregroundStyle(.secondary)
            HStack(spacing: 10) {
                SearchField(text: $query, prompt: "Model, e.g. AirPods Pro 3, Bose NC700, HD 600", busy: squig.isLoading)
                Picker("Type", selection: $kindFilter) {
                    Text("All types").tag("all")
                    ForEach(SquigService.Kind.allCases) { Text($0.title).tag($0.rawValue) }
                }
                .labelsHidden()
                .frame(width: 140)
            }
            if squig.isLoading {
                ProgressView(value: Double(squig.loadedDatabases), total: Double(max(squig.databases.count, 1))) {
                    Text("Loading databases… \(squig.loadedDatabases) of \(squig.databases.count)")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                .frame(maxWidth: 360)
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
                .frame(width: 330, height: 420)
                .overlay { if results.isEmpty { EmptyHint(query: query) } }

                if let selected {
                    CorrectionWorkbench(
                        title: selected.displayName,
                        credit: selected.database.siteName,
                        creditURL: selected.database.baseURL,
                        database: selected.database,
                        presetName: "\(selected.file) · \(selected.database.siteName)",
                        loadMeasurement: { try await squig.measurement(for: selected) }
                    )
                    .id(selected.id)
                } else {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Tips").font(.headline)
                        Text("• Pick the mode you actually use: ANC on or off, wired, passive…")
                        Text("• Each database comes with its own target curves that match its measurement rig. Harman targets are a good start.")
                        Text("• Measurements belong to their reviewers. Visit their sites to support them.")
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
            // Searching tens of thousands of entries on every keystroke would stall typing:
            // wait for a pause, then search off the main thread.
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

// MARK: - File

private struct FileMeasurementSection: View {
    @Environment(SquigService.self) private var squig
    @State private var measurement: FrequencyResponse?
    @State private var fileName = ""
    @State private var targetDatabaseID: String?
    @State private var error: String?

    private var targetDatabase: SquigService.Database? {
        squig.databases.first { $0.id == targetDatabaseID } ?? squig.databases.first { $0.kind == .headphones }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Measured your headphones yourself (miniDSP EARS, REW…)? Import the frequency response as text or CSV and pick a target from a database that uses a similar rig.")
                .font(.callout)
                .foregroundStyle(.secondary)
            HStack(spacing: 10) {
                Button("Choose Measurement File…", action: choose)
                if !fileName.isEmpty {
                    Label(fileName, systemImage: "doc.text").font(.callout).lineLimit(1)
                }
                Spacer()
                Picker("Targets from", selection: Binding(get: { targetDatabase?.id ?? "" }, set: { targetDatabaseID = $0 })) {
                    ForEach(SquigService.Kind.allCases) { kind in
                        Section(String(localized: kind.title)) {
                            ForEach(squig.databases.filter { $0.kind == kind }) { db in
                                Text(db.siteName).tag(db.id)
                            }
                        }
                    }
                }
                .frame(maxWidth: 300)
            }
            if let error { Text(error).font(.caption).foregroundStyle(Theme.danger) }
            if let measurement, let db = targetDatabase {
                CorrectionWorkbench(
                    title: fileName,
                    credit: nil,
                    creditURL: nil,
                    database: db,
                    presetName: fileName,
                    loadMeasurement: { measurement }
                )
                .id("\(fileName)|\(db.id)")
            }
        }
        .task { await squig.loadAll() }
    }

    private func choose() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.plainText, .commaSeparatedText, .tabSeparatedText, UTType(filenameExtension: "txt") ?? .plainText, UTType(filenameExtension: "frd") ?? .plainText]
        guard panel.runModal() == .OK, let url = panel.url,
              let text = (try? String(contentsOf: url, encoding: .utf8)) ?? (try? String(contentsOf: url, encoding: .isoLatin1)) else { return }
        if let fr = FrequencyResponse.parse(text) {
            measurement = fr
            fileName = url.deletingPathExtension().lastPathComponent
            error = nil
        } else {
            error = String(localized: "No frequency response found in that file.")
        }
    }
}

// MARK: - Workbench

/// Target selection, preferences, preview and apply for a raw measurement.
private struct CorrectionWorkbench: View {
    @Environment(AppModel.self) private var model
    @Environment(SquigService.self) private var squig

    let title: String
    let credit: String?
    let creditURL: URL?
    let database: SquigService.Database
    let presetName: String
    let loadMeasurement: @MainActor () async throws -> FrequencyResponse

    @AppStorage(DebugSnapshot.key("fitBass")) private var bass = 0.0
    @AppStorage(DebugSnapshot.key("fitTilt")) private var tilt = 0.0
    @AppStorage(DebugSnapshot.key("fitFilters")) private var filterCount = 10
    @AppStorage(DebugSnapshot.key("correctionKeepBass")) private var keepBass = false

    @State private var measurement: FrequencyResponse?
    /// Where the targets come from: the measurement's own database, or a fallback of the same kind.
    @State private var targetDatabase: SquigService.Database?
    @State private var targets: [String] = []
    @State private var targetName: String?
    @State private var target: FrequencyResponse?
    @State private var result: EQFitter.Result?
    @State private var computing = false
    @State private var error: String?
    @State private var applied = false

    private struct FitKey: Hashable {
        var target: String?
        var bass: Double
        var tilt: Double
        var filters: Int
        var keepBass: Bool
        var ready: Bool
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.headline).lineLimit(2)
                if let credit {
                    HStack(spacing: 4) {
                        Text("Measurement by \(credit)")
                        if let creditURL {
                            Link(destination: creditURL) { Image(systemName: "arrow.up.right.square") }
                                .help(Text("Open the reviewer's database"))
                        }
                    }
                    .font(.caption)
                    .foregroundStyle(.secondary)
                }
            }

            HStack {
                Picker("Target", selection: Binding(get: { targetName ?? "" }, set: { targetName = $0 })) {
                    if targets.isEmpty { Text("Loading…").tag("") }
                    ForEach(targets, id: \.self) { Text($0).tag($0) }
                }
                .frame(maxWidth: 320)
                if computing { ProgressView().controlSize(.small) }
            }
            if let targetDatabase, targetDatabase != database {
                Text("This database has no target curves; using targets from \(targetDatabase.siteName).")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            if let result {
                MeasurementChart(result: result)
                    .frame(height: 170)
                HStack(spacing: 14) {
                    Legend(color: .gray, dashed: false, text: "Measurement")
                    Legend(color: .white, dashed: true, text: "Target")
                    Legend(color: Theme.accent, dashed: false, text: "With EQ")
                    Spacer()
                    Text(String(format: String(localized: "Deviation %.1f → %.1f dB"), result.errorBeforeDB, result.errorAfterDB))
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(.secondary)
                }
                .font(.caption)
            } else if let error {
                Text(error).font(.caption).foregroundStyle(Theme.danger)
            } else {
                ProgressView().frame(maxWidth: .infinity, minHeight: 170)
            }

            Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 6) {
                GridRow {
                    Text("Bass")
                    Slider(value: $bass, in: -6...10, step: 0.5)
                    Text(Format.gain(bass)).monospacedDigit().frame(width: 86, alignment: .trailing)
                }
                GridRow {
                    Text("Tilt")
                    Slider(value: $tilt, in: -1...1, step: 0.1)
                    Text(String(format: "%+.1f dB/oct", tilt)).monospacedDigit().frame(width: 86, alignment: .trailing)
                }
                GridRow {
                    Text("Filters")
                    Stepper(value: $filterCount, in: 3...16) { Text("\(filterCount)") }
                    Color.clear.frame(width: 1, height: 1)
                }
            }
            .font(.callout)
            KeepBassToggle(isOn: $keepBass)

            Button {
                guard let result else { return }
                model.applyCorrection(bands: result.bands, name: presetName, source: credit ?? String(localized: "My measurement"))
                applied = true
            } label: {
                Label(applied ? "Applied" : "Apply Correction", systemImage: applied ? "checkmark" : "wand.and.stars")
            }
            .buttonStyle(.borderedProminent)
            .disabled(result == nil || computing)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .task {
            do {
                measurement = try await loadMeasurement()
            } catch {
                self.error = error.localizedDescription
            }
            targets = await squig.targets(for: database)
            targetDatabase = database
            if targets.isEmpty, let fallback = await squig.fallbackTargetDatabase(for: database.kind) {
                targetDatabase = fallback
                targets = await squig.targets(for: fallback)
            }
            if targetName == nil {
                targetName = targets.first { $0.localizedCaseInsensitiveContains("harman") } ?? targets.first
            }
            if targets.isEmpty { error = String(localized: "This database does not publish target curves.") }
        }
        .task(id: FitKey(target: targetName, bass: bass, tilt: tilt, filters: filterCount, keepBass: keepBass, ready: measurement != nil)) {
            await refit()
        }
    }

    private func refit() async {
        guard let measurement, let targetName, !targetName.isEmpty else { return }
        applied = false
        try? await Task.sleep(for: .milliseconds(200))
        if Task.isCancelled { return }
        computing = true
        defer { computing = false }
        do {
            let target = try await squig.target(named: targetName, in: targetDatabase ?? database)
            var options = EQFitter.Options(maxFilters: filterCount, bassAdjustDB: bass, tiltDBPerOctave: tilt, keepBassBelow: keepBass ? 250 : nil)
            options.maxFilters = filterCount
            let fitted = await Task.detached(priority: .userInitiated) {
                EQFitter.fit(measurement: measurement, target: target, options: options)
            }.value
            if Task.isCancelled { return }
            result = fitted
            error = nil
        } catch {
            self.error = error.localizedDescription
        }
    }
}

// MARK: - Shared pieces

private struct KeepBassToggle: View {
    @Binding var isOn: Bool

    var body: some View {
        Toggle(isOn: $isOn) {
            VStack(alignment: .leading, spacing: 1) {
                Text("Keep my bass (don't correct below 250 Hz)")
                Text("Useful when you use the headphones differently from how they were measured, e.g. a Bose powered off.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .toggleStyle(.checkbox)
    }
}

private struct SearchField: View {
    @Binding var text: String
    let prompt: LocalizedStringKey
    var busy = false

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
            TextField(prompt, text: $text)
                .textFieldStyle(.plain)
            if busy { ProgressView().controlSize(.small) }
        }
        .padding(8)
        .background(Color.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 8))
    }
}

private struct ResultRow: View {
    let symbol: String
    let title: String
    let subtitle: String

    var body: some View {
        HStack {
            Image(systemName: symbol)
                .foregroundStyle(.secondary)
                .frame(width: 18)
            VStack(alignment: .leading, spacing: 1) {
                Text(title).lineLimit(1)
                Text(subtitle)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
        }
    }
}

private struct EmptyHint: View {
    let query: String

    var body: some View {
        Text(query.isEmpty ? "Type a model name to search" : "No matches")
            .foregroundStyle(.secondary)
    }
}

private struct Tips: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Tips").font(.headline)
            Text("• Pick the variant that matches how you use the headphones: \"ANC off\", \"passive\" or \"wired\" when they are powered off or used with a cable.")
            Text("• Results from oratory1990 and crinacle are listed first when available.")
            Text("• The curve is saved as a preset, so you can tweak it afterwards.")
        }
    }
}

private struct Legend: View {
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

/// Measurement, target and predicted response on one log-frequency chart, optionally
/// with a band for the spread between measured positions and each position's curve.
struct MeasurementChart: View {
    let result: EQFitter.Result
    var spreadLow: [Double] = []
    var spreadHigh: [Double] = []
    var positions: [[Double]] = []

    var body: some View {
        Canvas { ctx, size in
            let rect = CGRect(origin: .zero, size: size).insetBy(dx: 30, dy: 10).offsetBy(dx: 12, dy: -4)
            let f = result.frequencies
            let all = result.measurement + result.target + result.predicted
            let lo = ((all.min() ?? -10) / 5).rounded(.down) * 5 - 5
            let hi = ((all.max() ?? 10) / 5).rounded(.up) * 5 + 5
            func x(_ freq: Double) -> CGFloat { rect.minX + CGFloat(log(freq / 20) / log(1000)) * rect.width }
            func y(_ db: Double) -> CGFloat { rect.maxY - CGFloat((db - lo) / (hi - lo)) * rect.height }

            for freq in [20.0, 50, 100, 200, 500, 1000, 2000, 5000, 10_000, 20_000] {
                let px = x(freq)
                ctx.stroke(Path { $0.move(to: CGPoint(x: px, y: rect.minY)); $0.addLine(to: CGPoint(x: px, y: rect.maxY)) }, with: .color(Theme.gridMinor), lineWidth: 0.5)
                ctx.draw(Text(Format.gridFrequency(freq)).font(.system(size: 8)).foregroundStyle(Theme.gridLabel), at: CGPoint(x: px, y: rect.maxY + 7))
            }
            var db = lo
            while db <= hi {
                let py = y(db)
                ctx.stroke(Path { $0.move(to: CGPoint(x: rect.minX, y: py)); $0.addLine(to: CGPoint(x: rect.maxX, y: py)) }, with: .color(Theme.gridMinor), lineWidth: 0.5)
                ctx.draw(Text("\(Int(db))").font(.system(size: 8).monospacedDigit()).foregroundStyle(Theme.gridLabel), at: CGPoint(x: rect.minX - 4, y: py), anchor: .trailing)
                db += 10
            }

            func line(_ values: [Double]) -> Path {
                var p = Path()
                for (i, v) in values.enumerated() {
                    let pt = CGPoint(x: x(f[i]), y: y(v))
                    if i == 0 { p.move(to: pt) } else { p.addLine(to: pt) }
                }
                return p
            }
            // Spread and positions can reach past the scale of the main curves: clip them.
            var inside = ctx
            inside.clip(to: Path(rect))
            if spreadLow.count == f.count, spreadHigh.count == f.count {
                var band = line(spreadHigh)
                for i in f.indices.reversed() { band.addLine(to: CGPoint(x: x(f[i]), y: y(spreadLow[i]))) }
                band.closeSubpath()
                inside.fill(band, with: .color(.gray.opacity(0.3)))
            }
            for curve in positions where curve.count == f.count {
                inside.stroke(line(curve), with: .color(.white.opacity(0.22)), lineWidth: 0.8)
            }
            ctx.stroke(line(result.measurement), with: .color(.gray), lineWidth: 1.2)
            ctx.stroke(line(result.target), with: .color(.white.opacity(0.8)), style: StrokeStyle(lineWidth: 1.2, dash: [4, 3]))
            ctx.stroke(line(result.predicted), with: .color(Theme.accent), lineWidth: 1.8)
        }
        .instrumentPanel(radius: 8)
    }
}
