import CoreAudio
import Foundation
import Testing
@testable import TaplineCore
import TaplineDSP

/// The Audio Unit's entry point into the kernel: `tl_kernel_process` on the host's buffers.
@Suite struct AudioUnitKernelTests {
    static let rate = 48_000.0

    /// Runs tl_kernel_process in place over a stereo signal, in host buffers of the given sizes.
    static func process(_ kernel: DSPKernel, _ left: [Float], _ right: [Float], cycles: [Int] = [512]) -> (left: [Float], right: [Float]) {
        var l = left, r = right
        var start = 0, c = 0
        while start < l.count {
            let n = min(cycles[c % cycles.count], l.count - start)
            c += 1
            l.withUnsafeMutableBufferPointer { lp in
                r.withUnsafeMutableBufferPointer { rp in
                    let channels: [UnsafeMutablePointer<Float>?] = [lp.baseAddress! + start, rp.baseAddress! + start]
                    channels.withUnsafeBufferPointer { tl_kernel_process(kernel.pointer, $0.baseAddress!, 2, UInt32(n)) }
                }
            }
            start += n
        }
        return (l, r)
    }

    static func noise(_ count: Int, seed: UInt64) -> [Float] {
        var state = seed
        return (0..<count).map { _ in
            state = state &* 6364136223846793005 &+ 1442695040888963407
            return Float(Int64(bitPattern: state >> 11) & 0xFFFFF) / Float(0xFFFFF) * 0.8 - 0.4
        }
    }

    /// A pair of speakers whose responses are short random filters, so the convolution shows.
    static var speakers: BinauralIRs {
        BinauralIRs(sampleRate: rate, responses: (0..<4).map { i in noise(700, seed: UInt64(i + 7)).map { $0 * 0.4 } })
    }

    enum Chain: String, CaseIterable {
        case eqAndListenAs, bypassed, virtualSpeakers, linearPhase
    }

    static func settings(_ chain: Chain) -> (DSPSettings, DeviceSimulation?) {
        var s = DSPSettings(bands: [
            EQBand(type: .lowShelf, frequency: 105, gain: 5, q: 0.7),
            EQBand(frequency: 2500, gain: -4, q: 2, channels: .right),
            EQBand(type: .highShelf, frequency: 9000, gain: 3, q: 0.7),
        ])
        s.effects.outputGainDB = 4   // the limiter works too
        switch chain {
        case .eqAndListenAs: return (s, SpeakerProfile.laptop.simulation)
        case .bypassed: s.eqEnabled = false
        case .virtualSpeakers: s.effects.virtualSpeakersEnabled = true
        case .linearPhase: s.filterPhase = .linearLowLatency
        }
        return (s, nil)
    }

    static func makeKernel(_ chain: Chain, layout: KernelLayout) -> DSPKernel {
        let (s, simulation) = settings(chain)
        let kernel = DSPKernel()
        kernel.configure(sampleRate: rate, layout: layout)
        kernel.setSettings(s, sampleRate: rate, simulation: simulation, simulating: simulation != nil)
        if s.effects.virtualSpeakersEnabled { kernel.setVirtualSpeakers(speakers, gain: 1, key: "test") }
        if s.filterPhase.isLinear { kernel.setLinearPhase(settings: s) }
        return kernel
    }

    /// The same chain on the same input plays the same samples, whether the IOProc mixes it from
    /// a tap or a host hands over its buffers.
    @Test(arguments: Chain.allCases)
    func hostBuffersPlayLikeTheIOProc(chain: Chain) {
        let count = chain == .linearPhase ? 30_000 : 12_000
        let left = Self.noise(count, seed: 1), right = Self.noise(count, seed: 2)
        let cycles = [512, 333, 64, 1024]
        let viaIOProc = VirtualSpeakerKernelTests.process(
            Self.makeKernel(chain, layout: KernelLayout(sourceBufferIndices: [0], leftBuffer: 0, leftChannel: 0, rightBuffer: 0, rightChannel: 1)),
            left, right, cycles: cycles)
        let viaHost = Self.process(
            Self.makeKernel(chain, layout: KernelLayout(sourceBufferIndices: [], leftBuffer: -1, leftChannel: 0, rightBuffer: -1, rightChannel: 1)),
            left, right, cycles: cycles)
        #expect(viaHost.left == viaIOProc.left)
        #expect(viaHost.right == viaIOProc.right)
        // And the chain did something.
        #expect(zip(viaHost.left, left).contains { abs($0 - $1) > 0.01 })
    }

