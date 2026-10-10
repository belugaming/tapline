import Foundation

/// Something a room measurement suggests fixing at the speakers or the seat,
/// before (or instead of) EQ.
public struct RoomAdvice: Sendable, Equatable, Identifiable {
    public enum Severity: Int, Sendable, Comparable {
        case info, suggestion, warning
        public static func < (a: Self, b: Self) -> Bool { a.rawValue < b.rawValue }
    }

    public enum Kind: Sendable, Equatable {
        /// One side is louder through the midrange.
        case balance(louder: EQChannels, dB: Double)
        /// One speaker is farther from the microphone.
        case distance(farther: EQChannels, centimeters: Double)
        /// The two speakers push in opposite directions (a wiring or polarity-switch problem).
        case polarity
        /// Left and right differ a lot in the bass; separate corrections help.
        case channelMismatch(dB: Double, frequency: Double)
        /// Sub-bass (30-90 Hz) above (+) or below (-) the target, relative to the midrange.
        case subwoofer(dB: Double)
        /// Broad excess from 100 to 400 Hz, typical of speakers near walls.
        /// `switchDB` is the nearest bass-tilt setting (-2, -4 or -6 dB).
        case bassTilt(dB: Double, switchDB: Double)
        /// A bump around 160 Hz from the desk or console under the speakers.
        case desktop(frequency: Double, dB: Double)
        /// A resonance between parallel surfaces. The EQ takes care of it.
        case roomMode(frequency: Double, dB: Double, dimensionMeters: Double)
        /// A narrow, deep dip from sound cancelling itself. EQ cannot fill it.
        case cancellation(frequency: Double, depthDB: Double, quarterWaveCentimeters: Double, nearCrossover: Bool)
        /// Treble trend versus the target. `switchDB` is the nearest treble-tilt setting.
        case treble(dB: Double, switchDB: Double, calibrated: Bool)
        case fewPositions(count: Int)
        case noisy(snrDB: Double)
    }

    public var kind: Kind
    public var severity: Severity

    public var id: String {
        switch kind {
        case .balance: "balance"
        case .distance: "distance"
        case .polarity: "polarity"
        case .channelMismatch: "mismatch"
        case .subwoofer: "subwoofer"
        case .bassTilt: "bassTilt"
        case .desktop: "desktop"
        case .roomMode(let f, _, _): "mode-\(Int(f))"
        case .cancellation(let f, _, _, _): "dip-\(Int(f))"
        case .treble: "treble"
        case .fewPositions: "positions"
        case .noisy: "noisy"
        }
    }
}

public enum RoomAdvisor {
    public struct Input: Sendable {
        /// Average of every measurement.
        public var measurement: FrequencyResponse
        /// Target including the bass / tilt preferences.
        public var target: FrequencyResponse
        public var left: FrequencyResponse?
        public var right: FrequencyResponse?
        /// Right arrival minus left arrival, seconds.
        public var arrivalDifference: Double?
        public var oppositePolarity: Bool?
        public var positions: Int
        public var worstSNRDB: Double
        public var calibrated: Bool

        public init(measurement: FrequencyResponse, target: FrequencyResponse, left: FrequencyResponse? = nil, right: FrequencyResponse? = nil,
                    arrivalDifference: Double? = nil, oppositePolarity: Bool? = nil, positions: Int, worstSNRDB: Double, calibrated: Bool) {
            self.measurement = measurement
            self.target = target
            self.left = left
            self.right = right
            self.arrivalDifference = arrivalDifference
            self.oppositePolarity = oppositePolarity
            self.positions = positions
            self.worstSNRDB = worstSNRDB
            self.calibrated = calibrated
        }
    }

    static let speedOfSound = 343.0

