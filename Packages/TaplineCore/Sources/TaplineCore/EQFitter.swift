import Foundation

/// Turns a headphone measurement and a target curve into a parametric EQ,
/// in the spirit of AutoEQ: smooth the required correction, start with two
/// shelves, then greedily add bells where the error is largest and refine all
/// filter parameters together after each addition.
public enum EQFitter {
    public struct Options: Sendable, Equatable {
        /// Total number of filters, shelves included.
        public var maxFilters: Int = 10
        /// Extra bass on top of the target (low shelf at 105 Hz).
        public var bassAdjustDB: Double = 0
        /// Extra tilt on top of the target, dB per octave around 1 kHz.
        public var tiltDBPerOctave: Double = 0
        /// Leave everything below this frequency untouched (e.g. headphones used
        /// in a different mode than they were measured in).
        public var keepBassBelow: Double?
        /// Same defaults as AutoEQ: boosts are capped harder than cuts.
        public var maxBoostDB: Double = 6
        public var maxCutDB: Double = 12
        /// Correct only below this frequency (room correction: leave the treble to the speakers).
        public var correctOnlyBelow: Double?
        /// Narrowest bell allowed. Room modes need more than headphones.
        public var maxQ: Double = 6
        /// Smoothing width in octaves below 1 kHz.
        public var lowSmoothingOctaves: Double = 1.0 / 6

        public init(maxFilters: Int = 10, bassAdjustDB: Double = 0, tiltDBPerOctave: Double = 0, keepBassBelow: Double? = nil) {
            self.maxFilters = maxFilters
            self.bassAdjustDB = bassAdjustDB
            self.tiltDBPerOctave = tiltDBPerOctave
            self.keepBassBelow = keepBassBelow
        }
    }

    public struct Result: Sendable {
        public var bands: [EQBand]
        public var frequencies: [Double]
        /// Measurement, level-matched to the target.
        public var measurement: [Double]
        /// Target including the bass / tilt preferences.
        public var target: [Double]
        /// Measurement with the fitted EQ applied.
        public var predicted: [Double]
        /// Weighted RMS deviation from the target (20 Hz - 10 kHz) before and after EQ.
        public var errorBeforeDB: Double
        public var errorAfterDB: Double
    }

    public static let grid = ResponseCurve.logFrequencies(count: 384)
    static let sampleRate = 48_000.0

    public static func fit(measurement: FrequencyResponse, target: FrequencyResponse, options: Options = Options()) -> Result {
        let f = grid
        let n = f.count
        var t = target.resampled(to: f)
        var m = measurement.resampled(to: f)

        // Preferences on top of the target.
        if options.bassAdjustDB != 0 {
            let shelf = BiquadCoefficients.design(type: .lowShelf, frequency: 105, gainDB: options.bassAdjustDB, q: 0.71, sampleRate: sampleRate)
            for i in 0..<n { t[i] += shelf.magnitudeDB(at: f[i], sampleRate: sampleRate) }
        }
        if options.tiltDBPerOctave != 0 {
            for i in 0..<n { t[i] += options.tiltDBPerOctave * log2(f[i] / 1000) }
        }

        // Level-match on the lower midrange, below the ear-gain region where targets differ most.
        let mids = (0..<n).filter { f[$0] >= 200 && f[$0] <= 1200 }
        let offset = mids.reduce(0) { $0 + t[$1] - m[$1] } / Double(max(mids.count, 1))
        for i in 0..<n { m[i] += offset }

        // Required correction, smoothed more in the treble where measurements are least reliable.
        var c = (0..<n).map { t[$0] - m[$0] }
        // Like AutoEQ: fine smoothing below 6 kHz, two-octave smoothing above 8 kHz with
        // a crossfade in between. Measurement rigs disagree wildly up there (coupler
        // resonances, fit), so only the broad treble trend is worth correcting.
        let lowWidth = options.lowSmoothingOctaves
        let fine = Smoothing.fractionalOctave(c, grid: f) { $0 < 1000 ? lowWidth : 1.0 / 5 }
        let broad = Smoothing.fractionalOctave(c, grid: f) { _ in 2.0 }
        for i in 0..<n {
            let k = (log2(f[i] / 6000) / log2(8000.0 / 6000)).clamped(to: 0...1)
            c[i] = fine[i] * (1 - k) + broad[i] * k
        }
        for i in 0..<n {
            if f[i] > 12_000 {
                c[i] *= max(0, 1 - log(f[i] / 12_000) / log(20_000.0 / 12_000))
            }
            if let top = options.correctOnlyBelow {
                if f[i] >= top * 2 { c[i] = 0 }
                else if f[i] > top {
                    let x = log2(f[i] / top)
                    c[i] *= 1 - x * x * (3 - 2 * x)
                }
            }
            if let keep = options.keepBassBelow {
                if f[i] <= keep { c[i] = 0 }
                else if f[i] < keep * 2 {
                    let x = log2(f[i] / keep)
                    c[i] *= x * x * (3 - 2 * x)
                }
            }
            c[i] = c[i].clamped(to: -options.maxCutDB...options.maxBoostDB)
        }

        let w: [Double] = f.map { freq in
            if freq < 30 { return 0.5 }
            if freq <= 8000 { return 1 }
            if freq <= 12_000 { return 0.5 }
            if freq <= 16_000 { return 0.2 }
            return 0.05
        }

        var model = Model(frequencies: f, correction: c, weights: w, options: options)
        model.fit()

        let response = model.totalResponse
        let predicted = (0..<n).map { m[$0] + response[$0] }
        let before = weightedRMS((0..<n).map { t[$0] - m[$0] }, w: w, f: f)
        let after = weightedRMS((0..<n).map { t[$0] - predicted[$0] }, w: w, f: f)

        return Result(bands: model.bands, frequencies: f, measurement: m, target: t, predicted: predicted,
                      errorBeforeDB: before, errorAfterDB: after)
    }

