import Foundation
import Observation
import OSLog
import TaplineCore

/// A set of impulse responses for Virtual Speakers: built into the app or imported.
struct VirtualSpeakerSet: Codable, Hashable, Identifiable, Sendable {
    var id: String
    var name: String
    var layout: BinauralIRs.Layout
    var sampleRate: Double
    /// Samples at `sampleRate`.
    var length: Int
    var isBuiltIn = false

    var duration: Double { sampleRate > 0 ? Double(length) / sampleRate : 0 }
}

/// The sets to choose from. Imported sets are stored as 4-channel float WAV files in
/// Application Support, with their names in the user defaults.
@MainActor
@Observable
final class VirtualSpeakerLibrary {
    static let builtIn: [VirtualSpeakerSet] = [
        VirtualSpeakerSet(id: EffectsSettings.defaultVirtualSpeakers, name: String(localized: "Studio room (KU100)"),
                          layout: .fourChannel, sampleRate: 48_000, length: 0, isBuiltIn: true),
        VirtualSpeakerSet(id: "sadie-ku100", name: String(localized: "Anechoic (KU100)"),
                          layout: .fourChannel, sampleRate: 48_000, length: 0, isBuiltIn: true),
    ]

    private static let resources = [EffectsSettings.defaultVirtualSpeakers: "SADIE-II-KU100-Room", "sadie-ku100": "SADIE-II-KU100"]
    private static let indexKey = "virtualSpeakerSets"

    private(set) var imported: [VirtualSpeakerSet] = []

    var all: [VirtualSpeakerSet] { Self.builtIn + imported }

    @ObservationIgnored private let defaults = UserDefaults.standard
    @ObservationIgnored private var loaded: [String: BinauralIRs] = [:]
    @ObservationIgnored private var lastPrepared: (key: String, irs: BinauralIRs, gain: Float)?
    @ObservationIgnored private let log = Logger(subsystem: "io.github.belugaming.Tapline", category: "virtual-speakers")

    init() {
        if let data = defaults.data(forKey: DebugSnapshot.key(Self.indexKey)),
           let sets = try? JSONDecoder().decode([VirtualSpeakerSet].self, from: data) {
            imported = sets
        }
    }

    /// The set an id from the settings stands for; a removed import falls back to the default.
    func resolve(_ id: String) -> VirtualSpeakerSet {
        all.first { $0.id == id } ?? Self.builtIn[0]
    }

    /// Identifies a set prepared for one sample rate.
    static func key(_ id: String, sampleRate: Double) -> String { "\(id)@\(sampleRate)" }

    /// The set resampled to `sampleRate`, with its loudness-matching gain.
    func prepared(_ set: VirtualSpeakerSet, sampleRate: Double) throws -> (irs: BinauralIRs, gain: Float, key: String) {
        let key = Self.key(set.id, sampleRate: sampleRate)
        if let lastPrepared, lastPrepared.key == key { return (lastPrepared.irs, lastPrepared.gain, key) }
        let irs = try load(set).prepared(sampleRate: sampleRate)
        let gain = irs.loudnessGain
        lastPrepared = (key, irs, gain)
        log.info("prepared \(set.name, privacy: .public): \(irs.length) taps at \(Int(sampleRate)) Hz, gain \(String(format: "%.2f", gain), privacy: .public)")
        return (irs, gain, key)
    }

    private func load(_ set: VirtualSpeakerSet) throws -> BinauralIRs {
        if let irs = loaded[set.id] { return irs }
        let url: URL
        if let resource = Self.resources[set.id] {
            guard let bundled = Bundle.main.url(forResource: resource, withExtension: "wav") else { throw CocoaError(.fileNoSuchFile) }
            url = bundled
        } else {
            url = Self.directory.appendingPathComponent("\(set.id).wav")
        }
        let irs = try BinauralIRs(wav: WAVFile.parse(Data(contentsOf: url))).trimmed()
        loaded[set.id] = irs
        return irs
    }

    /// Reads one file (HeSuVi, Impulcifer, 4 channels, one stereo speaker) or two stereo
    /// files (one per speaker) and adds them as a new set.
    func importFiles(_ urls: [URL]) throws -> VirtualSpeakerSet {
        let files = try urls.map { try WAVFile.parse(Data(contentsOf: $0)) }
        let irs = try BinauralIRs(wavFiles: files).trimmed()
        let id = UUID().uuidString
        let set = VirtualSpeakerSet(id: id, name: Self.name(for: urls), layout: irs.layout,
                                    sampleRate: irs.sampleRate, length: irs.length)
        try FileManager.default.createDirectory(at: Self.directory, withIntermediateDirectories: true)
        try irs.wav.encoded().write(to: Self.directory.appendingPathComponent("\(id).wav"), options: .atomic)
        loaded[id] = irs
        imported.append(set)
        saveIndex()
        return set
    }

    func remove(_ id: String) {
        guard let i = imported.firstIndex(where: { $0.id == id }) else { return }
        imported.remove(at: i)
        loaded[id] = nil
        if lastPrepared?.key.hasPrefix(id + "@") == true { lastPrepared = nil }
        try? FileManager.default.removeItem(at: Self.directory.appendingPathComponent("\(id).wav"))
        saveIndex()
    }

    func rename(_ id: String, to name: String) {
        guard let i = imported.firstIndex(where: { $0.id == id }), !name.isEmpty else { return }
        imported[i].name = name
        saveIndex()
    }

    private func saveIndex() {
        guard !DebugSnapshot.isActive, let data = try? JSONEncoder().encode(imported) else { return }
        defaults.set(data, forKey: Self.indexKey)
    }

    private static var directory: URL {
        URL.applicationSupportDirectory.appendingPathComponent("Tapline/Virtual Speakers", isDirectory: true)
    }

    /// The file name, or for two files their common start ("Room L" + "Room R" → "Room").
    private static func name(for urls: [URL]) -> String {
        let names = urls.map { $0.deletingPathExtension().lastPathComponent }
        guard names.count == 2 else { return names.first ?? String(localized: "Imported speakers") }
        let common = String(zip(names[0], names[1]).prefix(while: { $0.0 == $0.1 }).map { $0.0 })
            .trimmingCharacters(in: CharacterSet(charactersIn: " -_.(").union(.whitespaces))
        return common.count >= 3 ? common : names[0]
    }

    /// What went wrong with an import, for the user.
    static func message(for error: Error) -> String {
        switch error {
        case BinauralIRs.ImportError.unsupportedChannelCount(let n):
            return String(localized: "That file has \(n) channels. Tapline reads HeSuVi files (7 or 14 channels), Impulcifer's hrir.wav (16), 4-channel files, or stereo files of one speaker each.")
        case BinauralIRs.ImportError.silent:
            return String(localized: "The front speakers in that file are silent.")
        case BinauralIRs.ImportError.sameSide:
            return String(localized: "Both files are the same speaker. Choose one file for the left speaker and one for the right.")
        case BinauralIRs.ImportError.sampleRateMismatch:
            return String(localized: "The two files have different sample rates.")
        case BinauralIRs.ImportError.wrongFileCount:
            return String(localized: "Choose one file, or two stereo files (one per speaker).")
        case is WAVFile.ParseError:
            return String(localized: "That is not a WAV file Tapline can read (PCM or 32/64-bit float).")
        default:
            return error.localizedDescription
        }
    }
}
