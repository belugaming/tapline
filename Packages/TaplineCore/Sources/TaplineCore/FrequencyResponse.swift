import Foundation

/// A measured or target frequency response (frequency in Hz, level in dB).
public struct FrequencyResponse: Sendable, Equatable {
    public var frequencies: [Double]
    public var levels: [Double]

    public init(frequencies: [Double], levels: [Double]) {
        precondition(frequencies.count == levels.count)
        self.frequencies = frequencies
        self.levels = levels
    }

    /// Parses REW exports, squig.link `.txt` files and simple CSVs:
    /// any line whose first two numbers are a positive frequency and a level.
    public static func parse(_ text: String) -> FrequencyResponse? {
        var points: [(Double, Double)] = []
        for raw in text.split(whereSeparator: \.isNewline) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard let first = line.first, !"*#;/".contains(first) else { continue }
            let tokens = line.split(whereSeparator: { $0 == " " || $0 == "\t" || $0 == "," || $0 == ";" })
            guard tokens.count >= 2,
                  let f = Double(tokens[0]), let db = Double(tokens[1]),
                  f > 0, f.isFinite, db.isFinite else { continue }
            points.append((f, db))
        }
        points.sort { $0.0 < $1.0 }
        var freqs: [Double] = []
        var levels: [Double] = []
        for (f, db) in points where freqs.last.map({ f > $0 }) ?? true {
            freqs.append(f)
            levels.append(db)
        }
        guard freqs.count >= 10 else { return nil }
        return FrequencyResponse(frequencies: freqs, levels: levels)
    }

    /// Level at `f`, linearly interpolated on a log-frequency axis and held flat beyond the ends.
    public func level(at f: Double) -> Double {
        guard let first = frequencies.first, let last = frequencies.last else { return 0 }
        if f <= first { return levels[0] }
        if f >= last { return levels[levels.count - 1] }
        var lo = 0, hi = frequencies.count - 1
        while hi - lo > 1 {
            let mid = (lo + hi) / 2
            if frequencies[mid] <= f { lo = mid } else { hi = mid }
        }
        let t = log(f / frequencies[lo]) / log(frequencies[hi] / frequencies[lo])
        return levels[lo] + (levels[hi] - levels[lo]) * t
    }

    public func resampled(to grid: [Double]) -> [Double] {
        grid.map(level(at:))
    }

    /// Averages several responses (e.g. left and right channel) in dB on a grid.
    public static func average(_ responses: [FrequencyResponse], grid: [Double]) -> FrequencyResponse? {
        guard !responses.isEmpty else { return nil }
        let sampled = responses.map { $0.resampled(to: grid) }
        let mean = grid.indices.map { i in sampled.reduce(0) { $0 + $1[i] } / Double(sampled.count) }
        return FrequencyResponse(frequencies: grid, levels: mean)
    }
}

enum Smoothing {
    /// Moving average whose width (in octaves) depends on frequency.
    /// `grid` must be log-spaced.
    static func fractionalOctave(_ values: [Double], grid: [Double], width: (Double) -> Double) -> [Double] {
        guard grid.count > 2 else { return values }
        let pointsPerOctave = Double(grid.count - 1) / log2(grid[grid.count - 1] / grid[0])
        var out = values
        for i in values.indices {
            let half = max(0, Int((width(grid[i]) * pointsPerOctave / 2).rounded()))
            let lo = max(0, i - half), hi = min(values.count - 1, i + half)
            var sum = 0.0
            for j in lo...hi { sum += values[j] }
            out[i] = sum / Double(hi - lo + 1)
        }
        return out
    }
}