    static func weightedRMS(_ e: [Double], w: [Double], f: [Double]) -> Double {
        var sum = 0.0, weight = 0.0
        for i in e.indices where f[i] <= 10_000 {
            sum += w[i] * e[i] * e[i]
            weight += w[i]
        }
        return sqrt(sum / max(weight, 1e-9))
    }
}

// MARK: - Optimizer

private struct Model {
    enum Kind { case peak, lowShelf, highShelf }

    struct Filter {
        var kind: Kind
        var log2f: Double
        var gain: Double
        var logq: Double

        var frequency: Double { pow(2, log2f) }
        var q: Double { exp(logq) }
    }

    let f: [Double]
    let c: [Double]
    let w: [Double]
    let options: EQFitter.Options
    let cosW: [Double]
    let cos2W: [Double]

    var filters: [Filter] = []
    var responses: [[Double]] = []
    var total: [Double]

    init(frequencies: [Double], correction: [Double], weights: [Double], options: EQFitter.Options) {
        f = frequencies
        c = correction
        w = weights
        self.options = options
        cosW = frequencies.map { cos(2 * Double.pi * $0 / EQFitter.sampleRate) }
        cos2W = frequencies.map { cos(4 * Double.pi * $0 / EQFitter.sampleRate) }
        total = [Double](repeating: 0, count: frequencies.count)
    }

    var totalResponse: [Double] { total }

    /// Highest bell frequency: 10 kHz, or a bit above the correction limit.
    var peakCeiling: Double { min(10_000, (options.correctOnlyBelow ?? 10_000) * 1.5) }

    // Bounds per kind
    func clamp(_ x: Filter) -> Filter {
        var x = x
        switch x.kind {
        case .peak:
            x.log2f = x.log2f.clamped(to: log2(20)...log2(peakCeiling))
            x.logq = x.logq.clamped(to: log(0.25)...log(max(options.maxQ, 0.5)))
        case .lowShelf:
            x.log2f = x.log2f.clamped(to: log2(40)...log2(250))
            x.logq = x.logq.clamped(to: log(0.4)...log(1.2))
        case .highShelf:
            x.log2f = x.log2f.clamped(to: log2(4000)...log2(14_000))
            x.logq = x.logq.clamped(to: log(0.4)...log(1.2))
        }
        x.gain = x.gain.clamped(to: -options.maxCutDB...options.maxBoostDB)
        return x
    }

    func response(of x: Filter) -> [Double] {
        let type: FilterType = x.kind == .peak ? .peak : (x.kind == .lowShelf ? .lowShelf : .highShelf)
        let k = BiquadCoefficients.design(type: type, frequency: x.frequency, gainDB: x.gain, q: x.q, sampleRate: EQFitter.sampleRate)
        var out = [Double](repeating: 0, count: f.count)
        for i in f.indices {
            let num = k.b0 * k.b0 + k.b1 * k.b1 + k.b2 * k.b2 + 2 * (k.b0 * k.b1 + k.b1 * k.b2) * cosW[i] + 2 * k.b0 * k.b2 * cos2W[i]
            let den = 1 + k.a1 * k.a1 + k.a2 * k.a2 + 2 * (k.a1 + k.a1 * k.a2) * cosW[i] + 2 * k.a2 * cos2W[i]
            out[i] = 10 * log10(max(num, 1e-30) / max(den, 1e-30))
        }
        return out
    }

