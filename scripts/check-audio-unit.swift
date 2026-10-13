// Checks the registered Tapline Audio Unit the way a host uses it: it loads out of process, lists
// Tapline's presets (the built-in ones, then the user's, read from the app's preferences through
// the extension's sandbox exception), keeps its whole state in a project, loads Virtual Speakers,
// and delays the sound exactly by the latency it reports, also while the host bypasses it (a
// synthetic impulse rendered offline; no audio device is used). auval checks the rest.
//
//   swift scripts/check-audio-unit.swift [--expect-preset NAME]
//
// --expect-preset fails unless the presets include NAME (CI saves one in the app's preferences).
// It never prints the user's preset names.
import AppKit
import AVFoundation

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data("error: \(message)\n".utf8))
    exit(1)
}

func code(_ s: String) -> FourCharCode { s.utf8.reduce(0) { $0 << 8 | FourCharCode($1) } }

let description = AudioComponentDescription(componentType: kAudioUnitType_Effect, componentSubType: code("Tpln"),
                                            componentManufacturer: code("Tpln"), componentFlags: 0, componentFlagsMask: 0)
let arguments = Array(CommandLine.arguments.dropFirst())
let expectedPreset = arguments.firstIndex(of: "--expect-preset").flatMap { arguments.indices.contains($0 + 1) ? arguments[$0 + 1] : nil }

@MainActor func instantiate() async -> AUAudioUnit {
    do {
        return try await AUAudioUnit.instantiate(with: description, options: .loadOutOfProcess)
    } catch {
        fail("could not load aufx Tpln Tpln: \(error)")
    }
}

@MainActor func parameter(_ unit: AUAudioUnit, _ identifier: String) -> AUParameter {
    guard let p = unit.parameterTree?.allParameters.first(where: { $0.identifier == identifier }) else { fail("no parameter \(identifier)") }
    return p
}

/// Waits for the extension, which applies changes on its own queue.
func settle() async { try? await Task.sleep(for: .milliseconds(600)) }

/// Renders a synthetic impulse through a fresh instance offline (no audio device) and returns
/// the reported latency in samples, the first sample that is not silent and the loudest one,
/// counted from the impulse.
@MainActor func impulse(virtualSpeakers: Bool, bypassed: Bool) async -> (latency: Int, first: Int, peak: Int) {
    let rate = 48_000.0, at = 4800, length = 9600
    let unit: AVAudioUnit
    do {
        unit = try await AVAudioUnit.instantiate(with: description, options: .loadOutOfProcess)
    } catch {
        fail("could not load aufx Tpln Tpln: \(error)")
    }
    parameter(unit.auAudioUnit, "virtualSpeakers").value = virtualSpeakers ? 1 : 0
    await settle()
    unit.auAudioUnit.shouldBypassEffect = bypassed
    let engine = AVAudioEngine()
    let format = AVAudioFormat(standardFormatWithSampleRate: rate, channels: 2)!
    var position = 0
    let source = AVAudioSourceNode(format: format) { _, _, frames, list in
        for buffer in UnsafeMutableAudioBufferListPointer(list) {
            let samples = buffer.mData!.assumingMemoryBound(to: Float.self)
            for i in 0..<Int(frames) { samples[i] = position + i == at ? 0.5 : 0 }
        }
        position += Int(frames)
        return noErr
    }
    var output: [Float] = []
    do {
        try engine.enableManualRenderingMode(.offline, format: format, maximumFrameCount: 512)
        engine.attach(source)
        engine.attach(unit)
        engine.connect(source, to: unit, format: format)
        engine.connect(unit, to: engine.mainMixerNode, format: format)
        try engine.start()
        let buffer = AVAudioPCMBuffer(pcmFormat: engine.manualRenderingFormat, frameCapacity: 512)!
        while output.count < length {
            _ = try engine.renderOffline(512, to: buffer)
            output += UnsafeBufferPointer(start: buffer.floatChannelData![0], count: Int(buffer.frameLength))
        }
        engine.stop()
    } catch {
        fail("offline rendering failed: \(error)")
    }
    let latency = Int((unit.auAudioUnit.latency * rate).rounded())
    let first = (output.firstIndex { abs($0) > 1e-4 } ?? length) - at
    let peak = (output.indices.max { abs(output[$0]) < abs(output[$1]) } ?? 0) - at
    return (latency, first, peak)
}

