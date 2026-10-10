import CoreAudio
import Foundation
import OSLog
import Synchronization
import TaplineCore
import TaplineDSP

/// Describes one tapped stream that feeds the mixer.
struct TapSourceSpec: Equatable, Sendable {
    /// "main" for the system-wide tap, otherwise the app id.
    var key: String
    var processes: [AudioObjectID]
    var bundleIDs: [String]
    /// true: tap everything except `processes` / `bundleIDs`.
    var exclusive: Bool

    static let mainKey = "main"
}

/// Routes system audio through the DSP kernel:
///
///     apps ──(process taps, muted at the source)──► private aggregate device
///          ──► tl_ioproc (mix → EQ → effects → limiter) ──► output device's stereo pair
///
/// The aggregate device contains the real output device as its only sub-device,
/// so input (taps) and output share one clock and one IO cycle.
@MainActor
final class TapEngine {
    enum State: Equatable {
        case stopped
        case running
        case failed(String)
    }

    let kernel = DSPKernel()
    private(set) var state: State = .stopped
    private(set) var sources: [TapSourceSpec] = []
    private(set) var outputDevice: AudioDevice?
    private(set) var sampleRate: Double = 48_000
    private(set) var latencyFrames = 0
    private(set) var diagnostics: [String] = []
    /// IO cycles the HAL reported as overloaded (audible dropouts).
    let overloads = OverloadCounter()

    enum TapMode { case deviceStream, mixdown }
    private(set) var tapMode: TapMode = .deviceStream
    private(set) var driftCompensation = false

    private var tapIDs: [AudioObjectID] = []
    private var tapDescriptions: [CATapDescription] = []
    private var aggregateID: AudioObjectID = 0
    private var procID: AudioDeviceIOProcID?
    private var overloadAddress = CA.address(kAudioDeviceProcessorOverload)
    private var overloadBlock: AudioObjectPropertyListenerBlock?

    private let log = Logger(subsystem: "io.github.belugaming.Tapline", category: "engine")

    var isRunning: Bool { state == .running }

    func start(output: AudioDevice, sources: [TapSourceSpec]) throws {
        stop()
        diagnostics = []
        do {
            try build(output: output, sources: sources)
            state = .running
        } catch {
            teardown()
            state = .failed(error.localizedDescription)
            log.error("Engine start failed: \(error.localizedDescription, privacy: .public)")
            throw error
        }
    }

    func stop() {
        teardown()
        if case .failed = state { return }
        state = .stopped
    }

    /// Changes which processes a running tap captures without rebuilding the aggregate.
    func update(source spec: TapSourceSpec) {
        guard let index = sources.firstIndex(where: { $0.key == spec.key }), sources[index] != spec else { return }
        let description = tapDescriptions[index]
        description.processes = spec.processes
        description.bundleIDs = spec.bundleIDs
        // kAudioTapPropertyDescription takes a reference to the CATapDescription object.
        var address = CA.address(kAudioTapPropertyDescription)
        var reference = Unmanaged.passUnretained(description)
        let status = withUnsafeMutablePointer(to: &reference) {
            AudioObjectSetPropertyData(tapIDs[index], &address, 0, nil, UInt32(MemoryLayout<Unmanaged<CATapDescription>>.size), $0)
        }
        if status == noErr {
            sources[index] = spec
        } else {
            log.error("Tap update failed: \(fourCharCode(status), privacy: .public)")
        }
    }

    // MARK: - Build / teardown