    func error(_ sum: [Double]) -> Double {
        var e = 0.0
        for i in f.indices {
            let d = c[i] - sum[i]
            e += w[i] * d * d
        }
        return e
    }

    mutating func add(_ x: Filter) {
        let x = clamp(x)
        let r = response(of: x)
        filters.append(x)
        responses.append(r)
        for i in f.indices { total[i] += r[i] }
    }

    func mean(of values: [Double], from lo: Double, to hi: Double) -> Double {
        let idx = f.indices.filter { f[$0] >= lo && f[$0] <= hi }
        return idx.reduce(0) { $0 + values[$1] } / Double(max(idx.count, 1))
    }

    mutating func fit() {
        let budget = max(1, options.maxFilters)

        // Shelves for broad bass / treble trends.
        let bass = mean(of: c, from: 20, to: 60)
        if abs(bass) > 0.5, filters.count < budget, options.keepBassBelow == nil {
            add(Filter(kind: .lowShelf, log2f: log2(105), gain: bass, logq: log(0.71)))
        }
        let treble = mean(of: c, from: 10_000, to: 14_000)
        if abs(treble) > 0.5, filters.count < budget, options.correctOnlyBelow == nil {
            add(Filter(kind: .highShelf, log2f: log2(10_000), gain: treble * 0.6, logq: log(0.71)))
        }
        refine()

        while filters.count < budget {
            // Largest weighted residual in the band we trust.
            var best = -1
            var bestScore = 0.0
            for i in f.indices where f[i] >= 25 && f[i] <= peakCeiling {
                let score = abs(c[i] - total[i]) * w[i]
                if score > bestScore { bestScore = score; best = i }
            }
            guard best >= 0, bestScore > 0.35 else { break }
            let peak = c[best] - total[best]

            // Width at half height -> initial Q.
            var lo = best, hi = best
            while lo > 0, abs(c[lo - 1] - total[lo - 1]) > abs(peak) / 2, (c[lo - 1] - total[lo - 1]) * peak > 0 { lo -= 1 }
            while hi < f.count - 1, abs(c[hi + 1] - total[hi + 1]) > abs(peak) / 2, (c[hi + 1] - total[hi + 1]) * peak > 0 { hi += 1 }
            let octaves = max(log2(f[hi] / f[lo]), 0.1)
            let q = sqrt(pow(2, octaves)) / (pow(2, octaves) - 1)
            add(Filter(kind: .peak, log2f: log2(f[best]), gain: peak, logq: log(q)))
            refine(maxPasses: 40)
        }
        refine(maxPasses: 400)

        // Drop filters that ended up doing nothing.
        var keep: [Int] = []
        for (i, x) in filters.enumerated() where abs(x.gain) >= 0.25 { keep.append(i) }
        if keep.count != filters.count {
            filters = keep.map { filters[$0] }
            responses = keep.map { responses[$0] }
            total = f.indices.map { i in responses.reduce(0) { $0 + $1[i] } }
        }
    }

    /// Coordinate pattern search over (log2 f, gain, ln q) of every filter.
    mutating func refine(maxPasses: Int = 400) {
        guard !filters.isEmpty else { return }
        var current = error(total)
        var scale = 1.0
        let steps = (freq: 0.08, gain: 0.4, q: 0.15)
        var passes = 0
        while scale > 0.015, passes < maxPasses {
            passes += 1
            var improved = false
            for k in filters.indices {
                for param in 0..<3 {
                    for direction in [1.0, -1.0] {
                        var x = filters[k]
                        switch param {
                        case 0: x.log2f += direction * steps.freq * scale
                        case 1: x.gain += direction * steps.gain * scale
                        default: x.logq += direction * steps.q * scale
                        }
                        x = clamp(x)
                        let r = response(of: x)
                        var candidate = total
                        for i in f.indices { candidate[i] += r[i] - responses[k][i] }
                        let e = error(candidate)
                        if e < current - 1e-9 {
                            filters[k] = x
                            responses[k] = r
                            total = candidate
                            current = e
                            improved = true
                            break
                        }
                    }
                }
            }
            if !improved { scale *= 0.5 }
        }
    }

    var bands: [EQBand] {
        let order: (Filter) -> Int = { $0.kind == .lowShelf ? 0 : ($0.kind == .peak ? 1 : 2) }
        return filters
            .sorted { order($0) != order($1) ? order($0) < order($1) : $0.frequency < $1.frequency }
            .map { x in
                let type: FilterType = x.kind == .peak ? .peak : (x.kind == .lowShelf ? .lowShelf : .highShelf)
                return EQBand(type: type,
                              frequency: (x.frequency).rounded(),
                              gain: (x.gain * 10).rounded() / 10,
                              q: (x.q * 100).rounded() / 100)
            }
    }
}
