import Foundation

/// Reads and writes the Equalizer APO / AutoEQ "ParametricEQ.txt" format:
///
///     Preamp: -6.4 dB
///     Filter 1: ON LSC Fc 105 Hz Gain 4.2 dB Q 0.70
///     Filter 2: ON PK Fc 132 Hz Gain -10.8 dB Q 1.01
public enum ParametricEQFormat {
    public struct Parsed: Sendable, Equatable {
        public var preampDB: Double?
        public var bands: [EQBand]
    }

    public enum ParseError: Error, Equatable {
        case noFilters
    }

    public static func parse(_ text: String) throws -> Parsed {
        var preamp: Double?
        var bands: [EQBand] = []

        for rawLine in text.components(separatedBy: .newlines) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line.isEmpty || line.hasPrefix("#") { continue }
            let lower = line.lowercased()

            if lower.hasPrefix("preamp:") {
                let tokens = tokenize(String(line.dropFirst("preamp:".count)))
                preamp = tokens.compactMap(Double.init).first
                continue
            }
            guard lower.hasPrefix("filter") else { continue }
            guard let colon = line.firstIndex(of: ":") else { continue }
            let tokens = tokenize(String(line[line.index(after: colon)...]))
            guard let band = parseFilter(tokens) else { continue }
            bands.append(band)
        }

        guard !bands.isEmpty else { throw ParseError.noFilters }
        return Parsed(preampDB: preamp, bands: bands)
    }

    private static func tokenize(_ s: String) -> [String] {
        s.split(whereSeparator: { $0 == " " || $0 == "\t" }).map(String.init)
    }

    private static func parseFilter(_ tokens: [String]) -> EQBand? {
        guard tokens.count >= 2 else { return nil }
        let enabled = tokens[0].uppercased() == "ON"
        let typeCode = tokens[1].uppercased()

        func value(after key: String) -> Double? {
            guard let i = tokens.firstIndex(where: { $0.caseInsensitiveCompare(key) == .orderedSame }),
                  i + 1 < tokens.count else { return nil }
            return Double(tokens[i + 1].replacingOccurrences(of: ",", with: "."))
        }

        guard let fc = value(after: "Fc") else { return nil }
        let gain = value(after: "Gain") ?? 0
        var q = value(after: "Q")

        let type: FilterType
        switch typeCode {
        case "PK", "PEQ", "MODAL":
            type = .peak
        case "LS", "LSC", "LSQ", "LS6", "LS12":
            type = .lowShelf
            if q == nil { q = 0.71 }
        case "HS", "HSC", "HSQ", "HS6", "HS12":
            type = .highShelf
            if q == nil { q = 0.71 }
        case "LP", "LPQ":
            type = .lowPass
            if q == nil { q = 0.71 }
        case "HP", "HPQ":
            type = .highPass
            if q == nil { q = 0.71 }
        case "NO":
            type = .notch
            if q == nil { q = 30 }
        case "BP":
            type = .bandPass
        default:
            return nil
        }

        return EQBand(type: type, frequency: fc, gain: gain, q: q ?? 1, isEnabled: enabled).clamped()
    }

    public static func export(bands: [EQBand], preampDB: Double) -> String {
        var lines = [String(format: "Preamp: %.1f dB", preampDB)]
        for (i, b) in bands.enumerated() {
            let code: String
            switch b.type {
            case .peak: code = "PK"
            case .lowShelf: code = "LSC"
            case .highShelf: code = "HSC"
            case .lowPass: code = "LPQ"
            case .highPass: code = "HPQ"
            case .notch: code = "NO"
            case .bandPass: code = "BP"
            }
            let state = b.isEnabled ? "ON" : "OFF"
            if b.type.usesGain {
                lines.append(String(format: "Filter %d: %@ %@ Fc %.0f Hz Gain %.1f dB Q %.2f", i + 1, state, code, b.frequency, b.gain, b.q))
            } else {
                lines.append(String(format: "Filter %d: %@ %@ Fc %.0f Hz Q %.2f", i + 1, state, code, b.frequency, b.q))
            }
        }
        return lines.joined(separator: "\n") + "\n"
    }
}