    /// More frames than the kernel's buffers hold in one call: processed in pieces, seamlessly.
    @Test func longBuffersPlayLikeShortOnes() {
        let count = Int(TL_MAX_FRAMES) * 2 + 1000
        let left = Self.noise(count, seed: 3), right = Self.noise(count, seed: 4)
        let layout = KernelLayout(sourceBufferIndices: [], leftBuffer: -1, leftChannel: 0, rightBuffer: -1, rightChannel: 1)
        let once = Self.process(Self.makeKernel(.eqAndListenAs, layout: layout), left, right, cycles: [count])
        let pieces = Self.process(Self.makeKernel(.eqAndListenAs, layout: layout), left, right, cycles: [Int(TL_MAX_FRAMES), Int(TL_MAX_FRAMES), 1000])
        #expect(once.left == pieces.left)
        #expect(once.right == pieces.right)
    }

    /// One channel plays as both sides and gets their average back.
    @Test func monoPlaysAsBothSides() {
        let layout = KernelLayout(sourceBufferIndices: [], leftBuffer: -1, leftChannel: 0, rightBuffer: -1, rightChannel: 1)
        let x = Self.noise(6000, seed: 5)
        let stereo = Self.process(Self.makeKernel(.eqAndListenAs, layout: layout), x, x)
        let kernel = Self.makeKernel(.eqAndListenAs, layout: layout)
        var mono = x
        var start = 0
        while start < mono.count {
            let n = min(512, mono.count - start)
            mono.withUnsafeMutableBufferPointer { p in
                let channels: [UnsafeMutablePointer<Float>?] = [p.baseAddress! + start]
                channels.withUnsafeBufferPointer { tl_kernel_process(kernel.pointer, $0.baseAddress!, 1, UInt32(n)) }
            }
            start += n
        }
        let worst = zip(mono, zip(stereo.left, stereo.right)).map { abs($0 - 0.5 * ($1.0 + $1.1)) }.max() ?? 1
        #expect(worst < 1e-6, "largest difference \(worst)")
    }

    /// What the kernel reports as its latency is where an impulse comes out: the limiter's
    /// look-ahead, plus Virtual Speakers' partition, plus the linear-phase EQ's delay.
    @Test(arguments: [(false, FilterPhase.minimum), (true, .minimum), (false, .linearLowLatency), (true, .linearHighPrecision)])
    func reportedLatencyIsWhereAnImpulseComesOut(virtualSpeakers: Bool, phase: FilterPhase) {
        var s = DSPSettings(bands: [])   // flat: the FIR is a delayed impulse
        s.effects.limiterEnabled = false
        s.effects.virtualSpeakersEnabled = virtualSpeakers
        s.filterPhase = phase
        let kernel = DSPKernel()
        kernel.configure(sampleRate: Self.rate, layout: KernelLayout(sourceBufferIndices: [], leftBuffer: -1, leftChannel: 0, rightBuffer: -1, rightChannel: 1))
        kernel.setSettings(s, sampleRate: Self.rate)
        if virtualSpeakers {
            // Each speaker straight into its own ear.
            var one = [Float](repeating: 0, count: 64)
            one[0] = 1
            let zero = [Float](repeating: 0, count: 64)
            kernel.setVirtualSpeakers(BinauralIRs(sampleRate: Self.rate, responses: [one, zero, zero, one]), gain: 1, key: "unit")
        }
        if phase.isLinear { kernel.setLinearPhase(settings: s) }
        let latency = kernel.latency(virtualSpeakers: virtualSpeakers, phase: phase)
        let look = VirtualSpeakerKernelTests.lookahead(Self.rate)
        let expected = look + (virtualSpeakers ? DSPKernel.virtualSpeakerLatency : 0) + LinearPhaseEQ.latency(sampleRate: Self.rate, phase: phase)
        #expect(latency == expected)

        // Once everything faded in (the FIR's delay line filled, then 30 ms), an impulse.
        let at = latency + Int(0.2 * Self.rate)
        var x = [Float](repeating: 0, count: at + latency + 2000)
        x[at] = 0.5
        let out = Self.process(kernel, x, x, cycles: [512])
        let peak = out.left.indices.max { abs(out.left[$0]) < abs(out.left[$1]) }
        #expect(peak == at + latency)
        #expect(abs(out.left[at + latency] - 0.5) < 0.01)
    }
}

@Suite struct AudioUnitStateTests {
    static let correction = EQPreset(name: "HD 600 (AutoEQ)", bands: [
        EQBand(type: .lowShelf, frequency: 105, gain: 6, q: 0.7),
        EQBand(frequency: 3000, gain: -4, q: 2),
        EQBand(type: .highPass, frequency: 20, q: 0.7),
    ], preampDB: -6.2, autoPreamp: false, tone: ToneSettings(air: 2), source: "AutoEQ")