    public static func advise(_ input: Input) -> [RoomAdvice] {
        let g = EQFitter.grid
        var out: [RoomAdvice] = []

        // Deviation from the target, level-matched on the midrange.
        let m = input.measurement.resampled(to: g)
        let t = input.target.resampled(to: g)
        var d = g.indices.map { m[$0] - t[$0] }
        let mid = mean(d, g, 500, 2000)
        d = d.map { $0 - mid }
        let third = Smoothing.fractionalOctave(d, grid: g) { _ in 1.0 / 3 }
        let fine = Smoothing.fractionalOctave(d, grid: g) { _ in 1.0 / 12 }
        let octave = Smoothing.fractionalOctave(d, grid: g) { _ in 1.0 }
        // Height of narrow features above their surroundings: the median over an
        // octave either side ignores the feature itself.
        let local = g.indices.map { i in fine[i] - median(fine, g, g[i] / 2, g[i] * 2) }

        // Problems first: these make the rest unreliable.
        if input.oppositePolarity == true {
            out.append(RoomAdvice(kind: .polarity, severity: .warning))
        }
        if input.worstSNRDB < 25 {
            out.append(RoomAdvice(kind: .noisy(snrDB: input.worstSNRDB), severity: .warning))
        }

        // Left / right.
        if let l = input.left, let r = input.right {
            let ll = l.resampled(to: g), rr = r.resampled(to: g)
            let diff = mean(rr, g, 400, 4000) - mean(ll, g, 400, 4000)
            if abs(diff) >= 0.5 {
                out.append(RoomAdvice(kind: .balance(louder: diff > 0 ? .right : .left, dB: abs(diff)),
                                      severity: abs(diff) >= 1 ? .suggestion : .info))
            }
            let delta = Smoothing.fractionalOctave(g.indices.map { (ll[$0] - rr[$0]) + diff }, grid: g) { _ in 1.0 / 6 }
            if let worst = g.indices.filter({ g[$0] >= 30 && g[$0] <= 300 }).max(by: { abs(delta[$0]) < abs(delta[$1]) }),
               abs(delta[worst]) >= 4 {
                out.append(RoomAdvice(kind: .channelMismatch(dB: abs(delta[worst]), frequency: g[worst]), severity: .info))
            }
        }
        if let dt = input.arrivalDifference {
            let cm = dt * speedOfSound * 100
            if abs(cm) >= 2 {
                out.append(RoomAdvice(kind: .distance(farther: cm > 0 ? .right : .left, centimeters: abs(cm)),
                                      severity: abs(cm) >= 5 ? .suggestion : .info))
            }
        }

        // Level of the bass regions.
        let sub = median(third, g, 30, 90)
        if abs(sub) >= 3 {
            out.append(RoomAdvice(kind: .subwoofer(dB: sub), severity: .suggestion))
        }
        let lowMid = median(third, g, 100, 400)
        if lowMid >= 1.5 {
            let step = (lowMid / 2).rounded().clamped(to: 1...3) * 2
            out.append(RoomAdvice(kind: .bassTilt(dB: lowMid, switchDB: -step), severity: .suggestion))
        }
        // A desk bounce is broad (about an octave wide): look at the octave-smoothed
        // curve so a narrow room mode in the same place does not count.
        if let peak = g.indices.filter({ g[$0] >= 120 && g[$0] <= 220 }).max(by: { octave[$0] < octave[$1] }) {
            let f = g[peak]
            // Baseline above the subwoofer's range, whose level is judged separately.
            let bump = octave[peak] - median(fine, g, max(90, f / 3), f * 3)
            if bump >= 2.5 {
                out.append(RoomAdvice(kind: .desktop(frequency: f, dB: bump), severity: .suggestion))
            }
        }

        // Narrow features in the bass.
        for (i, depth) in extremes(local, g, 30, 300, minimum: true).filter({ $0.1 <= -8 }).prefix(2) {
            let f = g[i]
            out.append(RoomAdvice(kind: .cancellation(frequency: f, depthDB: -depth, quarterWaveCentimeters: speedOfSound / (4 * f) * 100,
                                                      nearCrossover: f >= 50 && f <= 130),
                                  severity: .suggestion))
        }
        for (i, height) in extremes(local, g, 25, 300, minimum: false).filter({ $0.1 >= 4 }).prefix(3) {
            let f = g[i]
            out.append(RoomAdvice(kind: .roomMode(frequency: f, dB: height, dimensionMeters: speedOfSound / (2 * f)), severity: .info))
        }

        // Treble: depends on the microphone, so only a hint without calibration.
        let treble = median(third, g, 4000, 12_000)
        if abs(treble) >= 2 {
            let step: Double = treble > 0 ? (treble >= 3 ? -4 : -2) : 2
            out.append(RoomAdvice(kind: .treble(dB: treble, switchDB: step, calibrated: input.calibrated),
                                  severity: input.calibrated ? .suggestion : .info))
        }

        if input.positions < 3 {
            out.append(RoomAdvice(kind: .fewPositions(count: input.positions), severity: .info))
        }
        return out.enumerated().sorted { a, b in
            a.element.severity != b.element.severity ? a.element.severity > b.element.severity : a.offset < b.offset
        }.map(\.element)
    }

    // MARK: - Helpers

    static func indices(_ g: [Double], _ lo: Double, _ hi: Double) -> [Int] {
        g.indices.filter { g[$0] >= lo && g[$0] <= hi }
    }

    static func mean(_ x: [Double], _ g: [Double], _ lo: Double, _ hi: Double) -> Double {
        let idx = indices(g, lo, hi)
        return idx.reduce(0) { $0 + x[$1] } / Double(max(idx.count, 1))
    }

    static func median(_ x: [Double], _ g: [Double], _ lo: Double, _ hi: Double) -> Double {
        let v = indices(g, lo, hi).map { x[$0] }.sorted()
        guard !v.isEmpty else { return 0 }
        return v.count % 2 == 1 ? v[v.count / 2] : (v[v.count / 2 - 1] + v[v.count / 2]) / 2
    }

    static func level(_ x: [Double], _ g: [Double], _ f: Double) -> Double {
        FrequencyResponse(frequencies: g, levels: x).level(at: f)
    }

    /// Local maxima (or minima) between `lo` and `hi`, strongest first, at least
    /// a sixth of an octave apart.
    static func extremes(_ x: [Double], _ g: [Double], _ lo: Double, _ hi: Double, minimum: Bool) -> [(Int, Double)] {
        let sign = minimum ? -1.0 : 1.0
        var found: [(Int, Double)] = []
        for i in indices(g, lo, hi) where i > 0 && i < x.count - 1 {
            if sign * x[i] >= sign * x[i - 1] && sign * x[i] > sign * x[i + 1] { found.append((i, x[i])) }
        }
        found.sort { sign * $0.1 > sign * $1.1 }
        var kept: [(Int, Double)] = []
        for e in found where !kept.contains(where: { abs(log2(g[$0.0] / g[e.0])) < 1.0 / 6 }) {
            kept.append(e)
        }
        return kept
    }
}
