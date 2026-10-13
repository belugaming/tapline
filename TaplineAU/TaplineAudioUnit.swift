import AVFoundation
import AudioToolbox
import OSLog
import TaplineCore
import TaplineDSP

/// What the render block works with: the buffers of the host's current format, set while the
/// host does not render (allocate / deallocate), read by the render thread.
private struct RenderBuffers {
    /// The input bus's buffer list, pulled into each cycle.
    var input: UnsafeMutablePointer<AudioBufferList>?
    /// Memory for up to two input channels and for the two output channels when the host
    /// passes none, `maxFrames` samples each.
    var inputData: (UnsafeMutablePointer<Float>?, UnsafeMutablePointer<Float>?) = (nil, nil)
    var outputData: (UnsafeMutablePointer<Float>?, UnsafeMutablePointer<Float>?) = (nil, nil)
    var maxFrames: AUAudioFrameCount = 0
    /// While the host bypasses the effect, the input still comes out as late as the effect's
    /// latency, which the host compensates: delay lines (left, then right, `delayMask + 1`
    /// samples each) that always record.
    var delay: UnsafeMutablePointer<Float>?
    var delayMask = 0
    var delayPosition = 0
}

/// A pointer the render block may capture: nothing but the block reads it while rendering.
private struct Unchecked<Pointer>: @unchecked Sendable {
    let pointer: Pointer
}

/// Tapline as an Audio Unit effect (aufx Tpln Tpln): the app's EQ, presets and headphone
/// corrections, Listen as, Virtual Speakers and linear phase on a stereo bus (or mono in,
/// stereo out), through the same kernel as the app. Its full state goes into the host's project.
final class TaplineAudioUnit: AUAudioUnit, @unchecked Sendable {
    let engine = AudioUnitEngine()

    private var inputBus: AUAudioUnitBus!
    private var outputBus: AUAudioUnitBus!
    private var inputBusArray: AUAudioUnitBusArray!
    private var outputBusArray: AUAudioUnitBusArray!
    private let buffers: UnsafeMutablePointer<RenderBuffers>
    private var inputBuffer: AVAudioPCMBuffer?
    private var outputBuffer: AVAudioPCMBuffer?
    private var delayMemory: UnsafeMutableBufferPointer<Float>?
    private var observer: UUID?
    private var selectedPreset: AUAudioUnitPreset?

    // On the main thread: what the host was last told.
    private var reportedParameters: [PluginParameter: AUValue] = [:]
    private var reportedLatency = 0.0
    private var reportedTail = 0.0
    private var reportedPresetID: UUID?

    private static let stateKey = "tapline"
    private let log = Logger(subsystem: "io.github.belugaming.Tapline", category: "audio-unit")

    override init(componentDescription: AudioComponentDescription, options: AudioComponentInstantiationOptions = []) throws {
        buffers = .allocate(capacity: 1)
        buffers.initialize(to: RenderBuffers())
        try super.init(componentDescription: componentDescription, options: options)

        let format = AVAudioFormat(standardFormatWithSampleRate: 48_000, channels: 2)!
        inputBus = try AUAudioUnitBus(format: format)
        inputBus.maximumChannelCount = 2
        outputBus = try AUAudioUnitBus(format: format)
        outputBus.maximumChannelCount = 2
        inputBusArray = AUAudioUnitBusArray(audioUnit: self, busType: .input, busses: [inputBus])
        outputBusArray = AUAudioUnitBusArray(audioUnit: self, busType: .output, busses: [outputBus])
        maximumFramesToRender = 4096

        parameterTree = makeParameterTree()
        let state = engine.state
        for parameter in PluginParameter.allCases { reportedParameters[parameter] = state.value(of: parameter) }
        reportedPresetID = state.preset.id
        reportedLatency = engine.snapshot.latencySeconds
        observer = engine.observe { [weak self] snapshot, origin in
            self?.engineChanged(snapshot, origin: origin)
        }
        log.info("created; \(self.engine.userPresets().count) presets from Tapline")
    }

    deinit {
        if let observer { engine.removeObserver(observer) }
        delayMemory?.deallocate()
        buffers.deinitialize(count: 1)
        buffers.deallocate()
    }

    // MARK: - Busses and formats

    override var inputBusses: AUAudioUnitBusArray { inputBusArray }
    override var outputBusses: AUAudioUnitBusArray { outputBusArray }

    /// Stereo in and out, or mono in and stereo out.
    override var channelCapabilities: [NSNumber]? { [1, 2, 2, 2] }

