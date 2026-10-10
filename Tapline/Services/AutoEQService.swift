import CryptoKit
import Foundation
import Observation
import TaplineCore

/// Searches the AutoEQ project's headphone measurement database
/// (github.com/jaakkopasanen/AutoEq) and downloads parametric EQ profiles.
@MainActor
@Observable
final class AutoEQService {
    struct Entry: Identifiable, Hashable, Sendable {
        enum FormFactor: String, Sendable { case overEar, inEar, earbud, unknown }

        let name: String
        let source: String
        let rig: String?
        /// Percent-encoded path relative to results/, as listed in INDEX.md.
        let path: String
        let formFactor: FormFactor

        var id: String { path }

        var profileURL: URL? {
            let folder = path.split(separator: "/").last.map(String.init) ?? ""
            return URL(string: "\(AutoEQService.base)/\(path)/\(folder)%20ParametricEQ.txt")
        }

        var pageURL: URL? {
            URL(string: "https://github.com/jaakkopasanen/AutoEq/tree/master/results/\(path)")
        }
    }

    nonisolated static let base = "https://raw.githubusercontent.com/jaakkopasanen/AutoEq/master/results"
    private static let indexURL = URL(string: "\(base)/INDEX.md")!
    private static let sourcePriority = ["oratory1990", "crinacle", "Rtings", "Innerfidelity", "HypetheSonics", "Kuulokenurkka", "Headphone.com Legacy"]

    private(set) var entries: [Entry] = []
    private(set) var isLoading = false
    private(set) var errorMessage: String?
    private(set) var lastUpdated: Date?

    private let cacheDirectory: URL = {
        let base = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        let dir = base.appendingPathComponent("io.github.belugaming.Tapline/AutoEQ", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }()

    private var indexCache: URL { cacheDirectory.appendingPathComponent("INDEX.md") }

    func loadIndex(force: Bool = false) async {
        if !force, !entries.isEmpty { return }
        isLoading = true
        errorMessage = nil
        defer { isLoading = false }

        let attributes = try? FileManager.default.attributesOfItem(atPath: indexCache.path)
        let modified = attributes?[.modificationDate] as? Date
        let fresh = modified.map { Date().timeIntervalSince($0) < 7 * 86_400 } ?? false

        if !force, fresh, let text = try? String(contentsOf: indexCache, encoding: .utf8) {
            entries = Self.parseIndex(text)
            lastUpdated = modified
            return
        }
        do {
            let (data, response) = try await URLSession.shared.data(from: Self.indexURL)
            guard (response as? HTTPURLResponse)?.statusCode == 200, let text = String(data: data, encoding: .utf8) else {
                throw URLError(.badServerResponse)
            }
            try? data.write(to: indexCache)
            entries = Self.parseIndex(text)
            lastUpdated = Date()
        } catch {
            if let text = try? String(contentsOf: indexCache, encoding: .utf8) {
                entries = Self.parseIndex(text)
                lastUpdated = modified
            } else {
                errorMessage = error.localizedDescription
            }
        }
    }

    func search(_ query: String, limit: Int = 120) -> [Entry] {
        let tokens = query.lowercased().split(separator: " ").map(String.init).filter { !$0.isEmpty }
        guard !tokens.isEmpty else { return [] }
        let matches = entries.filter { e in
            let hay = e.name.lowercased()
            return tokens.allSatisfy { hay.contains($0) }
        }
        return matches.sorted { a, b in
            let q = query.lowercased()
            let ap = a.name.lowercased().hasPrefix(q), bp = b.name.lowercased().hasPrefix(q)
            if ap != bp { return ap }
            if a.name != b.name { return a.name.localizedCaseInsensitiveCompare(b.name) == .orderedAscending }
            return Self.rank(a.source) < Self.rank(b.source)
        }
        .prefix(limit)
        .map { $0 }
    }

    func fetchProfile(_ entry: Entry) async throws -> String {
        guard let url = entry.profileURL else { throw URLError(.badURL) }
        let key = SHA256.hash(data: Data(entry.path.utf8)).map { String(format: "%02x", $0) }.joined()
        let cached = cacheDirectory.appendingPathComponent("\(key).txt")
        if let text = try? String(contentsOf: cached, encoding: .utf8) { return text }
        let (data, response) = try await URLSession.shared.data(from: url)
        guard (response as? HTTPURLResponse)?.statusCode == 200, let text = String(data: data, encoding: .utf8) else {
            throw URLError(.fileDoesNotExist)
        }
        _ = try ParametricEQFormat.parse(text)
        try? data.write(to: cached)
        return text
    }

    private static func rank(_ source: String) -> Int {
        sourcePriority.firstIndex(of: source) ?? sourcePriority.count
    }

    /// Lines look like: `- [Bose QuietComfort 45](./oratory1990/over-ear/Bose%20QuietComfort%2045) by oratory1990 on GRAS`
    nonisolated static func parseIndex(_ text: String) -> [Entry] {
        var result: [Entry] = []
        result.reserveCapacity(9000)
        for line in text.split(separator: "\n", omittingEmptySubsequences: true) {
            guard line.hasPrefix("- ["),
                  let nameEnd = line.range(of: "](./"),
                  let pathEnd = line.range(of: ") by ", range: nameEnd.upperBound..<line.endIndex) else { continue }
            let name = String(line[line.index(line.startIndex, offsetBy: 3)..<nameEnd.lowerBound])
                .replacingOccurrences(of: "\u{200B}", with: "")
            let path = String(line[nameEnd.upperBound..<pathEnd.lowerBound])
            let tail = line[pathEnd.upperBound...]
            let parts = tail.components(separatedBy: " on ")
            let source = parts.first.map { String($0) } ?? ""
            let rig = parts.count > 1 ? parts.dropFirst().joined(separator: " on ") : nil
            let lower = path.lowercased()
            let form: Entry.FormFactor = lower.contains("in-ear") ? .inEar : lower.contains("earbud") ? .earbud : lower.contains("over-ear") ? .overEar : .unknown
            result.append(Entry(name: name, source: source, rig: rig, path: path, formFactor: form))
        }
        return result
    }
}
