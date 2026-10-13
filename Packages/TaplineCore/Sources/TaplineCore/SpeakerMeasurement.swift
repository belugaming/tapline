import Foundation

/// A multichannel measurement in one recording: a sweep on each speaker in turn, then short
/// timing chirps: the reference speaker (front left), every speaker, the reference again.
/// As in `StereoSweepPlan`, the two reference chirps bracket the others, so the clock drift
/// between two audio interfaces (one playing, one recording) cancels. LFE channels get a
/// low chirp (20-200 Hz) instead, which a subwoofer can play.
public struct SpeakerSweepPlan: Sendable {
    public struct Speaker: Sendable, Equatable {
        /// The output channel it plays on (0-based).
        public var channel: Int
        public var isLFE: Bool

        public init(channel: Int, isLFE: Bool = false) {
            self.channel = channel
            self.isLFE = isLFE
        }
    }

    public let sampleRate: Double
    /// In playing order.
    public let speakers: [Speaker]
    /// Index into `speakers` of the timing reference.
    public let reference: Int
    public let sweep: SineSweep
    public let chirp: SineSweep
    public let lowChirp: SineSweep

    public static let preRoll = 0.6
    /// Between sweeps: longer than the analysis window, so one speaker's room decay stays out of the next.
    public static let gap = 1.2
    public static let chirpSeconds = 0.25
    public static let chirpSpacing = 0.5
    public static let lowChirpSeconds = 0.5
    public static let lowChirpSpacing = 1.0
    public static let tail = 0.8
    /// Shorter than the stereo sweep: a 7.1.4 layout plays twelve of them per position.
    public static let defaultSweepSeconds = 6.0

    public init(sampleRate: Double, speakers: [Speaker], reference: Int = 0, sweepSeconds: Double = defaultSweepSeconds, endFrequency: Double = 20_000) {
        self.sampleRate = sampleRate
        self.speakers = speakers
        self.reference = speakers.indices.contains(reference) ? reference : 0
        sweep = SineSweep(sampleRate: sampleRate, duration: sweepSeconds, endFrequency: endFrequency)
        chirp = SineSweep(sampleRate: sampleRate, duration: Self.chirpSeconds, startFrequency: 500, endFrequency: min(16_000, endFrequency))
        lowChirp = SineSweep(sampleRate: sampleRate, duration: Self.lowChirpSeconds, startFrequency: 20, endFrequency: 200)
    }

    /// When speaker `i`'s sweep starts, seconds.
    public func sweepStart(_ i: Int) -> Double {
        Self.preRoll + Double(i) * (sweep.duration + Self.gap)
    }

    /// The timing chirps in order: which speaker (index into `speakers`) and when, seconds.
    /// The reference speaker comes first and last.
    public var chirpSlots: [(speaker: Int, start: Double)] {
        var t = sweepStart(speakers.count)
        var slots: [(speaker: Int, start: Double)] = []
        for i in [reference] + Array(speakers.indices) + [reference] {
            slots.append((i, t))
            t += speakers[i].isLFE ? Self.lowChirpSpacing : Self.chirpSpacing
        }
        return slots
    }

    public var duration: Double {
        guard let last = chirpSlots.last else { return Self.preRoll + Self.tail }
        return last.start + (speakers[last.speaker].isLFE ? Self.lowChirpSeconds : Self.chirpSeconds) + Self.tail
    }

    /// What happens at `seconds` into the plan: which speaker plays (index into `speakers`),
    /// and whether it is a timing chirp. Nil before the first sweep and in the tail.
    public func playing(at seconds: Double) -> (speaker: Int, chirps: Bool)? {
        if seconds < Self.preRoll { return nil }
        let chirps = sweepStart(speakers.count)
        if seconds < chirps - Self.gap / 2 {
            let i = Int((seconds - Self.preRoll + Self.gap / 2) / (sweep.duration + Self.gap))
            return (min(max(i, 0), speakers.count - 1), false)
        }
        guard let slot = chirpSlots.last(where: { $0.start <= seconds + 0.05 }) else { return nil }
        return seconds < duration - Self.tail ? (slot.speaker, true) : nil
    }

