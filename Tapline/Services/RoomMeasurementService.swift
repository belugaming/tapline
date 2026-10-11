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
        case measuring(Double)
        case analyzing
    }

    struct Measurement: Identifiable {
        let id = UUID()
        let date = Date()
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

        var leftResponse: FrequencyResponse? { left ?? (speakers == .left ? response : nil) }
        var rightResponse: FrequencyResponse? { right ?? (speakers == .right ? response : nil) }
    }

    private(set) var phase: Phase = .idle
    private(set) var measurements: [Measurement] = []
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
    func checkLevel(output: AudioDevice, input: AudioDevice, channel: Int, speakers: Speakers) async {
        guard !isBusy else { return }
        guard await microphoneAccess() else {
            lastMessage = String(localized: "Tapline needs microphone access to measure your speakers.")
            return
        }
        phase = .checkingLevel
        defer { phase = .idle }
        do {
            let seconds = 3.0
            let (recording, _, _) = try await run(output: output, input: input, channel: channel, seconds: seconds + 0.3) { rate, _ in
                let noise = Self.pinkNoise(count: Int(seconds * rate), amplitude: 0.12)
                let silent = [Float](repeating: 0, count: noise.count)
                return (speakers == .right ? silent : noise, speakers == .left ? silent : noise)
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
                return (speakers == .right ? silent : padded, speakers == .left ? silent : padded)
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
            measurements.append(Measurement(speakers: speakers, response: response, peakDB: result.1, snrDB: result.2))
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
                return StereoSweepPlan(sampleRate: rate, sweepSeconds: sweepSeconds, endFrequency: end).render()
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
                                            peakDB: a.peakDB, snrDB: snr))
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

    /// Plays the left / right signals made by `signals` (given the output's and the input's
    /// actual sample rates) on `output` and records one channel of `input` for `seconds`.
    /// Returns the recording, the input rate and the output rate.
    private func run(output: AudioDevice, input: AudioDevice, channel: Int, seconds: Double,
                     signals: (_ outputRate: Double, _ inputRate: Double) -> (left: [Float], right: [Float])) async throws -> ([Float], Double, Double) {
        let player = AVAudioEngine()
        let recorder = AVAudioEngine()
        let node = AVAudioPlayerNode()
        do {
            try player.outputNode.auAudioUnit.setDeviceID(output.id)
            try recorder.inputNode.auAudioUnit.setDeviceID(input.id)
        } catch {
            throw MeasurementError.audio(String(localized: "Could not open the audio devices: \(error.localizedDescription)"))
        }

        let inFormat = recorder.inputNode.outputFormat(forBus: 0)
        guard inFormat.channelCount > 0, inFormat.sampleRate > 0 else {
            throw MeasurementError.audio(String(localized: "The selected input has no channels."))
        }
        let outRate = player.outputNode.outputFormat(forBus: 0).sampleRate > 0 ? player.outputNode.outputFormat(forBus: 0).sampleRate : output.sampleRate
        let (left, right) = signals(outRate, inFormat.sampleRate)
        let count = min(left.count, right.count)
        guard let format = AVAudioFormat(standardFormatWithSampleRate: outRate, channels: 2),
              let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(count)) else {
            throw MeasurementError.audio(String(localized: "Could not prepare the test signal."))
        }
        buffer.frameLength = AVAudioFrameCount(count)
        left.withUnsafeBufferPointer { buffer.floatChannelData![0].update(from: $0.baseAddress!, count: count) }
        right.withUnsafeBufferPointer { buffer.floatChannelData![1].update(from: $0.baseAddress!, count: count) }
        player.attach(node)
        player.connect(node, to: player.mainMixerNode, format: format)
        player.mainMixerNode.outputVolume = 1

        let ch = min(max(channel, 0), Int(inFormat.channelCount) - 1)
        let store = Recorder()
        recorder.inputNode.installTap(onBus: 0, bufferSize: 4096, format: inFormat) { pcm, _ in
            guard let data = pcm.floatChannelData else { return }
            store.append(data[ch], Int(pcm.frameLength))
        }

        do {
            recorder.prepare()
            try recorder.start()
            player.prepare()
            try player.start()
        } catch {
            recorder.inputNode.removeTap(onBus: 0)
            recorder.stop()
            player.stop()
            throw MeasurementError.audio(String(localized: "Could not start the audio devices: \(error.localizedDescription)"))
        }
        defer {
            node.stop()
            player.stop()
            recorder.inputNode.removeTap(onBus: 0)
            recorder.stop()
        }

        Self.start(node, with: buffer)

        let start = Date()
        while Date().timeIntervalSince(start) < seconds {
            try await Task.sleep(for: .milliseconds(100))
            let (peak, rms) = store.takeLevels()
            inputPeakDB = 20 * log10(max(Double(peak), 1e-9))
            inputRMSDB = 20 * log10(max(rms, 1e-9))
            if case .measuring = phase { phase = .measuring(min(1, Date().timeIntervalSince(start) / seconds)) }
        }
        return (store.all, inFormat.sampleRate, outRate)
    }

    private nonisolated static func start(_ node: AVAudioPlayerNode, with buffer: AVAudioPCMBuffer) {
        node.scheduleBuffer(buffer, at: nil, options: [], completionHandler: nil)
        node.play()
    }

    /// A synthetic room (modes, a cancellation dip, a slightly louder and farther
    /// right speaker) for screenshots.
    func applyDemo() {
        guard measurements.isEmpty else { return }
        let grid = EQFitter.grid
        func speaker(_ extra: [EQBand], level: Double, seed start: UInt64) -> FrequencyResponse {
            let room = ResponseCurve.responseDB(bands: [
                EQBand(type: .highPass, frequency: 38, q: 0.8),
                EQBand(type: .peak, frequency: 47, gain: 8, q: 5),
                EQBand(type: .peak, frequency: 112, gain: -9, q: 4),
                EQBand(type: .peak, frequency: 176, gain: 5, q: 6),
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
        let left = speaker([EQBand(type: .peak, frequency: 68, gain: 3, q: 4)], level: 82, seed: 42)
        let right = speaker([EQBand(type: .peak, frequency: 68, gain: -4, q: 4)], level: 82.9, seed: 7)
        let both = RoomResponse.average([left, right]) ?? left
        measurements = [Measurement(speakers: .stereo, response: both, left: left, right: right,
                                    arrivalDifference: 0.00011, oppositePolarity: false, peakDB: -14, snrDB: 52)]
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
