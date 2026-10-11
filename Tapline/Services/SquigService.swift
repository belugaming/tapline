import CryptoKit
import Foundation
import Observation
import TaplineCore

/// Searches the squig.link network of frequency-response databases
/// (https://squig.link) and downloads measurements and target curves.
///
/// Sites listed in squig.link's own opt-out list are never contacted.
@MainActor
@Observable
final class SquigService {
    enum Kind: String, CaseIterable, Identifiable, Sendable {
        case headphones = "Headphones"
        case iems = "IEMs"
        case earbuds = "Earbuds"
        case b5128 = "5128"

        var id: String { rawValue }

        var title: LocalizedStringResource {
            switch self {
            case .headphones: "Headphones"
            case .iems: "In-ear"
            case .earbuds: "Earbuds"
            case .b5128: "B&K 5128"
            }
        }
    }

    struct Database: Identifiable, Hashable, Sendable {
        let siteName: String
        let username: String
        let kind: Kind
        /// Ends with a slash, e.g. https://aden.squig.link/headphones/
        let baseURL: URL

        var id: String { baseURL.absoluteString }
    }

    struct Phone: Identifiable, Hashable, Sendable {
        let brand: String
        let model: String
        /// File stem in the database's data directory, e.g. "Apple AirPods Pro 3 (ANC - vol-4)".
        let file: String
        let database: Database
        /// Search words of brand, model and file, split once when the phone book loads.
        let words: [String]

        init(brand: String, model: String, file: String, database: Database) {
            self.brand = brand
            self.model = model
            self.file = file
            self.database = database
            words = SquigService.words("\(brand) \(model) \(file)")
        }

        var id: String { database.id + "|" + file }

        /// Variant shown to the user: the file name usually carries the mode (ANC, wired…).
        var displayName: String {
            file.localizedCaseInsensitiveContains(model) ? file : "\(brand) \(model) · \(file)"
        }
    }

    enum SquigError: LocalizedError {
        case missingMeasurement
        case missingTarget

        var errorDescription: String? {
            switch self {
            case .missingMeasurement: String(localized: "Measurement file not found on the site.")
            case .missingTarget: String(localized: "Target curve not found on the site.")
            }
        }
    }

    private(set) var databases: [Database] = []
    private(set) var phones: [Phone] = []
    private(set) var loadedDatabases = 0
    private(set) var isLoading = false
    private(set) var errorMessage: String?

    private var targetCache: [String: [String]] = [:]
    private var dataDirectory: [String: String] = [:]
    private var fallbackTargets: [Kind: Database] = [:]

    private static let directoryURL = URL(string: "https://squig.link/squigsites.json")!
    private static let optOutURL = URL(string: "https://squig.link/squigsites.js")!
    /// Fallback copy of squig.link's opt-out list, used if it cannot be downloaded.
    private static let knownOptOuts: Set<String> = ["64audio", "bloomaudio", "cammyfi", "crinacle", "eliseaudio", "hbb", "joycesreview", "kr0mka", "graph", "vsg"]

    nonisolated private let cacheDirectory: URL = {
        let base = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        let dir = base.appendingPathComponent("io.github.belugaming.Tapline/squig", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }()

    // MARK: - Loading

    func loadAll(force: Bool = false) async {
        if isLoading || (!force && !phones.isEmpty) { return }
        isLoading = true
        errorMessage = nil
        loadedDatabases = 0
        defer { isLoading = false }

        do {
            let optOuts = await fetchOptOuts()
            let directory = try await cachedData(from: Self.directoryURL, maxAge: force ? 0 : 7 * 86_400)
            databases = Self.parseDirectory(directory).filter { !optOuts.contains($0.username) }
        } catch {
            errorMessage = error.localizedDescription
            return
        }

        var collected: [Phone] = []
        await withTaskGroup(of: [Phone].self) { group in
            var iterator = databases.makeIterator()
            func enqueue() {
                guard let db = iterator.next() else { return }
                group.addTask { await self.loadPhoneBook(db, force: force) }
            }
            for _ in 0..<8 { enqueue() }
            for await batch in group {
                collected.append(contentsOf: batch)
                loadedDatabases += 1
                enqueue()
            }
        }
        phones = collected
    }

    private func fetchOptOuts() async -> Set<String> {
        guard let data = try? await cachedData(from: Self.optOutURL, maxAge: 7 * 86_400),
              let js = String(data: data, encoding: .utf8),
              let range = js.range(of: #"optedOut\s*=\s*\[([^\]]*)\]"#, options: .regularExpression) else {
            return Self.knownOptOuts
        }
        let names = Self.quotedStrings(in: String(js[range]))
        return names.isEmpty ? Self.knownOptOuts : Set(names).union(Self.knownOptOuts)
    }

    nonisolated private func loadPhoneBook(_ db: Database, force: Bool) async -> [Phone] {
        guard let url = URL(string: "data/phone_book.json", relativeTo: db.baseURL)?.absoluteURL,
              let data = try? await cachedData(from: url, maxAge: force ? 0 : 7 * 86_400),
              let json = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return [] }
        var result: [Phone] = []
        for brandEntry in json {
            let brand = brandEntry["name"] as? String ?? ""
            for item in brandEntry["phones"] as? [Any] ?? [] {
                if let name = item as? String {
                    result.append(Phone(brand: brand, model: name, file: name, database: db))
                } else if let dict = item as? [String: Any] {
                    let model = dict["name"] as? String ?? ""
                    let files: [String]
                    if let list = dict["file"] as? [String] { files = list }
                    else if let one = dict["file"] as? String { files = [one] }
                    else { files = [model] }
                    for file in files where !file.isEmpty {
                        result.append(Phone(brand: brand, model: model, file: file, database: db))
                    }
                }
            }
        }
        return result
    }