    /// It copies the input to the output and processes that: a mono input has one buffer for two.
    override var canProcessInPlace: Bool { false }

    override func shouldChange(to format: AVAudioFormat, for bus: AUAudioUnitBus) -> Bool {
        guard format.commonFormat == .pcmFormatFloat32, !format.isInterleaved else { return false }
        let channels = format.channelCount
        guard bus === outputBus ? channels == 2 : (channels == 1 || channels == 2) else { return false }
        return super.shouldChange(to: format, for: bus)
    }

    // MARK: - Latency and tail

    override var latency: TimeInterval { engine.snapshot.latencySeconds }
    override var tailTime: TimeInterval { engine.snapshot.tail }

    override var shouldBypassEffect: Bool {
        get { engine.inbox.bypassed.load(ordering: .relaxed) }
        set { engine.inbox.bypassed.store(newValue, ordering: .relaxed) }
    }

    // MARK: - Render resources

    override func allocateRenderResources() throws {
        try super.allocateRenderResources()
        let input = inputBus.format, output = outputBus.format
        let frames = maximumFramesToRender
        guard input.sampleRate == output.sampleRate,
              let inputBuffer = AVAudioPCMBuffer(pcmFormat: input, frameCapacity: frames),
              let outputBuffer = AVAudioPCMBuffer(pcmFormat: output, frameCapacity: frames),
              let inputChannels = inputBuffer.floatChannelData, let outputChannels = outputBuffer.floatChannelData else {
            super.deallocateRenderResources()
            throw NSError(domain: NSOSStatusErrorDomain, code: Int(kAudioUnitErr_FormatNotSupported))
        }
        self.inputBuffer = inputBuffer
        self.outputBuffer = outputBuffer
        // The longest latency at this rate: high precision, Virtual Speakers and the look-ahead.
        let rate = output.sampleRate
        let longest = LinearPhaseEQ.latency(sampleRate: rate, phase: .linearHighPrecision) + DSPKernel.virtualSpeakerLatency + Int(0.0015 * rate) + 2
        var size = 1
        while size < longest + 1 { size <<= 1 }
        delayMemory?.deallocate()
        let delay = UnsafeMutableBufferPointer<Float>.allocate(capacity: 2 * size)
        delay.initialize(repeating: 0)
        delayMemory = delay
        var b = RenderBuffers()
        b.input = inputBuffer.mutableAudioBufferList
        b.inputData = (inputChannels[0], input.channelCount > 1 ? inputChannels[1] : nil)
        b.outputData = (outputChannels[0], outputChannels[1])
        b.maxFrames = frames
        b.delay = delay.baseAddress
        b.delayMask = size - 1
        buffers.pointee = b
        engine.start(sampleRate: output.sampleRate)
    }

    override func deallocateRenderResources() {
        engine.stop()
        buffers.pointee = RenderBuffers()
        inputBuffer = nil
        outputBuffer = nil
        delayMemory?.deallocate()
        delayMemory = nil
        super.deallocateRenderResources()
    }

    // MARK: - Rendering

