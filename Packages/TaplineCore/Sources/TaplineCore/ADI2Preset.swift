import Foundation

/// The parametric EQ of an RME ADI-2 DAC and its preset file (`.adieqpr`), the
/// format RME's ADI-2 Remote app saves and loads in its PEQ Editor:
///
///     	<ADI-2DAC>
///     		<General>
///     			<val e="Version" v="2"/>
///     		</General>
///     		<EQPreset1Left>
///     			<val e="EQEnable" v="1.00,"/>
///     			<val e="EQBand1Type" v="0.00,"/>
///     			<val e="EQBand1Gain" v="-4.50,"/>
///     			...
///
/// Band 1 and 5 types: 0 = peak, 1 = shelf (the factory default), 2 = cut.
/// Tapline exports bells to the five bands and shelves to Bass / Treble.
public enum ADI2Preset {
    /// Bands 1-3 cover 20 Hz - 20 kHz at Q 0.5 - 9.9, bands 4 and 5 start at
    /// 200 Hz and stop at Q 5.0 (ADI-2 DAC FS manual, chapter 12.2).
    public static let slots: [EQFitter.Slot] = [
        EQFitter.Slot(frequencyRange: 20...20_000, qRange: 0.5...9.9),
        EQFitter.Slot(frequencyRange: 20...20_000, qRange: 0.5...9.9),
        EQFitter.Slot(frequencyRange: 20...20_000, qRange: 0.5...9.9),
        EQFitter.Slot(frequencyRange: 200...20_000, qRange: 0.5...5.0),
        EQFitter.Slot(frequencyRange: 200...20_000, qRange: 0.5...5.0),
    ]
    public static let gainRange: ClosedRange<Double> = -12...12
    public static let gainStepDB = 0.5
    public static let qStep = 0.1
    public static let fileExtension = "adieqpr"

    /// Fitter options that only produce what the ADI-2 can be set to.
    public static func fitOptions(_ base: EQFitter.Options) -> EQFitter.Options {
        var o = base
        o.slots = slots
        o.gainStepDB = gainStepDB
        o.qStep = qStep
        o.maxBoostDB = min(o.maxBoostDB, gainRange.upperBound)
        o.maxCutDB = min(o.maxCutDB, -gainRange.lowerBound)
        o.maxQ = min(o.maxQ, 9.9)
        return o
    }

    /// Bass and Treble are fixed shelves (manual, chapter 12.3).
    static let bassRange: ClosedRange<Double> = 20...150
    static let trebleRange: ClosedRange<Double> = 3000...10_000
    static let shelfQRange: ClosedRange<Double> = 0.5...1.5

    public enum Error: Swift.Error, Equatable {
        /// More bells than the ADI-2 has, or bells it cannot place (bands 4 and 5 start at 200 Hz and stop at Q 5).
        case doesNotFit
        /// Filters other than bells, one low shelf (Bass, 20-150 Hz) and one high shelf (Treble, 3-10 kHz).
        case unsupportedFilter(FilterType)
        case notAPreset
    }

    // MARK: - Export

