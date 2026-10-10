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
        case both, left, right
        var id: String { rawValue }

        var title: LocalizedStringResource {
            switch self {
            case .both: "Both speakers"
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
        let response: FrequencyResponse
        let peakDB: Double
        let snrDB: Double
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
            let noise = Self.pinkNoise(count: Int(seconds * output.sampleRate), amplitude: 0.12)
            _ = try await run(signal: noise, output: output, input: input, channel: channel, speakers: speakers, seconds: seconds + 0.3)
            lastMessage = levelAdvice()
        } catch {
            lastMessage = error.localizedDescription
        }
    }

    private func levelAdvice() -> String {
        if inputPeakDB > -3 { return String(localized: "Too loud: the microphone is close to clipping. Turn the volume or the preamp gain down.") }
        if inputPeakDB < -45 { return String(localized: "Too quiet. Turn the volume up, raise the preamp gain, or check the microphone channel and phantom power.") }
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
        do {
            let playback = SineSweep(sampleRate: output.sampleRate, duration: Self.sweepSeconds)
            let padded = [Float](repeating: 0, count: Int(Self.preRoll * output.sampleRate)) + playback.signal
            let total = Self.preRoll + Self.sweepSeconds + Self.tail
            let (recording, inputRate) = try await run(signal: padded, output: output, input: input, channel: channel, speakers: speakers, seconds: total)

            phase = .analyzing
            let sweepRate = inputRate
            let calibration = calibration
            let result = await Task.detached(priority: .userInitiated) { () -> (FrequencyResponse?, Double, Double) in
                let reference = sweepRate == playback.sampleRate ? playback : SineSweep(sampleRate: sweepRate, duration: Self.sweepSeconds)
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
            if result.1 > -1 {
                lastMessage = String(localized: "The microphone clipped during the sweep. Lower the volume and measure again.")
            } else if result.2 < 20 {
                lastMessage = String(localized: "The room is noisy or the level is low. The result may be unreliable.")
            } else {
                lastMessage = String(localized: "Measured. Move the microphone a little and measure again to average several positions.")
            }
        } catch {
            lastMessage = error.localizedDescription
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

    /// Plays `signal` on `output` and records one channel of `input` for `seconds`.
    private func run(signal: [Float], output: AudioDevice, input: AudioDevice, channel: Int, speakers: Speakers, seconds: Double) async throws -> ([Float], Double) {
        let player = AVAudioEngine()
        let recorder = AVAudioEngine()
        let node = AVAudioPlayerNode()
        do {
            try player.outputNode.auAudioUnit.setDeviceID(output.id)
            try recorder.inputNode.auAudioUnit.setDeviceID(input.id)
        } catch {
            throw MeasurementError.audio(String(localized: "Could not open the audio devices: \(error.localizedDescription)"))
        }

        let outRate = player.outputNode.outputFormat(forBus: 0).sampleRate > 0 ? player.outputNode.outputFormat(forBus: 0).sampleRate : output.sampleRate
        guard let format = AVAudioFormat(standardFormatWithSampleRate: outRate, channels: 2),
              let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(signal.count)) else {
            throw MeasurementError.audio(String(localized: "Could not prepare the test signal."))
        }
        buffer.frameLength = AVAudioFrameCount(signal.count)
        signal.withUnsafeBufferPointer { src in
            if speakers != .right { buffer.floatChannelData![0].update(from: src.baseAddress!, count: signal.count) }
            else { buffer.floatChannelData![0].update(repeating: 0, count: signal.count) }
            if speakers != .left { buffer.floatChannelData![1].update(from: src.baseAddress!, count: signal.count) }
            else { buffer.floatChannelData![1].update(repeating: 0, count: signal.count) }
        }
        player.attach(node)
        player.connect(node, to: player.mainMixerNode, format: format)
        player.mainMixerNode.outputVolume = 1

        let inFormat = recorder.inputNode.outputFormat(forBus: 0)
        guard inFormat.channelCount > 0, inFormat.sampleRate > 0 else {
            throw MeasurementError.audio(String(localized: "The selected input has no channels."))
        }
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
        return (store.all, inFormat.sampleRate)
    }

    private nonisolated static func start(_ node: AVAudioPlayerNode, with buffer: AVAudioPCMBuffer) {
        node.scheduleBuffer(buffer, at: nil, options: [], completionHandler: nil)
        node.play()
    }

    /// A synthetic room (two modes, one cancellation dip) for screenshots.
    func applyDemo() {
        guard measurements.isEmpty else { return }
        let grid = EQFitter.grid
        let room = ResponseCurve.responseDB(bands: [
            EQBand(type: .highPass, frequency: 38, q: 0.8),
            EQBand(type: .peak, frequency: 47, gain: 8, q: 5),
            EQBand(type: .peak, frequency: 112, gain: -9, q: 4),
            EQBand(type: .peak, frequency: 176, gain: 5, q: 6),
            EQBand(type: .peak, frequency: 300, gain: -3, q: 3),
            EQBand(type: .highShelf, frequency: 9000, gain: -2, q: 0.7),
        ], frequencies: grid, sampleRate: 48_000)
        var seed: UInt64 = 42
        let levels = room.enumerated().map { i, v -> Double in
            seed = seed &* 6364136223846793005 &+ 1442695040888963407
            let wiggle = (Double(seed >> 33) / Double(1 << 31) - 0.5) * (grid[i] > 1000 ? 2.0 : 0.6)
            return 82 + v + wiggle
        }
        measurements = [Measurement(speakers: .both, response: FrequencyResponse(frequencies: grid, levels: levels), peakDB: -14, snrDB: 52)]
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