    /// Pulls the input into the output buffers and runs the kernel on them in place. No locks,
    /// no allocation: it captures plain pointers and the lock-free inbox.
    override var internalRenderBlock: AUInternalRenderBlock {
        let buffers = Unchecked(pointer: self.buffers)
        let kernel = Unchecked(pointer: engine.kernel.pointer)
        let inbox = engine.inbox
        return { [unowned(unsafe) inbox] _, timestamp, frameCount, _, outputData, events, pullInputBlock in
            let b = buffers.pointer.pointee
            guard let input = b.input, let inputLeft = b.inputData.0 else { return kAudioUnitErr_Uninitialized }
            guard frameCount <= b.maxFrames else { return kAudioUnitErr_TooManyFramesToProcess }
            guard let pullInputBlock else { return kAudioUnitErr_NoConnection }

            // Pull into our own memory; the host may point the buffers elsewhere.
            let bytes = frameCount * UInt32(MemoryLayout<Float>.size)
            let inputs = UnsafeMutableAudioBufferListPointer(input)
            for i in 0..<inputs.count {
                inputs[i].mData = UnsafeMutableRawPointer(i == 0 ? inputLeft : (b.inputData.1 ?? inputLeft))
                inputs[i].mDataByteSize = bytes
            }
            var pullFlags = AudioUnitRenderActionFlags()
            let status = pullInputBlock(&pullFlags, timestamp, frameCount, 0, input)
            guard status == noErr else { return status }

            // Automation: the engine applies it within a few milliseconds.
            var event = events
            while let e = event {
                let type = e.pointee.head.eventType
                if type == .parameter || type == .parameterRamp {
                    inbox.post(address: e.pointee.parameter.parameterAddress, value: e.pointee.parameter.value)
                }
                event = UnsafePointer(e.pointee.head.next)
            }

            // The host's buffers, or ours when it passes none.
            let outputs = UnsafeMutableAudioBufferListPointer(outputData)
            guard outputs.count == 2, inputs.count >= 1 else { return kAudioUnitErr_FormatNotSupported }
            if outputs[0].mData == nil { outputs[0].mData = UnsafeMutableRawPointer(b.outputData.0) }
            if outputs[1].mData == nil { outputs[1].mData = UnsafeMutableRawPointer(b.outputData.1) }
            outputs[0].mDataByteSize = bytes
            outputs[1].mDataByteSize = bytes
            guard let left = outputs[0].mData?.assumingMemoryBound(to: Float.self),
                  let right = outputs[1].mData?.assumingMemoryBound(to: Float.self),
                  let inLeft = inputs[0].mData?.assumingMemoryBound(to: Float.self) else { return kAudioUnitErr_InvalidParameter }
            let inRight = inputs.count > 1 ? inputs[1].mData?.assumingMemoryBound(to: Float.self) ?? inLeft : inLeft
            let count = Int(frameCount)
            if left != inLeft { left.update(from: inLeft, count: count) }
            if right != inRight { right.update(from: inRight, count: count) }

            // Record the input; while the host bypasses the effect, play it as late as the effect would.
            let bypassed = inbox.bypassed.load(ordering: .relaxed)
            if let delay = b.delay {
                let mask = b.delayMask, size = mask + 1
                let lag = min(inbox.latency.load(ordering: .relaxed), mask)
                var position = b.delayPosition
                for i in 0..<count {
                    delay[position] = left[i]
                    delay[size + position] = right[i]
                    if bypassed {
                        let read = (position - lag) & mask
                        left[i] = delay[read]
                        right[i] = delay[size + read]
                    }
                    position = (position + 1) & mask
                }
                buffers.pointer.pointee.delayPosition = position
            }
            if bypassed { return noErr }
            var channels: (UnsafeMutablePointer<Float>?, UnsafeMutablePointer<Float>?) = (left, right)
            withUnsafePointer(to: &channels) { pair in
                pair.withMemoryRebound(to: UnsafeMutablePointer<Float>?.self, capacity: 2) {
                    tl_kernel_process(kernel.pointer, $0, 2, frameCount)
                }
            }
            return noErr
        }
    }

    // MARK: - Parameters

    private func makeParameterTree() -> AUParameterTree {
        let flags: AudioUnitParameterOptions = [.flag_IsReadable, .flag_IsWritable]
        let state = engine.state
        func parameter(_ p: PluginParameter, _ name: String, unit: AudioUnitParameterUnit, values: [String]? = nil) -> AUParameter {
            let parameter = AUParameterTree.createParameter(withIdentifier: p.identifier, name: name, address: p.rawValue,
                                                            min: p.range.lowerBound, max: p.range.upperBound, unit: unit, unitName: nil,
                                                            flags: flags, valueStrings: values, dependentParameters: nil)
            parameter.value = state.value(of: p)
            return parameter
        }
        let tree = AUParameterTree.createTree(withChildren: [
            parameter(.strength, String(localized: "Strength"), unit: .percent),
            parameter(.eq, String(localized: "EQ"), unit: .boolean),
            parameter(.virtualSpeakers, String(localized: "Virtual Speakers"), unit: .boolean),
            parameter(.listenAs, String(localized: "Listen as"), unit: .indexed,
                      values: [String(localized: "Off")] + SpeakerProfile.allCases.map { String(localized: $0.title) }
                        + [String(localized: "Headphones from Tapline")]),
            parameter(.filterPhase, String(localized: "Filter Phase"), unit: .indexed,
                      values: FilterPhase.allCases.map { String(localized: $0.title) }),
        ])
        let engine = self.engine
        tree.implementorValueObserver = { parameter, value in
            engine.setParameter(parameter.address, value)
        }
        tree.implementorValueProvider = { parameter in
            engine.parameterValue(parameter.address)
        }
        return tree
    }

