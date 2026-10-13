import AVFoundation
import CoreAudio
import Foundation
import Observation
import TaplineCore

/// Measures speakers in the room: plays a sine sweep (or pink noise for a level
/// check) on the output device while recording a microphone, then turns the
/// recording into a frequency response.
///
/// The sweep is played by Tapline's own process, which its taps exclude, so the
/// measurement sees the speakers and the room without Tapline's EQ.
@MainActor
@Observable
final class RoomMeasurementService {
    enum Speakers: String, CaseIterable, Identifiable {
        /// Each speaker on its own in one run, plus timing chirps.
        case stereo
        case both, left, right
        var id: String { rawValue }

        var title: LocalizedStringResource {
            switch self {
            case .stereo: "Left & right"
            case .both: "Together"
            case .left: "Left"
            case .right: "Right"
            }
        }
    }

    enum Phase: Equatable {
        case idle
        case checkingLevel
        /// Pink noise on one output channel, for the wiring check.
        case identifying(Int)
        case measuring(Double)
        case analyzing
    }

    struct Measurement: Identifiable {
        let id = UUID()
        var date = Date()
        let speakers: Speakers
        /// Both speakers (for a stereo run: the power average of the two).
        let response: FrequencyResponse
        var left: FrequencyResponse?
        var right: FrequencyResponse?
        /// Right arrival minus left arrival, seconds (stereo runs).
        var arrivalDifference: Double?
        var oppositePolarity: Bool?
        let peakDB: Double
        let snrDB: Double
        /// The suggested microphone position it was taken at (0 ..< `positionCount`),
        /// nil once all of them were measured.
        var position: Int?

        var leftResponse: FrequencyResponse? { left ?? (speakers == .left ? response : nil) }
        var rightResponse: FrequencyResponse? { right ?? (speakers == .right ? response : nil) }

        enum Quality { case good, fair, noisy, clipped }

        /// Clipped and noisy measurements are worth repeating.
        var quality: Quality {
            if peakDB > -1 { return .clipped }
            if snrDB < 20 { return .noisy }
            return snrDB < 30 ? .fair : .good
        }
    }

    private(set) var phase: Phase = .idle
    private(set) var measurements: [Measurement] = []
    /// Seconds into the running level check or measurement, and its length.
    private(set) var elapsedSeconds: Double = 0
    private(set) var runSeconds: Double = 0
    private var measuringSpeakers: Speakers = .stereo
    /// Live microphone level during a level check or measurement, dBFS.
    private(set) var inputPeakDB: Double = -120
    private(set) var inputRMSDB: Double = -120
    private(set) var calibration: FrequencyResponse?
    private(set) var calibrationName: String?
    var lastMessage: String?

    nonisolated static let sweepSeconds = 8.0
    nonisolated private static let preRoll = 0.6
    nonisolated private static let tail = 1.5

    var isBusy: Bool { phase != .idle }

    var isMeasuring: Bool {
        if case .measuring = phase { return true }
        return false
    }

    var secondsLeft: Int { Int(max(0, runSeconds - elapsedSeconds).rounded(.up)) }

    /// What the measurement in progress plays right now.
    var nowPlaying: LocalizedStringResource? {
        guard isMeasuring else { return nil }
        switch measuringSpeakers {
        case .left: return "Left speaker"
        case .right: return "Right speaker"
        case .both: return "Both speakers"
        case .stereo:
            // StereoSweepPlan's timeline: left sweep, gap, right sweep, gap, timing clicks.
            let gap = StereoSweepPlan.gap
            let rightStart = StereoSweepPlan.preRoll + Self.sweepSeconds + gap
            let clicksStart = rightStart + Self.sweepSeconds + gap
            if elapsedSeconds < rightStart - gap / 2 { return "Left speaker" }
            if elapsedSeconds < clicksStart - gap / 2 { return "Right speaker" }
            return "Timing clicks"
        }
    }

    // MARK: - Positions

    /// Suggested microphone positions: the listening spot, then about 20 cm to the
    /// left, to the right, in front and behind.
    nonisolated static let positionCount = 5

    var measuredPositions: Set<Int> { Set(measurements.compactMap(\.position)) }

    /// The first suggested position not measured yet.
    var nextPosition: Int? {
        let taken = measuredPositions
        return (0..<Self.positionCount).first { !taken.contains($0) }
    }

    // MARK: - Microphone