    static var full: AudioUnitState {
        AudioUnitState(preset: correction, strength: 0.6, eqEnabled: false, levelMatchedBypass: false, filterPhase: .linearHighPrecision,
                       virtualSpeakersEnabled: true, virtualSpeakers: "sadie-ku100", listenAs: SpeakerProfile.car.simulation, listening: true)
    }

    @Test func roundTripsThroughTheSavedState() throws {
        let state = Self.full
        let restored = try #require(AudioUnitState(encoded: state.encoded()))
        #expect(restored == state)
        #expect(AudioUnitState(encoded: AudioUnitState().encoded()) == AudioUnitState())
    }

    @Test func savesItsFormatVersion() throws {
        let object = try #require(try JSONSerialization.jsonObject(with: Self.full.encoded()) as? [String: Any])
        #expect(object["version"] as? Int == AudioUnitState.formatVersion)
    }

    /// A state from a newer version keeps what this version reads; a value it does not know
    /// (here a new filter phase) keeps its default.
    @Test func readsNewerStatesAsFarAsItCan() throws {
        var object = try #require(try JSONSerialization.jsonObject(with: Self.full.encoded()) as? [String: Any])
        object["version"] = 7
        object["filterPhase"] = "linearUltra"
        object["newSetting"] = ["anything": 1]
        let restored = try #require(AudioUnitState(encoded: try JSONSerialization.data(withJSONObject: object)))
        #expect(restored.filterPhase == .minimum)
        #expect(restored.preset == Self.correction)
        #expect(restored.strength == 0.6)
        #expect(restored.listenAs?.profile == .car && restored.listening)
    }

    @Test func missingValuesAreDefaults() throws {
        let restored = try #require(AudioUnitState(encoded: Data("{}".utf8)))
        #expect(restored == AudioUnitState())
        #expect(restored.preset.id == BuiltInPresets.all[0].id)
        #expect(AudioUnitState(encoded: Data("not json".utf8)) == nil)
        #expect(AudioUnitState(encoded: Data()) == nil)
    }

    @Test func strengthScalesTheCurve() {
        var state = AudioUnitState(preset: Self.correction)
        state.strength = 1
        let full = state.settings
        #expect(full.bands.map(\.gain) == [6, -4, 0])
        #expect(full.preampDB == -6.2 && !full.autoPreamp)
        #expect(full.tone.air == 2)

        state.strength = 0.5
        let half = state.settings
        #expect(half.bands.map(\.gain) == [3, -2, 0])
        #expect(half.bands[2].type == .highPass)   // filters without a gain stay
        #expect(abs(half.preampDB + 3.1) < 1e-9)
        #expect(half.tone.air == 1)

        // None of it at 0: flat, at unity gain (only the 20 Hz high-pass stays).
        state.strength = 0
        let none = state.settings
        let response = ResponseCurve.responseDB(bands: none.activeBands, frequencies: [200, 1000, 3000, 10_000], sampleRate: 48_000)
        #expect(response.allSatisfy { abs($0) < 0.01 }, "\(response)")
        #expect(state.kernelParams(sampleRate: 48_000).preamp == 1)
    }

    @Test func kernelParamsCarryTheSettings() {
        var p = Self.full.kernelParams(sampleRate: 48_000)
        #expect(p.eq_enabled == 0)
        #expect(p.bypass_gain == 1)   // not level-matched
        #expect(p.virtual_speakers_enabled == 1)
        #expect(p.linear_phase == TL_LIN_HIGH_PRECISION)
        #expect(p.simulation.enabled == 1)
        #expect(p.limiter_enabled == 1)

        var state = Self.full
        state.listening = false
        state.levelMatchedBypass = true
        p = state.kernelParams(sampleRate: 48_000)
        // The device stays in place so that switching it off fades.
        #expect(p.simulation.enabled == 0)
        #expect(p.bypass_gain < 1)
    }