    /// Sets a parameter for the view, through the host so that it can record automation.
    /// `gesture` marks the start (.touch) and end (.release) of a drag.
    func setParameter(_ parameter: PluginParameter, _ value: AUValue, gesture: AUParameterAutomationEventType = .value) {
        guard let p = parameterTree?.parameter(withAddress: parameter.rawValue) else {
            engine.setParameter(parameter.rawValue, value)
            return
        }
        p.setValue(value, originator: nil, atHostTime: 0, eventType: gesture)
    }

    /// Main thread: tells the host what changed other than through it.
    private func engineChanged(_ snapshot: EngineSnapshot, origin: AudioUnitEngine.Origin) {
        if let tree = parameterTree {
            for parameter in PluginParameter.allCases {
                let value = snapshot.state.value(of: parameter)
                guard reportedParameters[parameter] != value else { continue }
                reportedParameters[parameter] = value
                // The host set it itself; restored states and presets it learns about here.
                if origin != .host { tree.parameter(withAddress: parameter.rawValue)?.setValue(value, originator: nil) }
            }
        }
        if snapshot.latencySeconds != reportedLatency {
            willChangeValue(forKey: "latency")
            reportedLatency = snapshot.latencySeconds
            didChangeValue(forKey: "latency")
        }
        if snapshot.tail != reportedTail {
            willChangeValue(forKey: "tailTime")
            reportedTail = snapshot.tail
            didChangeValue(forKey: "tailTime")
        }
        if snapshot.state.preset.id != reportedPresetID {
            reportedPresetID = snapshot.state.preset.id
            if origin == .view {
                willChangeValue(forKey: "currentPreset")
                selectedPreset = factoryPreset(matching: snapshot.state.preset)
                didChangeValue(forKey: "currentPreset")
            }
        }
    }

    // MARK: - State and presets

    /// The host's project keeps the whole state, the curve included.
    override var fullState: [String: Any]? {
        get {
            var state = super.fullState ?? [:]
            state[Self.stateKey] = engine.state.encoded()
            return state
        }
        set {
            super.fullState = newValue
            guard let data = newValue?[Self.stateKey] as? Data else { return }
            if let state = AudioUnitState(encoded: data) {
                engine.restore(state)
            } else {
                log.error("could not read the saved state (\(data.count) bytes)")
            }
        }
    }

    /// The host's preset menu: Tapline's built-in presets, then the user's from the app.
    override var factoryPresets: [AUAudioUnitPreset]? {
        engine.presets().enumerated().map { index, preset in
            let p = AUAudioUnitPreset()
            p.number = index
            p.name = preset.name
            return p
        }
    }

    override var supportsUserPresets: Bool { true }

    override var currentPreset: AUAudioUnitPreset? {
        get { selectedPreset }
        set {
            guard let preset = newValue else {
                selectedPreset = nil
                return
            }
            if preset.number >= 0 {
                // By number, or by name when the user's presets changed since the host listed them.
                let presets = engine.presets()
                let chosen = presets.indices.contains(preset.number) && presets[preset.number].name == preset.name
                    ? presets[preset.number] : presets.first { $0.name == preset.name }
                guard let chosen else { return }
                engine.update(from: .restore) { $0.preset = chosen }
                selectedPreset = preset
            } else if userPresets.contains(where: { $0.number == preset.number }) {
                // A preset the host saved through the system: its state is the full state.
                if let state = try? presetState(for: preset) { fullState = state }
                selectedPreset = preset
            } else {
                // Only a name to show (a host's own preset, "Untitled"…): the state stays.
                selectedPreset = preset
            }
        }
    }

    private func factoryPreset(matching preset: EQPreset) -> AUAudioUnitPreset? {
        guard let index = engine.presets().firstIndex(where: { $0.id == preset.id && $0.hasSameCurve(as: preset) }) else { return nil }
        let p = AUAudioUnitPreset()
        p.number = index
        p.name = preset.name
        return p
    }
}

extension SpeakerProfile {
    var title: LocalizedStringResource {
        switch self {
        case .phone: "Phone speaker"
        case .laptop: "Laptop speakers"
        case .tv: "TV speakers"
        case .bluetoothSpeaker: "Small Bluetooth speaker"
        case .car: "Car"
        }
    }
}

extension FilterPhase {
    var title: LocalizedStringResource {
        switch self {
        case .minimum: "Minimum phase"
        case .linearLowLatency: "Linear phase · low latency"
        case .linearHighPrecision: "Linear phase · high precision"
        }
    }
}