    // MARK: - Search

    func search(_ query: String, kinds: Set<Kind>, limit: Int = 150) -> [Phone] {
        Self.search(query, in: phones, kinds: kinds, limit: limit)
    }

    /// Every query word must start a word of the entry, so "pro 3" does not match "53dB".
    /// Tens of thousands of entries: call it off the main thread while the user types.
    nonisolated static func search(_ query: String, in phones: [Phone], kinds: Set<Kind>, limit: Int = 150) -> [Phone] {
        let tokens = words(query)
        guard !tokens.isEmpty else { return [] }
        let phrase = tokens.joined(separator: " ")
        let scored: [(phone: Phone, score: Int)] = phones.compactMap { p in
            guard kinds.contains(p.database.kind),
                  tokens.allSatisfy({ t in p.words.contains { $0.hasPrefix(t) } }) else { return nil }
            var s = p.words.joined(separator: " ").contains(phrase) ? 100 : 0
            s += tokens.filter { p.words.contains($0) }.count * 10
            return (p, s)
        }
        return scored.sorted { a, b in
            if a.score != b.score { return a.score > b.score }
            let x = a.phone, y = b.phone
            if x.model != y.model { return x.model.localizedCaseInsensitiveCompare(y.model) == .orderedAscending }
            if x.database.siteName != y.database.siteName { return x.database.siteName < y.database.siteName }
            return x.file < y.file
        }.prefix(limit).map { $0.phone }
    }

    nonisolated static func words(_ s: String) -> [String] {
        s.lowercased().split(whereSeparator: { !$0.isLetter && !$0.isNumber }).map(String.init)
    }

    // MARK: - Measurements & targets