    @Test func parametersReadAndWriteTheState() {
        var state = AudioUnitState()
        #expect(state.value(of: .strength) == 100)
        #expect(state.value(of: .eq) == 1)
        #expect(state.value(of: .virtualSpeakers) == 0)
        #expect(state.value(of: .listenAs) == 0)
        #expect(state.value(of: .filterPhase) == 0)

        state.set(.strength, to: 35)
        #expect(abs(state.strength - 0.35) < 1e-6)
        state.set(.strength, to: 500)
        #expect(state.strength == 1)
        state.set(.strength, to: .nan)
        #expect(state.strength == 1)
        state.set(.eq, to: 0)
        #expect(!state.eqEnabled)
        state.set(.virtualSpeakers, to: 1)
        #expect(state.virtualSpeakersEnabled)
        state.set(.filterPhase, to: 2)
        #expect(state.filterPhase == .linearHighPrecision)
        #expect(state.value(of: .filterPhase) == 2)

        for (i, profile) in SpeakerProfile.allCases.enumerated() {
            state.set(.listenAs, to: Float(i + 1))
            #expect(state.playingDevice?.profile == profile)
            #expect(state.value(of: .listenAs) == Float(i + 1))
        }
        state.set(.listenAs, to: 0)
        #expect(state.playingDevice == nil)
        #expect(state.listenAs?.profile == .car)   // kept, so switching off fades
        #expect(state.value(of: .listenAs) == 0)
    }

    @Test func listenAsPlaysTheHeadphonesFromTheApp() {
        let airpods = DeviceSimulation(id: "airpods", name: "AirPods Pro", kind: .measured,
                                       bands: [EQBand(frequency: 3000, gain: 4, q: 2)], source: "Crinacle")
        var state = AudioUnitState()
        // Without headphones from the app there is nothing to play.
        state.set(.listenAs, to: PluginParameter.listenAsHeadphones)
        #expect(state.playingDevice == nil)
        state.set(.listenAs, to: PluginParameter.listenAsHeadphones, headphones: airpods)
        #expect(state.playingDevice == airpods)
        #expect(state.value(of: .listenAs) == PluginParameter.listenAsHeadphones)
        // The state keeps them: the project plays them on a Mac without the app's choice too.
        state.set(.listenAs, to: 0)
        state.set(.listenAs, to: PluginParameter.listenAsHeadphones)
        #expect(state.playingDevice == airpods)
    }
}

@Suite struct SharedPresetsTests {
    static func withDefaults(_ body: (UserDefaults) throws -> Void) rethrows {
        let suite = "io.github.belugaming.Tapline.tests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        try body(defaults)
    }

    /// The app saves its presets through TolerantJSON; the Audio Unit reads exactly that.
    @Test func readsThePresetsAsTheAppSavesThem() throws {
        try Self.withDefaults { defaults in
            #expect(SharedPresets.userPresets(in: defaults).isEmpty)
            let presets = [AudioUnitStateTests.correction, EQPreset(name: "Mine", bands: [EQBand(frequency: 500, gain: 2, q: 1)])]
            let data: Data? = TolerantJSON().encode(presets)
            defaults.set(try #require(data), forKey: SharedPresets.userPresetsKey)
            #expect(SharedPresets.userPresets(in: defaults) == presets)
        }
    }

    @Test func skipsPresetsItCannotRead() throws {
        try Self.withDefaults { defaults in
            let good = AudioUnitStateTests.correction
            var array = try #require(try JSONSerialization.jsonObject(with: JSONEncoder().encode([good])) as? [Any])
            array.append(["name": "From a newer version", "bands": "unreadable"])
            defaults.set(try JSONSerialization.data(withJSONObject: array), forKey: SharedPresets.userPresetsKey)
            #expect(SharedPresets.userPresets(in: defaults) == [good])

            defaults.set(Data("garbage".utf8), forKey: SharedPresets.userPresetsKey)
            #expect(SharedPresets.userPresets(in: defaults).isEmpty)
        }
    }

    @Test func offersOnlyMeasuredHeadphonesFromListenAs() throws {
        try Self.withDefaults { defaults in
            #expect(SharedPresets.listenAsHeadphones(in: defaults) == nil)
            defaults.set(try JSONEncoder().encode(SpeakerProfile.phone.simulation), forKey: SharedPresets.listenAsKey)
            #expect(SharedPresets.listenAsHeadphones(in: defaults) == nil)
            let buds = DeviceSimulation(id: "buds", name: "Galaxy Buds", kind: .measured, bands: [EQBand(frequency: 100, gain: 3, q: 1)])
            defaults.set(try JSONEncoder().encode(buds), forKey: SharedPresets.listenAsKey)
            #expect(SharedPresets.listenAsHeadphones(in: defaults) == buds)
        }
    }

    @Test func builtInPresetsKeepTheirIDs() {
        #expect(BuiltInPresets.all.count == 10)
        #expect(BuiltInPresets.all.allSatisfy { $0.isBuiltIn })
        #expect(BuiltInPresets.all[0].id == UUID(uuidString: "00000000-0000-0000-0000-000000000001"))
        #expect(Set(BuiltInPresets.all.map(\.id)).count == BuiltInPresets.all.count)
    }
}