    private func build(output: AudioDevice, sources: [TapSourceSpec]) throws {
        // Where the system's stereo pair lives on the device.
        let deviceStreams = CA.streamConfiguration(output.id, input: false)
        var stereo = output.preferredStereo.map(Int.init)
        if stereo.count < 2 { stereo = output.outputChannels >= 2 ? [1, 2] : [1] }
        guard let deviceLeft = Self.locate(channel: stereo[0], in: deviceStreams) else {
            throw CoreAudioError(status: kAudioHardwareBadStreamError, operation: "No output channel \(stereo[0])")
        }

        // 1. One process tap per source, muting its processes only while we read them.
        //    Prefer taps of the device's own output stream: they run on the device's clock
        //    and only see audio headed for this device. Fall back to stereo mixdowns.
        let specs = Array(sources.prefix(DSPKernel.maxSources))
        do {
            try createTaps(specs, deviceUID: output.uid, stream: deviceLeft.buffer)
            tapMode = .deviceStream
        } catch {
            log.error("Device stream tap failed (\(error.localizedDescription, privacy: .public)), using stereo mixdown")
            destroyTaps()
            try createTaps(specs, deviceUID: nil, stream: 0)
            tapMode = .mixdown
        }

        // 2. Private aggregate: output device (clock) + all taps.
        //    Taps of a Bluetooth or virtual device already follow that device's clock; drift
        //    compensation there makes the HAL add/drop samples (wobble, rhythmic crackle).
        //    Wired, USB and built-in devices have their own crystal and need it.
        driftCompensation = !(output.isBluetooth || output.isVirtual)
        let aggregateUID = "\(AudioDeviceCenter.privateAggregatePrefix).\(UUID().uuidString)"
        let composition: [String: Any] = [
            kAudioAggregateDeviceNameKey: "Tapline",
            kAudioAggregateDeviceUIDKey: aggregateUID,
            kAudioAggregateDeviceMainSubDeviceKey: output.uid,
            kAudioAggregateDeviceClockDeviceKey: output.uid,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: false,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: output.uid, kAudioSubDeviceDriftCompensationKey: false]],
            kAudioAggregateDeviceTapListKey: tapDescriptions.map {
                [kAudioSubTapUIDKey: $0.uuid.uuidString,
                 kAudioSubTapDriftCompensationKey: driftCompensation,
                 // When the HAL has to resample, use its best converter.
                 kAudioSubTapDriftCompensationQualityKey: kAudioAggregateDriftCompensationMaxQuality]
            },
        ]
        var aggregate = AudioObjectID(kAudioObjectUnknown)
        try CA.check(AudioHardwareCreateAggregateDevice(composition as CFDictionary, &aggregate), "Create aggregate device")
        aggregateID = aggregate

        // 3. Work out the buffer layout. Sub-device input streams come first, then one stream per tap.
        let inputs = CA.streamConfiguration(aggregate, input: true)
        let outputs = CA.streamConfiguration(aggregate, input: false)
        let tapCount = tapIDs.count
        guard inputs.count >= tapCount else {
            throw CoreAudioError(status: kAudioHardwareBadStreamError, operation: "Aggregate has \(inputs.count) input streams for \(tapCount) taps")
        }
        let firstTap = inputs.count - tapCount
        let sourceIndices = (0..<tapCount).map { firstTap + $0 }

        guard let left = Self.locate(channel: stereo[0], in: outputs) else {
            throw CoreAudioError(status: kAudioHardwareBadStreamError, operation: "No output channel \(stereo[0])")
        }
        let right = stereo.count > 1 ? Self.locate(channel: stereo[1], in: outputs) : nil
        // Device-stream taps mirror the output stream's channel layout.
        let streamTaps = tapMode == .deviceStream
        let rightInSameStream = right.map { $0.buffer == left.buffer } ?? false

        sampleRate = CA.read(aggregate, kAudioDevicePropertyNominalSampleRate, default: output.sampleRate)
        if sampleRate <= 0 { sampleRate = 48_000 }

        let layout = KernelLayout(
            sourceBufferIndices: sourceIndices,
            sourceLeftChannel: streamTaps ? left.channel : 0,
            sourceRightChannel: streamTaps ? (rightInSameStream ? right!.channel : left.channel) : 1,
            leftBuffer: left.buffer, leftChannel: left.channel,
            rightBuffer: right?.buffer ?? -1, rightChannel: right?.channel ?? 0,
            passthrough: streamTaps && rightInSameStream
        )
        kernel.configure(sampleRate: sampleRate, layout: layout)

        let bufferFrames = CA.read(aggregate, kAudioDevicePropertyBufferFrameSize, default: UInt32(0))
        let safety = CA.read(aggregate, kAudioDevicePropertySafetyOffset, kAudioObjectPropertyScopeOutput, default: UInt32(0))
        let deviceLatency = CA.read(aggregate, kAudioDevicePropertyLatency, kAudioObjectPropertyScopeOutput, default: UInt32(0))
        latencyFrames = Int(bufferFrames + safety + deviceLatency) + Int((0.0015 * sampleRate).rounded())

        for (i, tapID) in tapIDs.enumerated() {
            let format = CA.read(tapID, kAudioTapPropertyFormat, default: AudioStreamBasicDescription())
            diagnostics.append("tap[\(i)] \(sources[i].key): \(format.mChannelsPerFrame)ch @ \(Int(format.mSampleRate)) Hz → input buffer \(sourceIndices[i])")
        }
        diagnostics.append("aggregate inputs \(inputs) outputs \(outputs) @ \(Int(sampleRate)) Hz, buffer \(bufferFrames)")
        diagnostics.append("tap mode \(tapMode == .deviceStream ? "device stream \(deviceLeft.buffer)" : "stereo mixdown"), drift compensation \(driftCompensation ? "on" : "off") (\(output.transportName))")
        diagnostics.append("output L → buffer \(left.buffer) ch \(left.channel), R → \(right.map { "buffer \($0.buffer) ch \($0.channel)" } ?? "mono")")
        for line in diagnostics { log.info("\(line, privacy: .public)") }

        // 4. IOProc in C, reading only the tap streams (never the hardware inputs).
        var proc: AudioDeviceIOProcID?
        try CA.check(AudioDeviceCreateIOProcID(aggregate, DSPKernel.ioProc, kernel.clientData, &proc), "Create IOProc")
        procID = proc

        let inputUsage = (0..<inputs.count).map { UInt32($0 >= firstTap ? 1 : 0) }
        let usageStatus = inputUsage.withUnsafeBufferPointer {
            tl_set_stream_usage(aggregate, proc, 1, $0.baseAddress!, UInt32(inputs.count))
        }
        if usageStatus != noErr {
            diagnostics.append("stream usage not applied (\(fourCharCode(usageStatus)))")
        }

        overloads.reset()
        let counter = overloads
        let block: AudioObjectPropertyListenerBlock = { @Sendable _, _ in
            counter.increment()
        }
        overloadBlock = block
        AudioObjectAddPropertyListenerBlock(aggregate, &overloadAddress, DispatchQueue.global(qos: .utility), block)

        try CA.check(AudioDeviceStart(aggregate, proc), "Start audio device")
        outputDevice = output
    }

    private func createTaps(_ specs: [TapSourceSpec], deviceUID: String?, stream: Int) throws {
        for spec in specs {
            let description: CATapDescription
            switch (spec.exclusive, deviceUID) {
            case (true, let uid?): description = CATapDescription(excludingProcesses: spec.processes, deviceUID: uid, stream: UInt(stream))
            case (false, let uid?): description = CATapDescription(processes: spec.processes, deviceUID: uid, stream: UInt(stream))
            case (true, nil): description = CATapDescription(stereoGlobalTapButExcludeProcesses: spec.processes)
            case (false, nil): description = CATapDescription(stereoMixdownOfProcesses: spec.processes)
            }
            if !spec.exclusive { description.isProcessRestoreEnabled = true }
            description.bundleIDs = spec.bundleIDs
            description.name = "Tapline \(spec.key)"
            description.isPrivate = true
            description.muteBehavior = .mutedWhenTapped

            var tapID = AudioObjectID(kAudioObjectUnknown)
            try CA.check(AudioHardwareCreateProcessTap(description, &tapID), "Create process tap")
            tapIDs.append(tapID)
            tapDescriptions.append(description)
            self.sources.append(spec)
        }
    }

    private func destroyTaps() {
        for tap in tapIDs { AudioHardwareDestroyProcessTap(tap) }
        tapIDs = []
        tapDescriptions = []
        sources = []
    }

    private func teardown() {
        if aggregateID != 0 {
            if let procID {
                AudioDeviceStop(aggregateID, procID)
                AudioDeviceDestroyIOProcID(aggregateID, procID)
            }
            if let overloadBlock {
                AudioObjectRemovePropertyListenerBlock(aggregateID, &overloadAddress, DispatchQueue.global(qos: .utility), overloadBlock)
            }
            AudioHardwareDestroyAggregateDevice(aggregateID)
        }
        for tap in tapIDs { AudioHardwareDestroyProcessTap(tap) }
        procID = nil
        overloadBlock = nil
        aggregateID = 0
        tapIDs = []
        tapDescriptions = []
        sources = []
        outputDevice = nil
    }

    /// Maps a 1-based device channel number onto (buffer index, channel within buffer).
    static func locate(channel: Int, in config: [Int]) -> (buffer: Int, channel: Int)? {
        var remaining = channel - 1
        guard remaining >= 0 else { return nil }
        for (i, n) in config.enumerated() {
            if remaining < n { return (i, remaining) }
            remaining -= n
        }
        return nil
    }
}

final class OverloadCounter: Sendable {
    private let atomic = Atomic<Int>(0)
    var value: Int { atomic.load(ordering: .relaxed) }
    func increment() { atomic.add(1, ordering: .relaxed) }
    func reset() { atomic.store(0, ordering: .relaxed) }
}

extension TapEngine.State {
    var errorMessage: String? {
        if case .failed(let message) = self { return message }
        return nil
    }
}