    func microphoneAccess() async -> Bool {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: return true
        case .notDetermined: return await AVCaptureDevice.requestAccess(for: .audio)
        default: return false
        }
    }

    func loadCalibration(from url: URL) -> Bool {
        guard let text = (try? String(contentsOf: url, encoding: .utf8)) ?? (try? String(contentsOf: url, encoding: .isoLatin1)),
              let fr = FrequencyResponse.parse(text) else { return false }
        calibration = fr
        calibrationName = url.lastPathComponent
        return true
    }

    func clearCalibration() {
        calibration = nil
        calibrationName = nil
    }

    func remove(_ id: UUID) { measurements.removeAll { $0.id == id } }
    func clearMeasurements() { measurements = [] }

    var averagedResponse: FrequencyResponse? { RoomResponse.average(measurements.map(\.response)) }
    var averagedLeft: FrequencyResponse? { RoomResponse.average(measurements.compactMap(\.leftResponse)) }
    var averagedRight: FrequencyResponse? { RoomResponse.average(measurements.compactMap(\.rightResponse)) }

    /// Mean over the stereo runs (the positions surround the listening spot).
    var arrivalDifference: Double? {
        let values = measurements.compactMap(\.arrivalDifference)
        return values.isEmpty ? nil : values.reduce(0, +) / Double(values.count)
    }

    /// True if any run heard opposite polarity, false if runs were clear, nil if none could tell.
    var oppositePolarity: Bool? {
        let values = measurements.compactMap(\.oppositePolarity)
        return values.isEmpty ? nil : values.contains(true)
    }

    var worstSNRDB: Double { measurements.map(\.snrDB).min() ?? 0 }

    // MARK: - Level check

    /// Plays three seconds of pink noise at the sweep's level and shows what the microphone hears.
    /// `outputChannels`: a multichannel output's channels to play it on (else `speakers`).
    func checkLevel(output: AudioDevice, input: AudioDevice, channel: Int, speakers: Speakers, outputChannels: [Int]? = nil) async {
        guard !isBusy else { return }
        guard await microphoneAccess() else {
            lastMessage = String(localized: "Tapline needs microphone access to measure your speakers.")
            return
        }
        phase = .checkingLevel
        defer { phase = .idle }
        do {
            let seconds = 3.0
            let (recording, _, _) = try await run(output: output, input: input, channel: channel, seconds: seconds + 0.3,
                                                  multichannel: outputChannels != nil) { rate, _ in
                let noise = Self.pinkNoise(count: Int(seconds * rate), amplitude: 0.12)
                if let outputChannels { return Dictionary(uniqueKeysWithValues: outputChannels.map { ($0, noise) }) }
                let silent = [Float](repeating: 0, count: noise.count)
                return [0: speakers == .right ? silent : noise, 1: speakers == .left ? silent : noise]
            }
            // The loudest moment of the whole check: the live meter's last reading is
            // the silence after the noise has stopped.
            let peak = Double(recording.map(abs).max() ?? 0)
            lastMessage = levelAdvice(peakDB: 20 * log10(max(peak, 1e-9)))
        } catch {
            lastMessage = error.localizedDescription
        }
    }

    private func levelAdvice(peakDB: Double) -> String {
        if peakDB > -3 { return String(localized: "Too loud: the microphone is close to clipping. Turn the volume or the preamp gain down.") }
        if peakDB < -45 { return String(localized: "Too quiet. Turn the volume up, raise the preamp gain, or check the microphone channel and phantom power.") }
        return String(localized: "Level looks good.")
    }

    // MARK: - Measurement

    func measure(output: AudioDevice, input: AudioDevice, channel: Int, speakers: Speakers) async {
        guard !isBusy else { return }
        guard await microphoneAccess() else {
            lastMessage = String(localized: "Tapline needs microphone access to measure your speakers.")
            return
        }
        lastMessage = nil
        measuringSpeakers = speakers
        phase = .measuring(0)
        defer { phase = .idle }
        if speakers == .stereo {
            await measureStereo(output: output, input: input, channel: channel)
            return
        }
        do {
            let total = Self.preRoll + Self.sweepSeconds + Self.tail
            let (recording, inputRate, outputRate) = try await run(output: output, input: input, channel: channel, seconds: total) { rate, micRate in
                let end = SineSweep.endFrequency(playbackRate: rate, recordingRate: micRate)
                let padded = [Float](repeating: 0, count: Int(Self.preRoll * rate)) + SineSweep(sampleRate: rate, duration: Self.sweepSeconds, endFrequency: end).signal
                let silent = [Float](repeating: 0, count: padded.count)
                return [0: speakers == .right ? silent : padded, 1: speakers == .left ? silent : padded]
            }

            phase = .analyzing
            let sweepRate = inputRate
            let calibration = calibration
            let result = await Task.detached(priority: .userInitiated) { () -> (FrequencyResponse?, Double, Double) in
                // The same sweep range as the one played, at the microphone's rate.
                let end = SineSweep.endFrequency(playbackRate: outputRate, recordingRate: sweepRate)
                let reference = SineSweep(sampleRate: sweepRate, duration: Self.sweepSeconds, endFrequency: end)
                let ir = reference.impulseResponse(from: recording)
                var fr = RoomResponse.frequencyResponse(impulse: ir, sampleRate: sweepRate)
                if let cal = calibration, let measured = fr { fr = RoomResponse.applyCalibration(measured, calibration: cal) }
                let peak = Double(recording.map(abs).max() ?? 0)
                let noiseCount = max(1, Int(Self.preRoll * 0.8 * sweepRate))
                let noise = recording.prefix(noiseCount)
                let sweepPart = recording.dropFirst(Int(Self.preRoll * sweepRate)).prefix(Int(Self.sweepSeconds * sweepRate))
                func rms<C: Collection>(_ c: C) -> Double where C.Element == Float {
                    sqrt(c.reduce(0.0) { $0 + Double($1) * Double($1) } / Double(max(c.count, 1)))
                }
                let snr = 20 * log10(max(rms(sweepPart), 1e-9) / max(rms(noise), 1e-9))
                return (fr, 20 * log10(max(peak, 1e-9)), snr)
            }.value

            guard let response = result.0 else {
                lastMessage = String(localized: "The measurement failed. Check that the microphone hears the speakers.")
                return
            }
            measurements.append(Measurement(speakers: speakers, response: response, peakDB: result.1, snrDB: result.2, position: nextPosition))
            lastMessage = Self.resultMessage(peakDB: result.1, snrDB: result.2)
        } catch {
            lastMessage = error.localizedDescription
        }
    }

    /// One recording: a sweep per speaker, then left / right / left timing chirps.
    private func measureStereo(output: AudioDevice, input: AudioDevice, channel: Int) async {
        do {
            let sweepSeconds = Self.sweepSeconds
            let duration = StereoSweepPlan(sampleRate: 48_000, sweepSeconds: sweepSeconds).duration
            let (recording, inputRate, outputRate) = try await run(output: output, input: input, channel: channel, seconds: duration + 0.4) { rate, micRate in
                let end = SineSweep.endFrequency(playbackRate: rate, recordingRate: micRate)
                let (left, right) = StereoSweepPlan(sampleRate: rate, sweepSeconds: sweepSeconds, endFrequency: end).render()
                return [0: left, 1: right]
            }
            phase = .analyzing
            let calibration = calibration
            let analysis = await Task.detached(priority: .userInitiated) {
                StereoSweepPlan.analyze(recording: recording, inputRate: inputRate, outputRate: outputRate, sweepSeconds: sweepSeconds)
            }.value
            guard var a = analysis else {
                lastMessage = String(localized: "The measurement failed. Check that the microphone hears the speakers.")
                return
            }
            if let cal = calibration {
                a.left = RoomResponse.applyCalibration(a.left, calibration: cal)
                a.right = RoomResponse.applyCalibration(a.right, calibration: cal)
            }
            guard let both = RoomResponse.average([a.left, a.right]) else { return }
            let snr = min(a.snrLeftDB, a.snrRightDB)
            measurements.append(Measurement(speakers: .stereo, response: both, left: a.left, right: a.right,
                                            arrivalDifference: a.arrivalDifference, oppositePolarity: a.oppositePolarity,
                                            peakDB: a.peakDB, snrDB: snr, position: nextPosition))
            lastMessage = Self.resultMessage(peakDB: a.peakDB, snrDB: snr)
        } catch {
            lastMessage = error.localizedDescription
        }
    }

    private static func resultMessage(peakDB: Double, snrDB: Double) -> String {
        if peakDB > -1 {
            return String(localized: "The microphone clipped during the sweep. Lower the volume and measure again.")
        }
        if snrDB < 20 {
            return String(localized: "The room is noisy or the level is low. The result may be unreliable.")
        }
        return String(localized: "Measured. Move the microphone a little and measure again to average several positions.")
    }

    // MARK: - Multichannel speakers

    /// One measurement of every speaker of a multichannel layout, at one microphone position.
    struct SpeakerRun: Identifiable {
        let id = UUID()
        var date = Date()
        /// The layout it was measured on; runs on another layout are not averaged.
        let layout: SpeakerLayout
        /// By device channel.
        let results: [SpeakerSweepPlan.SpeakerResult]
        let peakDB: Double
        var position: Int?

        var snrDB: Double { results.map(\.snrDB).min() ?? 0 }

        var quality: Measurement.Quality {
            if peakDB > -1 { return .clipped }
            if snrDB < 20 { return .noisy }
            return snrDB < 30 ? .fair : .good
        }
    }

    /// One speaker over every run: the power average of its responses, its mean arrival.
    struct SpeakerAverage: Identifiable {
        let channel: Int
        let position: SpeakerPosition
        let response: FrequencyResponse
        /// One per run, for the spread between positions.
        let responses: [FrequencyResponse]
        /// After the reference speaker's, seconds (positive: farther away).
        let arrival: Double?
        let oppositePolarity: Bool?
        let snrDB: Double

        var id: Int { channel }
    }

    private(set) var speakerRuns: [SpeakerRun] = []
    /// What the multichannel measurement in progress plays.
    private var measuringPlan: SpeakerSweepPlan?
    private var measuringLayout: SpeakerLayout?

    func speakerRuns(for layout: SpeakerLayout) -> [SpeakerRun] { speakerRuns.filter { $0.layout == layout } }

    func removeSpeakerRun(_ id: UUID) { speakerRuns.removeAll { $0.id == id } }

    func clearSpeakerRuns(for layout: SpeakerLayout) { speakerRuns.removeAll { $0.layout == layout } }

    func measuredPositions(for layout: SpeakerLayout) -> Set<Int> { Set(speakerRuns(for: layout).compactMap(\.position)) }

    func nextPosition(for layout: SpeakerLayout) -> Int? {
        let taken = measuredPositions(for: layout)
        return (0..<Self.positionCount).first { !taken.contains($0) }
    }

    /// Every measured speaker of `layout`, in channel order.
    func speakerAverages(for layout: SpeakerLayout) -> [SpeakerAverage] {
        let runs = speakerRuns(for: layout)
        return layout.speakerChannels.compactMap { channel in
            let results = runs.compactMap { run in run.results.first { $0.channel == channel } }
            guard let response = RoomResponse.average(results.map(\.response)) else { return nil }
            let arrivals = results.compactMap(\.arrival)
            let polarities = results.compactMap(\.oppositePolarity)
            return SpeakerAverage(channel: channel, position: layout.positions[channel], response: response, responses: results.map(\.response),
                                  arrival: arrivals.isEmpty ? nil : arrivals.reduce(0, +) / Double(arrivals.count),
                                  oppositePolarity: polarities.isEmpty ? nil : polarities.contains(true),
                                  snrDB: results.map(\.snrDB).min() ?? 0)
        }
    }

    /// What the multichannel measurement in progress plays now: which speaker, whether its
    /// sweep or its timing click, and its place in the order.
    var nowPlayingSpeaker: (position: SpeakerPosition, chirps: Bool, index: Int, count: Int)? {
        guard isMeasuring, let plan = measuringPlan, let layout = measuringLayout,
              let now = plan.playing(at: elapsedSeconds) else { return nil }
        let channel = plan.speakers[now.speaker].channel
        guard layout.positions.indices.contains(channel) else { return nil }
        return (layout.positions[channel], now.chirps, now.speaker + 1, plan.speakers.count)
    }

    /// Wiring check: two seconds of pink noise on one output channel.
    func identify(output: AudioDevice, channel: Int, seconds: Double = 2) async {
        guard !isBusy else { return }
        lastMessage = nil
        phase = .identifying(channel)
        defer { phase = .idle }
        do {
            try await play(output: output, seconds: seconds) { rate in
                [channel: Self.pinkNoise(count: Int(seconds * rate), amplitude: 0.12)]
            }
        } catch is CancellationError {
            return
        } catch {
            lastMessage = error.localizedDescription
        }
    }

    /// One recording: a sweep on every speaker of `layout` in turn, then timing clicks.
    func measureSpeakers(output: AudioDevice, input: AudioDevice, channel: Int, layout: SpeakerLayout) async {
        guard !isBusy else { return }
        guard await microphoneAccess() else {
            lastMessage = String(localized: "Tapline needs microphone access to measure your speakers.")
            return
        }
        let channels = layout.speakerChannels.filter { $0 < max(output.outputChannels, layout.channelCount) }
        guard !channels.isEmpty else { return }
        let speakers = channels.map { SpeakerSweepPlan.Speaker(channel: $0, isLFE: layout.positions[$0].isLFE) }
        let reference = layout.referenceChannel.flatMap { channels.firstIndex(of: $0) } ?? 0
        let sweepSeconds = SpeakerSweepPlan.defaultSweepSeconds
        // Only for its timing (what plays when): a low rate is quicker to make.
        let timeline = SpeakerSweepPlan(sampleRate: 8000, speakers: speakers, reference: reference, sweepSeconds: sweepSeconds)
        lastMessage = nil
        measuringPlan = timeline
        measuringLayout = layout
        phase = .measuring(0)
        defer {
            phase = .idle
            measuringPlan = nil
            measuringLayout = nil
        }
        do {
            let (recording, inputRate, outputRate) = try await run(output: output, input: input, channel: channel, seconds: timeline.duration + 0.4,
                                                                   multichannel: true) { rate, micRate in
                let end = SineSweep.endFrequency(playbackRate: rate, recordingRate: micRate)
                let signals = SpeakerSweepPlan(sampleRate: rate, speakers: speakers, reference: reference, sweepSeconds: sweepSeconds, endFrequency: end).render()
                return Dictionary(uniqueKeysWithValues: zip(channels, signals))
            }
            phase = .analyzing
            let calibration = calibration
            let analysis = await Task.detached(priority: .userInitiated) { () -> SpeakerSweepPlan.Analysis? in
                guard var a = SpeakerSweepPlan.analyze(recording: recording, inputRate: inputRate, outputRate: outputRate,
                                                       speakers: speakers, reference: reference, sweepSeconds: sweepSeconds) else { return nil }
                if let calibration {
                    for i in a.speakers.indices {
                        a.speakers[i].response = RoomResponse.applyCalibration(a.speakers[i].response, calibration: calibration)
                    }
                }
                return a
            }.value
            guard let analysis else {
                lastMessage = String(localized: "The measurement failed. Check that the microphone hears the speakers.")
                return
            }
            let run = SpeakerRun(layout: layout, results: analysis.speakers, peakDB: analysis.peakDB, position: nextPosition(for: layout))
            speakerRuns.append(run)
            lastMessage = Self.resultMessage(peakDB: run.peakDB, snrDB: run.snrDB)
        } catch {
            lastMessage = error.localizedDescription
        }
    }

    /// A synthetic home cinema measured at four positions, for screenshots: every speaker with
    /// its own room, distance and level, a subwoofer, and one height speaker wired backwards.
    func applySpeakerDemo(layout: SpeakerLayout) {
        guard speakerRuns(for: layout).isEmpty else { return }
        let grid = EQFitter.grid
        func response(_ position: SpeakerPosition, channel: Int, spot: Int) -> FrequencyResponse {
            let shift = [0.0, 2.5, -2, 1.5][spot % 4]
            var bands: [EQBand]
            if position.isLFE {
                bands = [EQBand(type: .highPass, frequency: 24, q: 0.8), EQBand(type: .lowPass, frequency: 95, q: 0.7),
                         EQBand(type: .lowPass, frequency: 110, q: 0.7),
                         EQBand(frequency: 42 + Double(spot), gain: 7 + shift, q: 5), EQBand(frequency: 68, gain: -6 - shift, q: 4)]
            } else {
                let small = position.group != .front
                bands = [EQBand(type: .highPass, frequency: small ? 75 : 40, q: 0.8),
                         EQBand(frequency: 48 + Double(channel), gain: 6 + shift, q: 5),
                         EQBand(frequency: 105 + 9 * Double(channel), gain: -7 - shift, q: 4),
                         EQBand(frequency: 170 + 6 * Double(channel), gain: 4 - shift / 2, q: 6),
                         EQBand(type: .highShelf, frequency: 9000, gain: position.isHeight ? -4 : -2, q: 0.7)]
            }
            let room = ResponseCurve.responseDB(bands: bands, frequencies: grid, sampleRate: 48_000)
            var seed = UInt64(channel * 31 + spot * 7 + 3)
            let level = 82 + [0.0, 0.6, 1.8, 9.5, -1.2, -0.8, -2.1, -1.7, -3.0, -2.6, -3.4, -2.9, -1, -1, -2, -2][channel % 16]
            return FrequencyResponse(frequencies: grid, levels: room.enumerated().map { i, v in
                seed = seed &* 6364136223846793005 &+ 1442695040888963407
                return level + v + (Double(seed >> 33) / Double(1 << 31) - 0.5) * (grid[i] > 1000 ? 2.0 : 0.6)
            })
        }
        // Metres farther than the front left (the subwoofer also lags by its low-pass filter).
        func distance(_ position: SpeakerPosition) -> Double {
            switch position {
            case .frontLeft: 0
            case .frontRight: 0.05
            case .center: -0.12
            case .lfe, .lfe2: 2.4
            case .surroundLeft, .surroundRight: -0.9
            case .rearLeft, .rearRight: -0.4
            case .wideLeft, .wideRight: -0.3
            default: position.isHeight ? -0.6 : 0
            }
        }
        let spots: [(snr: Double, peak: Double)] = [(51, -14), (47, -15), (28, -18), (49, -14)]
        speakerRuns.removeAll { $0.layout == layout }
        for (i, spot) in spots.enumerated() {
            let results = layout.speakerChannels.map { c -> SpeakerSweepPlan.SpeakerResult in
                let p = layout.positions[c]
                return SpeakerSweepPlan.SpeakerResult(channel: c, response: response(p, channel: c, spot: i),
                                                      arrival: (distance(p) + 0.01 * Double(i % 2)) / SpeakerAlignment.speedOfSound,
                                                      oppositePolarity: p.isLFE ? nil : p == .topRearRight,
                                                      snrDB: spot.snr - (p.isLFE ? 3 : 0))
            }
            let date = Calendar.current.date(bySettingHour: 20, minute: 10 + 2 * i, second: 0, of: Date()) ?? Date()
            speakerRuns.append(SpeakerRun(date: date, layout: layout, results: results, peakDB: spot.peak, position: i))
        }
    }

    // MARK: - Playback + recording

    private final class Recorder: @unchecked Sendable {
        private let lock = NSLock()
        private var samples: [Float] = []
        private var peak: Float = 0
        private var sumSquares: Double = 0
        private var count = 0

        func append(_ p: UnsafePointer<Float>, _ n: Int) {
            lock.lock()
            defer { lock.unlock() }
            samples.append(contentsOf: UnsafeBufferPointer(start: p, count: n))
            for i in 0..<n {
                let v = abs(p[i])
                if v > peak { peak = v }
                sumSquares += Double(p[i]) * Double(p[i])
            }
            count += n
        }

        /// Peak and RMS since the last call.
        func takeLevels() -> (Float, Double) {
            lock.lock()
            defer { lock.unlock() }
            let result = (peak, count > 0 ? sqrt(sumSquares / Double(count)) : 0)
            peak = 0
            sumSquares = 0
            count = 0
            return result
        }

        var all: [Float] {
            lock.lock()
            defer { lock.unlock() }
            return samples
        }
    }

    enum MeasurementError: LocalizedError {
        case audio(String)
        var errorDescription: String? {
            switch self { case .audio(let m): m }
        }
    }

    /// Plays the signals made by `signals` (given the output's and the input's actual sample
    /// rates; by output channel) on `output` and records one channel of `input` for `seconds`.
    /// Returns the recording, the input rate and the output rate. Without `multichannel` they
    /// are a left / right pair (channels 0 and 1) through the mixer, as on any stereo output.
    private func run(output: AudioDevice, input: AudioDevice, channel: Int, seconds: Double, multichannel: Bool = false,
                     signals: (_ outputRate: Double, _ inputRate: Double) -> [Int: [Float]]) async throws -> ([Float], Double, Double) {
        let player = AVAudioEngine()
        let recorder = AVAudioEngine()
        let node = AVAudioPlayerNode()
        // Every call that sets up or starts the engines goes through ObjCException.catching:
        // AVFoundation raises Objective-C exceptions for what it finds wrong only at run time.
        do {
            try ObjCException.catching {
                try player.outputNode.auAudioUnit.setDeviceID(output.id)
                try recorder.inputNode.auAudioUnit.setDeviceID(input.id)
            }
        } catch {
            throw MeasurementError.audio(String(localized: "Could not open the audio devices: \(error.localizedDescription)"))
        }

        // Tap in the format the selected input runs at: the input node's input format. Its
        // output format is not always the device's (on macOS 27.2 a tap in it raised
        // "Failed to create tap due to format mismatch", 1 ch at 48 kHz).
        let hardware = recorder.inputNode.inputFormat(forBus: 0)
        guard hardware.channelCount > 0, hardware.sampleRate > 0 else {
            throw MeasurementError.audio(String(localized: "The selected input has no channels."))
        }
        let inFormat = Self.tapFormat(for: hardware)
        let outRate = player.outputNode.outputFormat(forBus: 0).sampleRate > 0 ? player.outputNode.outputFormat(forBus: 0).sampleRate : output.sampleRate
        let channels = multichannel ? Self.outputChannelCount(player, output) : nil
        let (format, buffer) = try Self.signalBuffer(signals(outRate, inFormat.sampleRate), rate: outRate, channels: channels)

        let ch = min(max(channel, 0), Int(inFormat.channelCount) - 1)
        let store = Recorder()
        do {
            try ObjCException.catching {
                player.attach(node)
                if multichannel {
                    // Straight to the output: no mixer up- or down-mixes the channels.
                    player.connect(node, to: player.outputNode, format: format)
                } else {
                    player.connect(node, to: player.mainMixerNode, format: format)
                    player.mainMixerNode.outputVolume = 1
                }
                Self.record(recorder.inputNode, format: inFormat, channel: ch, into: store)
                recorder.prepare()
                try recorder.start()
                player.prepare()
                try player.start()
                Self.start(node, with: buffer)
            }
        } catch {
            Self.stop(player, node, recorder)
            throw MeasurementError.audio(String(localized: "Could not start the audio devices: \(error.localizedDescription)"))
        }
        defer { Self.stop(player, node, recorder) }

        let start = Date()
        runSeconds = seconds
        elapsedSeconds = 0
        while Date().timeIntervalSince(start) < seconds {
            try await Task.sleep(for: .milliseconds(100))
            let (peak, rms) = store.takeLevels()
            inputPeakDB = 20 * log10(max(Double(peak), 1e-9))
            inputRMSDB = 20 * log10(max(rms, 1e-9))
            elapsedSeconds = Date().timeIntervalSince(start)
            if case .measuring = phase { phase = .measuring(min(1, elapsedSeconds / seconds)) }
        }
        return (store.all, inFormat.sampleRate, outRate)
    }

    /// Plays the signals made by `signals` (given the output's sample rate; by output channel)
    /// on every channel of `output` for `seconds`, without recording. Cancelling stops it.
    private func play(output: AudioDevice, seconds: Double, signals: (_ outputRate: Double) -> [Int: [Float]]) async throws {
        let player = AVAudioEngine()
        let node = AVAudioPlayerNode()
        do {
            try ObjCException.catching { try player.outputNode.auAudioUnit.setDeviceID(output.id) }
        } catch {
            throw MeasurementError.audio(String(localized: "Could not open the audio devices: \(error.localizedDescription)"))
        }
        let rate = player.outputNode.outputFormat(forBus: 0).sampleRate > 0 ? player.outputNode.outputFormat(forBus: 0).sampleRate : output.sampleRate
        let (format, buffer) = try Self.signalBuffer(signals(rate), rate: rate, channels: Self.outputChannelCount(player, output))
        do {
            try ObjCException.catching {
                player.attach(node)
                player.connect(node, to: player.outputNode, format: format)
                player.prepare()
                try player.start()
                Self.start(node, with: buffer)
            }
        } catch {
            try? ObjCException.catching { node.stop() }
            try? ObjCException.catching { player.stop() }
            throw MeasurementError.audio(String(localized: "Could not start the audio devices: \(error.localizedDescription)"))
        }
        defer {
            try? ObjCException.catching { node.stop() }
            try? ObjCException.catching { player.stop() }
        }
        try await Task.sleep(for: .seconds(seconds))
    }

    /// The output's channel count as the engine sees it: the format it connects to must match.
    private static func outputChannelCount(_ engine: AVAudioEngine, _ output: AudioDevice) -> Int {
        let hardware = Int(engine.outputNode.outputFormat(forBus: 0).channelCount)
        return max(hardware > 0 ? hardware : output.outputChannels, 2)
    }

    /// The test signal as a buffer: a stereo pair from channels 0 and 1 (`channels` nil), or one
    /// buffer channel per output channel in a discrete layout, so they reach the speakers 1:1.
    private static func signalBuffer(_ signals: [Int: [Float]], rate: Double, channels: Int?) throws -> (AVAudioFormat, AVAudioPCMBuffer) {
        let count = signals.values.map(\.count).max() ?? 0
        let format: AVAudioFormat?
        if let channels {
            let layout = AVAudioChannelLayout(layoutTag: kAudioChannelLayoutTag_DiscreteInOrder | UInt32(channels))
            format = layout.map { AVAudioFormat(standardFormatWithSampleRate: rate, channelLayout: $0) }
        } else {
            format = AVAudioFormat(standardFormatWithSampleRate: rate, channels: 2)
        }
        guard count > 0, let format, let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(count)),
              let data = buffer.floatChannelData else {
            throw MeasurementError.audio(String(localized: "Could not prepare the test signal."))
        }
        buffer.frameLength = AVAudioFrameCount(count)
        for c in 0..<Int(format.channelCount) {
            data[c].update(repeating: 0, count: count)
            guard let signal = signals[c], !signal.isEmpty else { continue }
            signal.withUnsafeBufferPointer { data[c].update(from: $0.baseAddress!, count: min(signal.count, count)) }
        }
        return (format, buffer)
    }

    /// `hardware` as deinterleaved Float32, which the tap block reads channel by channel.
    private static func tapFormat(for hardware: AVAudioFormat) -> AVAudioFormat {
        if hardware.commonFormat == .pcmFormatFloat32, !hardware.isInterleaved { return hardware }
        // Above two channels a format needs a channel layout.
        if let layout = hardware.channelLayout {
            return AVAudioFormat(standardFormatWithSampleRate: hardware.sampleRate, channelLayout: layout)
        }
        return AVAudioFormat(standardFormatWithSampleRate: hardware.sampleRate, channels: hardware.channelCount) ?? hardware
    }

    /// Stops what `run` started, each call on its own: an exception from one (a node that
    /// was never attached) must not leave the other engine running.
    private static func stop(_ player: AVAudioEngine, _ node: AVAudioPlayerNode, _ recorder: AVAudioEngine) {
        try? ObjCException.catching { node.stop() }
        try? ObjCException.catching { player.stop() }
        try? ObjCException.catching { recorder.inputNode.removeTap(onBus: 0) }
        try? ObjCException.catching { recorder.stop() }
    }

    private nonisolated static func start(_ node: AVAudioPlayerNode, with buffer: AVAudioPCMBuffer) {
        node.scheduleBuffer(buffer, at: nil, options: [], completionHandler: nil)
        node.play()
    }

    /// Records one channel of `input` into `store`. The tap block runs on Core Audio's
    /// realtime thread: written inside this main-actor class it would be main-actor
    /// isolated, and the Swift runtime's isolation check traps on the first buffer
    /// (macOS 27). Here it is nonisolated, and `store` locks its own state.
    private nonisolated static func record(_ input: AVAudioInputNode, format: AVAudioFormat, channel: Int, into store: Recorder) {
        input.installTap(onBus: 0, bufferSize: 4096, format: format) { pcm, _ in
            guard let data = pcm.floatChannelData else { return }
            store.append(data[channel], Int(pcm.frameLength))
        }
    }

    /// A synthetic room (modes, a cancellation dip, a slightly louder and farther
    /// right speaker) measured at four positions, for screenshots.
    func applyDemo() {
        guard measurements.isEmpty else { return }
        let grid = EQFitter.grid
        func speaker(_ extra: [EQBand], level: Double, seed start: UInt64, shift: Double) -> FrequencyResponse {
            let room = ResponseCurve.responseDB(bands: [
                EQBand(type: .highPass, frequency: 38, q: 0.8),
                EQBand(type: .peak, frequency: 47, gain: 8 + shift, q: 5),
                EQBand(type: .peak, frequency: 112, gain: -9 - shift, q: 4),
                EQBand(type: .peak, frequency: 176, gain: 5 - shift / 2, q: 6),
                EQBand(type: .peak, frequency: 300, gain: -3, q: 3),
                EQBand(type: .highShelf, frequency: 9000, gain: -2, q: 0.7),
            ] + extra, frequencies: grid, sampleRate: 48_000)
            var seed = start
            let levels = room.enumerated().map { i, v -> Double in
                seed = seed &* 6364136223846793005 &+ 1442695040888963407
                let wiggle = (Double(seed >> 33) / Double(1 << 31) - 0.5) * (grid[i] > 1000 ? 2.0 : 0.6)
                return level + v + wiggle
            }
            return FrequencyResponse(frequencies: grid, levels: levels)
        }
        // The modes change a little from one position to the next; the third was taken in a noisier moment.
        let spots: [(shift: Double, level: Double, snr: Double, peak: Double)] = [
            (0, 0, 52, -14), (3, -0.4, 47, -15), (-2.5, 0.3, 27, -18), (1.5, -0.2, 50, -14),
        ]
        measurements = spots.enumerated().map { i, spot -> Measurement in
            let seed = UInt64(i) * 101
            let left = speaker([EQBand(type: .peak, frequency: 68, gain: 3, q: 4)], level: 82 + spot.level, seed: 42 + seed, shift: spot.shift)
            let right = speaker([EQBand(type: .peak, frequency: 68, gain: -4, q: 4)], level: 82.9 + spot.level, seed: 7 + seed, shift: spot.shift)
            let both = RoomResponse.average([left, right]) ?? left
            // Fixed times, so snapshots come out the same on every run.
            let date = Calendar.current.date(bySettingHour: 9, minute: 41 + 2 * i, second: 0, of: Date()) ?? Date()
            return Measurement(date: date, speakers: .stereo, response: both, left: left, right: right,
                               arrivalDifference: 0.00011, oppositePolarity: false, peakDB: spot.peak, snrDB: spot.snr, position: i)
        }
    }

    /// Paul Kellet's economy pink noise filter.
    private static func pinkNoise(count: Int, amplitude: Float) -> [Float] {
        var b0: Float = 0, b1: Float = 0, b2: Float = 0
        var out = [Float](repeating: 0, count: count)
        var generator = SystemRandomNumberGenerator()
        for i in 0..<count {
            let white = Float.random(in: -1...1, using: &generator)
            b0 = 0.99765 * b0 + white * 0.0990460
            b1 = 0.96300 * b1 + white * 0.2965164
            b2 = 0.57000 * b2 + white * 1.0526913
            out[i] = (b0 + b1 + b2 + white * 0.1848) * 0.11 * amplitude * 4
        }
        let fade = min(count / 4, 4800)
        for i in 0..<fade {
            let g = Float(i) / Float(fade)
            out[i] *= g
            out[count - 1 - i] *= g
        }
        return out
    }
}