    /// One signal per speaker, in the order of `speakers`, all of the same length.
    public func render() -> [[Float]] {
        let count = Int((duration * sampleRate).rounded())
        var out = [[Float]](repeating: [Float](repeating: 0, count: count), count: speakers.count)
        func place(_ signal: [Float], at seconds: Double, into i: Int) {
            let start = Int((seconds * sampleRate).rounded())
            for (j, v) in signal.enumerated() where start + j < count { out[i][start + j] = v }
        }
        for i in speakers.indices { place(sweep.signal, at: sweepStart(i), into: i) }
        for slot in chirpSlots {
            place(speakers[slot.speaker].isLFE ? lowChirp.signal : chirp.signal, at: slot.start, into: slot.speaker)
        }
        return out
    }

    public struct SpeakerResult: Sendable {
        public var channel: Int
        public var response: FrequencyResponse
        /// Arrival after the reference speaker's, seconds: positive when the speaker is farther
        /// away (for a subwoofer this includes its low-pass filter's delay). Nil when unclear.
        public var arrival: Double?
        /// Whether it pushes when the reference speaker pulls. Nil when unclear, and always
        /// for LFE channels, whose low-passed chirp shows no clear polarity.
        public var oppositePolarity: Bool?
        public var snrDB: Double
    }

    public struct Analysis: Sendable {
        public var speakers: [SpeakerResult]
        public var peakDB: Double
    }