@MainActor func check() async {
    let unit = await instantiate()
    print("Loaded \(unit.audioUnitName ?? "?") by \(unit.manufacturerName ?? "?"), out of process")

    // Presets: ten built in, then the user's from Tapline.
    guard let presets = unit.factoryPresets, presets.count >= 10 else { fail("expected at least the 10 built-in presets") }
    print("Presets: \(presets.count) (10 built in, \(presets.count - 10) from Tapline's preferences)")
    if let expectedPreset {
        guard presets.dropFirst(10).contains(where: { $0.name == expectedPreset }) else {
            fail("the presets saved in Tapline's preferences do not include \"\(expectedPreset)\": the extension cannot read them")
        }
        print("Found the preset saved in Tapline's preferences")
    }

    // Latency: the limiter's look-ahead, then linear phase (low latency, 176 ms at 48 kHz) and
    // Virtual Speakers (256 samples) on top.
    let base = unit.latency
    parameter(unit, "filterPhase").value = 1
    parameter(unit, "virtualSpeakers").value = 1
    await settle()
    let linear = unit.latency
    print(String(format: "Latency: %.2f ms; with linear phase (low latency) and Virtual Speakers %.2f ms", base * 1000, linear * 1000))
    guard base > 0, base < 0.005 else { fail("latency \(base) s without linear phase") }
    guard linear > 0.17, linear < 0.2 else { fail("latency \(linear) s with linear phase") }

    // Render resources (no audio is processed): Virtual Speakers load their room, which shows
    // in the tail, the room's length on top of the FIR's second half.
    do {
        try unit.allocateRenderResources()
    } catch {
        fail("could not allocate render resources: \(error)")
    }
    await settle()
    print(String(format: "Tail: %.2f s", unit.tailTime))
    guard unit.tailTime > 0.25 else { fail("tail \(unit.tailTime) s: the Virtual Speakers' impulse responses did not load") }
    unit.deallocateRenderResources()

    // State: a preset, Strength and Listen as, saved and restored into another instance.
    unit.currentPreset = presets[1]
    parameter(unit, "strength").value = 40
    parameter(unit, "listenAs").value = 5
    await settle()
    guard let state = unit.fullState, let saved = state["tapline"] as? Data else { fail("the full state has no Tapline state") }
    let other = await instantiate()
    other.fullState = state
    await settle()
    for (identifier, value) in [("strength", 40), ("filterPhase", 1), ("virtualSpeakers", 1), ("listenAs", 5)] as [(String, AUValue)] {
        let restored = parameter(other, identifier).value
        guard restored == value else { fail("\(identifier) restored as \(restored), saved as \(value)") }
    }
    guard let restored = other.fullState?["tapline"] as? Data,
          let a = try? JSONSerialization.jsonObject(with: saved) as? NSDictionary,
          let b = try? JSONSerialization.jsonObject(with: restored) as? NSDictionary, a == b else {
        fail("the restored state differs from the saved one")
    }
    guard let preset = a["preset"] as? NSDictionary, preset["name"] as? String == presets[1].name else { fail("the saved state lost the preset") }
    guard other.latency > 0.17 else { fail("the restored instance reports \(other.latency) s of latency") }
    print("State: saved and restored (\(saved.count) bytes)")

    // The sound comes out exactly as late as reported, also while the host bypasses the effect
    // (a synthetic impulse, rendered offline).
    for (virtualSpeakers, bypassed) in [(false, false), (false, true), (true, false), (true, true)] {
        let r = await impulse(virtualSpeakers: virtualSpeakers, bypassed: bypassed)
        let what = "\(virtualSpeakers ? "Virtual Speakers" : "EQ")\(bypassed ? ", bypassed by the host" : "")"
        print("Impulse (\(what)): reported \(r.latency) samples, first sound after \(r.first), loudest after \(r.peak)")
        guard r.first >= r.latency else { fail("\(what): sound \(r.latency - r.first) samples earlier than the reported latency") }
        if !virtualSpeakers || bypassed {
            guard r.peak == r.latency else { fail("\(what): the impulse came out \(r.peak) samples late, \(r.latency) reported") }
        }
    }
    print("OK")
}

// The extension's remote view machinery expects AppKit's main thread.
setvbuf(stdout, nil, _IOLBF, 0)
NSApplication.shared.setActivationPolicy(.prohibited)
Task { @MainActor in
    await check()
    exit(0)
}
NSApplication.shared.run()