    /// Builds a preset file. With `right`, Dual EQ is switched on and each
    /// channel gets its own bands.
    public static func export(left: [EQBand], right: [EQBand]? = nil, name: String) throws -> String {
        let l = try placed(left.filter { $0.type == .peak || !$0.type.usesGain })
        let r = try right.map { try placed($0.filter { $0.type == .peak || !$0.type.usesGain }) }
        let shelves = try toneShelves(left + (right ?? []))

        var lines = ["\t<ADI-2DAC>", "\t\t<General>", "\t\t\t<val e=\"Version\" v=\"2\"/>", "\t\t</General>"]
        func val(_ key: String, _ value: Double) {
            lines.append("\t\t\t<val e=\"\(key)\" v=\"\(String(format: "%.2f", value)),\"/>")
        }
        func bands(_ slots: [EQBand?]) {
            val("EQEnable", 1)
            for (i, band) in slots.enumerated() {
                let n = i + 1
                let b = band ?? EQBand(frequency: defaultFrequencies[i])
                if n == 1 || n == 5 { val("EQBand\(n)Type", 0) }
                val("EQBand\(n)Gain", b.gain)
                val("EQBand\(n)Freq", b.frequency)
                val("EQBand\(n)Q", b.q)
            }
        }

        lines.append("\t\t<EQPreset1Left>")
        bands(l)
        // Shelves go to Bass / Treble, loaded with the preset. Without shelves they
        // keep the factory values and are not loaded, so the unit's tone controls stay as they are.
        let bass = shelves.bass, treble = shelves.treble
        val("BassTrebleEn", 1)
        val("EQBassGain", bass?.gain ?? 0)
        val("EQBassFreq", bass?.frequency ?? 85)
        val("EQBassQ", bass?.q ?? 0.9)
        val("EQTrebleGain", treble?.gain ?? 0)
        val("EQTrebleFreq", treble?.frequency ?? 6500)
        val("EQTrebleQ", treble?.q ?? 0.7)
        val("DualEQ", r == nil ? 0 : 1)
        val("EQ Preset Select", 1)
        val("Load BT w. EQ", bass != nil || treble != nil ? 1 : 0)
        lines.append("\t\t</EQPreset1Left>")
        lines.append("\t\t<EQPreset1Right>")
        bands(r ?? l)
        lines.append("\t\t</EQPreset1Right>")
        lines.append("\t\t<PresetNames>")
        lines.append("\t\t\t<val e=\"EQPresetName1\" v=\"\(presetName(name))\"/>")
        lines.append("\t\t</PresetNames>")
        lines.append("\t</ADI-2DAC>")
        return lines.joined(separator: "\n") + "\n"
    }

    private static let defaultFrequencies: [Double] = [100, 500, 1000, 5000, 10_000]

    static func rounded(_ b: EQBand) -> EQBand {
        var b = b
        b.gain = ((b.gain / gainStepDB).rounded() * gainStepDB).clamped(to: gainRange)
        b.q = max(qStep, (b.q / qStep).rounded() * qStep)
        b.frequency = EQFitter.roundedFrequency(b.frequency).clamped(to: 20...20_000)
        return b
    }

    /// Rounds the bells to the hardware's steps and puts each into a band slot.
    static func placed(_ bands: [EQBand]) throws -> [EQBand?] {
        let active = bands.filter { $0.isEnabled && (!$0.type.usesGain || abs($0.gain) >= gainStepDB / 2) }
        if let other = active.first(where: { $0.type != .peak }) { throw Error.unsupportedFilter(other.type) }
        let rounded = active.map(Self.rounded)
        guard let placement = EQFitter.assign(rounded.map { ($0.frequency, $0.q) }, to: slots) else { throw Error.doesNotFit }
        var out = [EQBand?](repeating: nil, count: slots.count)
        for (i, slot) in placement.enumerated() { out[slot] = rounded[i] }
        return out
    }

    /// At most one low shelf for Bass and one high shelf for Treble (the same on both channels).
    static func toneShelves(_ bands: [EQBand]) throws -> (bass: EQBand?, treble: EQBand?) {
        func pick(_ type: FilterType, _ range: ClosedRange<Double>) throws -> EQBand? {
            let found = bands.filter { $0.isEnabled && $0.type == type && abs($0.gain) >= gainStepDB / 2 }
            guard let first = found.first else { return nil }
            func same(_ a: EQBand, _ b: EQBand) -> Bool { a.frequency == b.frequency && a.gain == b.gain && a.q == b.q }
            guard found.allSatisfy({ same($0, first) }), range.contains(first.frequency.rounded()) else { throw Error.unsupportedFilter(type) }
            var b = rounded(first)
            b.frequency = type == .highShelf ? (first.frequency / 100).rounded() * 100 : first.frequency.rounded()
            b.q = b.q.clamped(to: shelfQRange)
            return b
        }
        return (try pick(.lowShelf, bassRange), try pick(.highShelf, trebleRange))
    }

    /// Printable ASCII, no XML specials, at most 20 characters.
    static func presetName(_ name: String) -> String {
        let allowed = name.unicodeScalars.filter { $0.isASCII && $0.value >= 32 && !"<>&\"".unicodeScalars.contains($0) }
        let s = String(String.UnicodeScalarView(allowed)).trimmingCharacters(in: .whitespaces)
        return String((s.isEmpty ? "Tapline" : s).prefix(20))
    }