    /// Analyses a mono microphone recording at `inputRate` of `render()` played at
    /// `outputRate` (nil: the same rate). The recording must start before playback.
    public static func analyze(recording: [Float], inputRate: Double, outputRate: Double? = nil, speakers: [Speaker], reference: Int = 0,
                               sweepSeconds: Double = defaultSweepSeconds, windowSeconds: Double = 0.5) -> Analysis? {
        guard !speakers.isEmpty else { return nil }
        let end = SineSweep.endFrequency(playbackRate: outputRate ?? inputRate, recordingRate: inputRate)
        let plan = SpeakerSweepPlan(sampleRate: inputRate, speakers: speakers, reference: reference, sweepSeconds: sweepSeconds, endFrequency: end)
        let fs = inputRate
        let n = plan.sweep.signal.count
        let slots = plan.chirpSlots
        guard recording.count > Int(slots[0].start * fs) else { return nil }

        func slice(_ from: Int, _ to: Int) -> [Float] {
            let a = max(0, min(from, recording.count)), b = max(a, min(to, recording.count))
            return Array(recording[a..<b])
        }

        // Sweeps, each deconvolved on its own. The first one finds the latency (playback,
        // recording, distance); the others are looked for where it puts them.
        var latency = 0
        var responses: [FrequencyResponse?] = []
        for i in speakers.indices {
            let nominal = Int(plan.sweepStart(i) * fs)
            let first = i == 0
            let begin = max(0, nominal + latency - Int((first ? 0.05 : 0.1) * fs))
            let stop = nominal + latency + n + Int(((first ? 1.0 : 0.1) + windowSeconds + 0.1) * fs)
            let ir = plan.sweep.impulseResponse(from: slice(begin, stop))
            let expected = nominal + latency - begin + n - 1
            let window = first ? (expected - Int(0.05 * fs))..<(expected + Int(1.0 * fs))
                               : (expected - Int(0.05 * fs))..<(expected + Int(0.05 * fs))
            guard let peak = StereoSweepPlan.peakIndex(ir, in: window) else {
                if first { return nil }
                responses.append(nil)
                continue
            }
            if first { latency = peak - expected }
            let lo = max(0, peak - Int(0.05 * fs)), hi = min(ir.count, peak + Int((windowSeconds + 0.05) * fs))
            responses.append(hi > lo ? RoomResponse.frequencyResponse(impulse: Array(ir[lo..<hi]), sampleRate: fs, windowSeconds: windowSeconds) : nil)
        }

        // Chirps: arrival times relative to the reference speaker, with sub-sample precision.
        var arrivals = [Double?](repeating: nil, count: speakers.count)
        var polarities = [Bool?](repeating: nil, count: speakers.count)
        let begin = max(0, Int(slots[0].start * fs) + latency - Int(0.1 * fs))
        let segment = slice(begin, Int(plan.duration * fs) + latency + Int(0.3 * fs))
        let cir = plan.chirp.impulseResponse(from: segment)
        let lowCIR = speakers.contains(where: \.isLFE) ? plan.lowChirp.impulseResponse(from: segment) : []
        // Where each chirp starts in the recording as heard, in samples, and the sign of its peak.
        func locate(_ slot: Int, around offset: Double, reach: Double) -> (position: Double, sign: Float?)? {
            let s = speakers[slots[slot].speaker]
            let length = (s.isLFE ? plan.lowChirp : plan.chirp).signal.count
            let expected = Int(slots[slot].start * fs) + latency + Int(offset.rounded()) - begin + length - 1
            let window = (expected - Int(reach * fs))..<(expected + Int(reach * fs))
            if s.isLFE {
                guard let p = envelopePeak(lowCIR, in: window, halfWidth: Int(0.0025 * fs)) else { return nil }
                return (Double(begin) + p - Double(length - 1), nil)
            }
            guard let p = StereoSweepPlan.peakIndex(cir, in: window) else { return nil }
            return (Double(begin) + StereoSweepPlan.interpolatedPeak(cir, at: p) - Double(length - 1),
                    StereoSweepPlan.clearSign(cir, at: p, halfWidth: Int(0.0005 * fs)))
        }
        let lastSlot = slots.count - 1
        if let first = locate(0, around: 0, reach: 0.05) {
            let firstOffset = first.position - (slots[0].start * fs + Double(latency))
            if let last = locate(lastSlot, around: firstOffset, reach: 0.02) {
                let lastOffset = last.position - (slots[lastSlot].start * fs + Double(latency))
                // The reference's offset at any time between its two chirps (linear clock drift).
                func offset(at seconds: Double) -> Double {
                    let t0 = slots[0].start, t1 = slots[lastSlot].start
                    return firstOffset + (lastOffset - firstOffset) * (seconds - t0) / max(t1 - t0, 1e-9)
                }
                let referenceSign: Float? = first.sign != nil && first.sign == last.sign ? first.sign : nil
                for slot in 1..<lastSlot {
                    let i = slots[slot].speaker
                    let expected = offset(at: slots[slot].start)
                    // Up to about 10 m nearer or farther than the reference; subwoofers lag more.
                    guard let hit = locate(slot, around: expected, reach: speakers[i].isLFE ? 0.06 : 0.03) else { continue }
                    arrivals[i] = (hit.position - (slots[slot].start * fs + Double(latency)) - expected) / fs
                    if !speakers[i].isLFE, let a = referenceSign, let b = hit.sign {
                        polarities[i] = a != b
                    }
                }
            }
        }

        // Signal to noise: each sweep against the room before the first one.
        func rms(_ lo: Double, _ hi: Double) -> Double {
            let a = max(0, Int(lo * fs) + latency), b = min(recording.count, Int(hi * fs) + latency)
            guard b > a else { return 1e-9 }
            var sum = 0.0
            for i in a..<b { sum += Double(recording[i]) * Double(recording[i]) }
            return sqrt(sum / Double(b - a))
        }
        let noise = rms(0, preRoll * 0.8)
        var results: [SpeakerResult] = []
        for (i, s) in speakers.enumerated() {
            guard let response = responses[i] else { continue }
            let start = plan.sweepStart(i)
            let snr = 20 * log10(max(rms(start, start + sweepSeconds), 1e-9) / max(noise, 1e-9))
            results.append(SpeakerResult(channel: s.channel, response: response, arrival: arrivals[i],
                                         oppositePolarity: polarities[i], snrDB: snr))
        }
        guard !results.isEmpty else { return nil }
        let peak = Double(recording.map(abs).max() ?? 0)
        return Analysis(speakers: results, peakDB: 20 * log10(max(peak, 1e-9)))
    }