    nonisolated static func resolve(_ path: String, against base: URL) -> URL? {
        guard let encoded = path.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) else { return nil }
        return URL(string: encoded, relativeTo: base)?.absoluteURL
    }

    func measurement(for phone: Phone) async throws -> FrequencyResponse {
        let dir = await dataDir(for: phone.database)
        var channels: [FrequencyResponse] = []
        for suffixes in [[" L", " R"], [" L1", " R1"], [""]] {
            for suffix in suffixes {
                guard let url = Self.resolve(dir + phone.file + suffix + ".txt", against: phone.database.baseURL) else { continue }
                if let data = try? await cachedData(from: url, maxAge: 30 * 86_400),
                   let fr = FrequencyResponse.parse(String(decoding: data, as: UTF8.self)) {
                    channels.append(fr)
                }
            }
            if !channels.isEmpty { break }
        }
        guard let average = FrequencyResponse.average(channels, grid: EQFitter.grid) else { throw SquigError.missingMeasurement }
        return average
    }

    /// Target curves the site offers, read from its graph tool configuration.
    func targets(for db: Database) async -> [String] {
        if let cached = targetCache[db.id] { return cached }
        var names: [String] = []
        // The graph tool page names its config script, e.g. "../assets/js/config_hp.js".
        var candidates: [URL] = []
        if let page = try? await cachedData(from: db.baseURL, maxAge: 7 * 86_400),
           let html = String(data: page, encoding: .utf8),
           let r = html.range(of: #"src=["'][^"']*config[^"']*\.js"#, options: .regularExpression) {
            let src = String(html[r]).replacingOccurrences(of: #"^src=["']"#, with: "", options: .regularExpression)
            if let url = URL(string: src, relativeTo: db.baseURL)?.absoluteURL { candidates.append(url) }
        }
        candidates += ["config.js", "assets/js/config.js"].compactMap { URL(string: $0, relativeTo: db.baseURL)?.absoluteURL }
        for url in candidates {
            guard let data = try? await cachedData(from: url, maxAge: 7 * 86_400),
                  let js = String(data: data, encoding: .utf8), js.contains("targets") else { continue }
            let (dir, targets) = Self.parseConfig(js)
            dataDirectory[db.id] = dir
            names = targets
            break
        }
        targetCache[db.id] = names
        return names
    }

    func target(named name: String, in db: Database) async throws -> FrequencyResponse {
        let dir = await dataDir(for: db)
        for path in [dir + "targets/" + name + " Target.txt", dir + name + " Target.txt"] {
            guard let url = Self.resolve(path, against: db.baseURL) else { continue }
            if let data = try? await cachedData(from: url, maxAge: 30 * 86_400),
               let fr = FrequencyResponse.parse(String(decoding: data, as: UTF8.self)) {
                return fr
            }
        }
        throw SquigError.missingTarget
    }

    /// A database of the same kind that publishes a Harman target, for databases without targets.
    func fallbackTargetDatabase(for kind: Kind) async -> Database? {
        if let db = fallbackTargets[kind] { return db }
        for db in databases where db.kind == kind {
            let names = await targets(for: db)
            if names.contains(where: { $0.localizedCaseInsensitiveContains("harman") }) {
                fallbackTargets[kind] = db
                return db
            }
        }
        return nil
    }

    private func dataDir(for db: Database) async -> String {
        if let dir = dataDirectory[db.id] { return dir }
        _ = await targets(for: db)
        return dataDirectory[db.id] ?? "data/"
    }

    // MARK: - Parsing

    nonisolated static func parseDirectory(_ data: Data) -> [Database] {
        guard let sites = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return [] }
        var result: [Database] = []
        for site in sites {
            guard let username = site["username"] as? String else { continue }
            let name = site["name"] as? String ?? username
            let root: String
            switch site["urlType"] as? String {
            case "root": root = "https://squig.link"
            case "altDomain": root = (site["altDomain"] as? String) ?? ""
            case "subdomain": root = "https://\(username).squig.link"
            default: root = "https://squig.link/lab/\(username)"
            }
            guard !root.isEmpty else { continue }
            for db in site["dbs"] as? [[String: Any]] ?? [] {
                guard let type = db["type"] as? String, let kind = Kind(rawValue: type) else { continue }
                var folder = db["folder"] as? String ?? "/"
                if !folder.hasPrefix("/") { folder = "/" + folder }
                if !folder.hasSuffix("/") { folder += "/" }
                let trimmedRoot = root.hasSuffix("/") ? String(root.dropLast()) : root
                guard let url = URL(string: trimmedRoot + folder) else { continue }
                result.append(Database(siteName: name, username: username, kind: kind, baseURL: url))
            }
        }
        return result
    }

    /// Extracts `DIR` and the target file names from a squig graph tool config.js.
    nonisolated static func parseConfig(_ js: String) -> (dir: String, targets: [String]) {
        // Drop comments: whole commented lines and trailing " // ..." (keeps "https://").
        let cleaned = js.split(separator: "\n", omittingEmptySubsequences: false).compactMap { line -> String? in
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.hasPrefix("//") { return nil }
            if let r = line.range(of: #"\s//.*$"#, options: .regularExpression) { return String(line[..<r.lowerBound]) }
            return String(line)
        }.joined(separator: "\n")

        var dir = "data/"
        if let r = cleaned.range(of: #"DIR\s*=\s*["']([^"']+)["']"#, options: .regularExpression) {
            dir = quotedStrings(in: String(cleaned[r])).first ?? dir
        }
        var targets: [String] = []
        if let start = cleaned.range(of: #"targets\s*=\s*\["#, options: .regularExpression) {
            // Find the matching closing bracket of the targets array.
            var depth = 1
            var index = start.upperBound
            while index < cleaned.endIndex, depth > 0 {
                let ch = cleaned[index]
                if ch == "[" { depth += 1 } else if ch == "]" { depth -= 1 }
                index = cleaned.index(after: index)
            }
            let body = String(cleaned[start.upperBound..<index])
            var search = body.startIndex..<body.endIndex
            while let files = body.range(of: #"files\s*:\s*\[[^\]]*\]"#, options: .regularExpression, range: search) {
                targets += quotedStrings(in: String(body[files]))
                search = files.upperBound..<body.endIndex
            }
        }
        var seen = Set<String>()
        return (dir, targets.filter { seen.insert($0).inserted })
    }

    nonisolated static func quotedStrings(in s: String) -> [String] {
        var out: [String] = []
        var current = ""
        var quote: Character?
        for ch in s {
            if let q = quote {
                if ch == q { out.append(current); current = ""; quote = nil } else { current.append(ch) }
            } else if ch == "\"" || ch == "'" {
                quote = ch
            }
        }
        return out
    }

    // MARK: - HTTP cache

    nonisolated private func cachedData(from url: URL, maxAge: TimeInterval) async throws -> Data {
        let key = SHA256.hash(data: Data(url.absoluteString.utf8)).map { String(format: "%02x", $0) }.joined()
        let file = cacheDirectory.appendingPathComponent(key)
        let attributes = try? FileManager.default.attributesOfItem(atPath: file.path)
        if let modified = attributes?[.modificationDate] as? Date, Date().timeIntervalSince(modified) < maxAge,
           let data = try? Data(contentsOf: file) {
            return data
        }
        var request = URLRequest(url: url, timeoutInterval: 15)
        request.setValue("Tapline (https://github.com/belugaming/tapline)", forHTTPHeaderField: "User-Agent")
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw URLError(.fileDoesNotExist) }
            try? data.write(to: file)
            return data
        } catch {
            if let stale = try? Data(contentsOf: file) { return stale }
            throw error
        }
    }
}