    // MARK: - Import

    public struct Parsed: Sendable, Equatable {
        public var name: String?
        /// Bands as Tapline sees them: per-channel when Dual EQ was on.
        public var bands: [EQBand]
        public var isEnabled: Bool
    }

    /// Reads a preset file. Bass / Treble become shelves when the preset loads them.
    public static func parse(_ text: String) throws -> Parsed {
        let sections = Self.sections(text)
        guard let left = sections["EQPreset1Left"] else { throw Error.notAPreset }
        let right = sections["EQPreset1Right"]
        let dual = (left["DualEQ"] ?? 0) >= 0.5 && right != nil

        func bands(_ values: [String: Double], channels: EQChannels) -> [EQBand] {
            var out: [EQBand] = []
            for n in 1...5 {
                guard let gain = values["EQBand\(n)Gain"], let freq = values["EQBand\(n)Freq"], let q = values["EQBand\(n)Q"] else { continue }
                let code = Int((values["EQBand\(n)Type"] ?? 0).rounded())
                let type: FilterType
                switch (n, code) {
                case (1, 1): type = .lowShelf
                case (5, 1): type = .highShelf
                case (1, 2...): type = .highPass
                case (5, 2...): type = .lowPass
                default: type = .peak
                }
                if type.usesGain && abs(gain) < 0.05 { continue }
                out.append(EQBand(type: type, frequency: freq, gain: type.usesGain ? gain : 0, q: q, channels: channels).clamped())
            }
            return out
        }

        var result = dual ? bands(left, channels: .left) + bands(right!, channels: .right) : bands(left, channels: .both)
        if (left["Load BT w. EQ"] ?? 0) >= 0.5, (left["BassTrebleEn"] ?? 1) >= 0.5 {
            if let g = left["EQBassGain"], abs(g) >= 0.05 {
                result.insert(EQBand(type: .lowShelf, frequency: left["EQBassFreq"] ?? 85, gain: g, q: left["EQBassQ"] ?? 0.9).clamped(), at: 0)
            }
            if let g = left["EQTrebleGain"], abs(g) >= 0.05 {
                result.append(EQBand(type: .highShelf, frequency: left["EQTrebleFreq"] ?? 6500, gain: g, q: left["EQTrebleQ"] ?? 0.7).clamped())
            }
        }
        return Parsed(name: names(text).first, bands: result, isEnabled: (left["EQEnable"] ?? 1) >= 0.5)
    }

    /// Numeric `<val e=".." v=".."/>` entries per section.
    private static func sections(_ text: String) -> [String: [String: Double]] {
        var out: [String: [String: Double]] = [:]
        var current: String?
        for raw in text.split(whereSeparator: \.isNewline) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("</") { current = nil; continue }
            if line.hasPrefix("<val ") {
                if let section = current, let (key, value) = entry(line),
                   let number = Double(value.trimmingCharacters(in: CharacterSet(charactersIn: ", "))), number.isFinite {
                    out[section, default: [:]][key] = number
                }
                continue
            }
            if line.hasPrefix("<"), line.hasSuffix(">"), !line.hasSuffix("/>") {
                current = String(line.dropFirst().dropLast())
                if out[current!] == nil { out[current!] = [:] }
            }
        }
        return out
    }

    private static func names(_ text: String) -> [String] {
        text.split(whereSeparator: \.isNewline).compactMap { raw in
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard let (key, value) = entry(line), key.hasPrefix("EQPresetName") else { return nil }
            return value
        }
    }

    private static func entry(_ line: String) -> (String, String)? {
        guard let e = line.range(of: "e=\""), let eEnd = line[e.upperBound...].firstIndex(of: "\""),
              let v = line.range(of: "v=\"", range: eEnd..<line.endIndex),
              let vEnd = line[v.upperBound...].firstIndex(of: "\"") else { return nil }
        return (String(line[e.upperBound..<eEnd]), String(line[v.upperBound..<vEnd]))
    }
}