    /// Peak of the RMS envelope (over ±`halfWidth` samples) inside `range`, interpolated: the
    /// arrival of a band-limited pulse whose largest swing could be either of two half-waves.
    static func envelopePeak(_ x: [Float], in range: Range<Int>, halfWidth: Int) -> Double? {
        let lo = max(0, range.lowerBound), hi = min(x.count, range.upperBound)
        guard hi > lo, halfWidth > 0 else { return nil }
        // Running sum of squares.
        var prefix = [Double](repeating: 0, count: x.count + 1)
        for i in x.indices { prefix[i + 1] = prefix[i] + Double(x[i]) * Double(x[i]) }
        func energy(_ i: Int) -> Double {
            let a = max(0, i - halfWidth), b = min(x.count, i + halfWidth + 1)
            return (prefix[b] - prefix[a]) / Double(max(b - a, 1))
        }
        var best = lo
        var bestValue = energy(lo)
        for i in lo..<hi {
            let e = energy(i)
            if e > bestValue { best = i; bestValue = e }
        }
        guard bestValue > 0 else { return nil }
        guard best > 0, best < x.count - 1 else { return Double(best) }
        let a = energy(best - 1), b = bestValue, c = energy(best + 1)
        let d = a - 2 * b + c
        return abs(d) > 1e-18 ? Double(best) + 0.5 * (a - c) / d : Double(best)
    }
}

/// Level trims and delays that make every speaker of a multichannel layout equally loud and
/// arrive at the same moment at the listening position.
public enum SpeakerAlignment {
    public struct Speaker: Sendable, Equatable {
        public var position: SpeakerPosition
        /// `level(_:position:)` of its measured response, dB.
        public var levelDB: Double
        /// Arrival after the reference speaker's, seconds (nil: not measured).
        public var arrival: Double?

        public init(position: SpeakerPosition, levelDB: Double, arrival: Double?) {
            self.position = position
            self.levelDB = levelDB
            self.arrival = arrival
        }
    }

    public struct Result: Sendable, Equatable {
        /// Never a boost: louder speakers are turned down to the quietest.
        public var trimDB: Double
        public var delayMS: Double
    }

    /// The kernel's and Equalizer APO's limit is far longer; anything beyond this is a
    /// measurement gone wrong rather than a room.
    public static let maxDelayMS = 50.0
    public static let maxTrimDB = 12.0
    public static let speedOfSound = 343.0

    /// A speaker's level in its own band, dB: the third-octave smoothed mean from 500 Hz to
    /// 2 kHz, or from 30 to 80 Hz for an LFE channel.
    public static func level(_ response: FrequencyResponse, position: SpeakerPosition) -> Double {
        let g = EQFitter.grid
        let smooth = Smoothing.fractionalOctave(response.resampled(to: g), grid: g) { _ in 1.0 / 3 }
        let (lo, hi) = position.isLFE ? (30.0, 80.0) : (500.0, 2000.0)
        let band = g.indices.filter { g[$0] >= lo && g[$0] <= hi }
        return band.reduce(0) { $0 + smooth[$1] } / Double(max(band.count, 1))
    }

    /// Trims that turn every speaker down to the quietest one, and delays that hold back the
    /// nearer speakers until the farthest one arrives. LFE channels are delayed with the rest
    /// but keep their level: how loud the subwoofer plays is a matter of taste and of the
    /// receiver's +10 dB LFE gain, set at the subwoofer or in the receiver.
    public static func align(_ speakers: [Speaker]) -> [Result] {
        let mains = speakers.filter { !$0.position.isLFE && $0.levelDB.isFinite }
        let quietest = mains.map(\.levelDB).min()
        let arrivals = speakers.compactMap(\.arrival).filter(\.isFinite)
        let latest = arrivals.max()
        return speakers.map { s in
            var trim = 0.0
            if !s.position.isLFE, let quietest, s.levelDB.isFinite {
                trim = (quietest - s.levelDB).clamped(to: -maxTrimDB...0)
            }
            var delay = 0.0
            if let latest, let arrival = s.arrival, arrival.isFinite {
                delay = ((latest - arrival) * 1000).clamped(to: 0...maxDelayMS)
            }
            return Result(trimDB: trim, delayMS: delay)
        }
    }
}

/// What a multichannel measurement suggests checking, speaker by speaker.
public struct SpeakerAdvice: Sendable, Equatable, Identifiable {
    public enum Kind: Sendable, Equatable {
        /// Pushes when the reference speaker pulls: a wiring or polarity-switch problem.
        case polarity(channel: Int)
        /// No timing chirp found: the speaker may not play, or plays from another channel.
        case silent(channel: Int)
        /// Much louder or quieter than the others (dB from their median): check its level control.
        case level(channel: Int, dB: Double)
        /// Much nearer or farther than the others (metres from their median): worth checking the distance.
        case distance(channel: Int, meters: Double)
        /// The LFE channel's level in its band relative to the other speakers in theirs.
        case subwoofer(channel: Int, dB: Double)
        case noisy(snrDB: Double)
        case fewPositions(count: Int)
    }

    public enum Severity: Int, Sendable, Comparable {
        case info, suggestion, warning
        public static func < (a: Self, b: Self) -> Bool { a.rawValue < b.rawValue }
    }

    public var kind: Kind
    public var severity: Severity

    public var id: String {
        switch kind {
        case .polarity(let c): "polarity-\(c)"
        case .silent(let c): "silent-\(c)"
        case .level(let c, _): "level-\(c)"
        case .distance(let c, _): "distance-\(c)"
        case .subwoofer(let c, _): "sub-\(c)"
        case .noisy: "noisy"
        case .fewPositions: "positions"
        }
    }

    public struct Speaker: Sendable, Equatable {
        public var channel: Int
        public var position: SpeakerPosition
        public var levelDB: Double
        public var arrival: Double?
        public var oppositePolarity: Bool?

        public init(channel: Int, position: SpeakerPosition, levelDB: Double, arrival: Double?, oppositePolarity: Bool?) {
            self.channel = channel
            self.position = position
            self.levelDB = levelDB
            self.arrival = arrival
            self.oppositePolarity = oppositePolarity
        }
    }

    public static func advise(_ speakers: [Speaker], positions: Int, worstSNRDB: Double) -> [SpeakerAdvice] {
        var out: [SpeakerAdvice] = []
        for s in speakers where s.oppositePolarity == true {
            out.append(SpeakerAdvice(kind: .polarity(channel: s.channel), severity: .warning))
        }
        for s in speakers where s.arrival == nil {
            out.append(SpeakerAdvice(kind: .silent(channel: s.channel), severity: .warning))
        }
        if worstSNRDB < 25 {
            out.append(SpeakerAdvice(kind: .noisy(snrDB: worstSNRDB), severity: .warning))
        }
        let mains = speakers.filter { !$0.position.isLFE }
        func median(_ v: [Double]) -> Double? {
            let s = v.sorted()
            guard !s.isEmpty else { return nil }
            return s.count % 2 == 1 ? s[s.count / 2] : (s[s.count / 2 - 1] + s[s.count / 2]) / 2
        }
        if let typical = median(mains.map(\.levelDB)) {
            for s in mains where abs(s.levelDB - typical) >= 6 {
                out.append(SpeakerAdvice(kind: .level(channel: s.channel, dB: s.levelDB - typical), severity: .suggestion))
            }
            for s in speakers where s.position.isLFE {
                out.append(SpeakerAdvice(kind: .subwoofer(channel: s.channel, dB: s.levelDB - typical), severity: .info))
            }
        }
        if let typical = median(mains.compactMap(\.arrival)) {
            for s in mains {
                guard let a = s.arrival else { continue }
                let meters = (a - typical) * SpeakerAlignment.speedOfSound
                if abs(meters) >= 1.5 {
                    out.append(SpeakerAdvice(kind: .distance(channel: s.channel, meters: meters), severity: .info))
                }
            }
        }
        if positions < 3 {
            out.append(SpeakerAdvice(kind: .fewPositions(count: positions), severity: .info))
        }
        return out.enumerated().sorted { a, b in
            a.element.severity != b.element.severity ? a.element.severity > b.element.severity : a.offset < b.offset
        }.map(\.element)
    }
}
