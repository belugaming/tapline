import CoreAudio
import Foundation
import Testing
@testable import TaplineCore
import TaplineDSP

@Suite struct SpeakerProfileTests {
    let sr = 48_000.0

    func response(_ profile: SpeakerProfile, at f: Double) -> Double {
        ResponseCurve.responseDB(bands: profile.bands, frequencies: [f], sampleRate: sr)[0]
    }

    @Test func everyProfileFitsTheKernelsBlock() {
        for profile in SpeakerProfile.allCases {
            #expect(!profile.bands.isEmpty && profile.bands.count <= DeviceSimulation.maxStages, "\(profile)")
            let sim = profile.simulation
            #expect(sim.bands.count == profile.bands.count)
            #expect(sim.kind == .speaker && sim.profile == profile && sim.id == profile.rawValue)
            #expect(profile.bands.allSatisfy { $0.clamped() == $0 }, "\(profile) has a band out of range")
        }
    }

    @Test func smallSpeakersLoseTheBass() {
        #expect(response(.phone, at: 200) < response(.phone, at: 2000) - 20)
        #expect(response(.laptop, at: 80) < response(.laptop, at: 1000) - 20)
        #expect(response(.tv, at: 40) < response(.tv, at: 1000) - 15)
        #expect(response(.bluetoothSpeaker, at: 35) < response(.bluetoothSpeaker, at: 110) - 15)
        // A phone is the most limited of all, a TV reaches deeper than a laptop.
        #expect(response(.phone, at: 300) < response(.laptop, at: 300))
        #expect(response(.laptop, at: 120) < response(.tv, at: 120))
    }

    @Test func profilesHaveTheirTypicalResonances() {
        // Passive radiators: a bass bump above the cut-off.
        #expect(response(.bluetoothSpeaker, at: 110) > response(.bluetoothSpeaker, at: 1000) + 4)
        // Phones push the presence region.
        #expect(response(.phone, at: 3200) > response(.phone, at: 2000) + 1)
        // A car cabin lifts the deep bass; off-axis tweeters lose treble.
        #expect(response(.car, at: 40) > response(.car, at: 1000) + 3)
        #expect(response(.car, at: 12_000) < response(.car, at: 1000) - 2)
        // Down-firing TV speakers lose treble, and a phone's top end fades.
        #expect(response(.tv, at: 10_000) < response(.tv, at: 1000) - 3)
        #expect(response(.phone, at: 16_000) < response(.phone, at: 3000) - 5)
    }

    @Test func channelHandlingFollowsTheDevice() {
        #expect(SpeakerProfile.phone.width == 0)
        #expect(SpeakerProfile.bluetoothSpeaker.width == 0)
        #expect(SpeakerProfile.laptop.width > 0 && SpeakerProfile.laptop.width < SpeakerProfile.tv.width)
        #expect(SpeakerProfile.tv.width < 1 && SpeakerProfile.car.width < 1 && SpeakerProfile.car.width > 0)
        #expect(SpeakerProfile.allCases.allSatisfy { $0.simulation.width == $0.width })
    }
}

@Suite struct SimulationLevelTests {
    let sr = 48_000.0

    @Test func makeupGainCancelsTheLoudnessChange() {
        for profile in SpeakerProfile.allCases {
            let sim = profile.simulation
            let makeup = sim.makeupGainDB(sampleRate: sr)
            #expect(DeviceSimulation.gainRange.contains(makeup))
            #expect(abs(sim.loudnessChangeDB(sampleRate: sr) + makeup) < 1e-9, "\(profile): \(makeup)")
            // The same through the loudness model: the curve plus a flat make-up gain.
            let total = ResponseCurve.loudnessChangeDB(bands: sim.bands + [EQBand(type: .highShelf, frequency: 1, gain: makeup, q: 0.71)], sampleRate: sr)
            #expect(abs(total) < 0.05, "\(profile): \(total)")
        }
        // Earbuds that lose the bass come out quieter, so they are made up for. A phone loses
        // even more bass, but its forward presence keeps the K-weighted loudness close.
        let leaky = DeviceSimulation(id: "x", name: "x", kind: .measured, bands: [EQBand(type: .lowShelf, frequency: 200, gain: -10, q: 0.7)])
        #expect(leaky.makeupGainDB(sampleRate: sr) > 0.5)
        #expect(abs(SpeakerProfile.phone.simulation.makeupGainDB(sampleRate: sr)) < 3)
        #expect(DeviceSimulation(id: "x", name: "x", kind: .measured, bands: []).makeupGainDB() == 0)
    }

    @Test func makeupGainStaysInRange() {
        let loud = DeviceSimulation(id: "x", name: "x", kind: .measured, bands: [EQBand(type: .highShelf, frequency: 200, gain: 24, q: 0.71)])
        #expect(loud.makeupGainDB() == DeviceSimulation.gainRange.lowerBound)
        let broken = DeviceSimulation(id: "x", name: "x", kind: .measured, bands: [EQBand(frequency: .nan, gain: .nan, q: .nan)])
        #expect(broken.makeupGainDB().isFinite)
    }

    @Test func withoutALimiterTheMakeupKeepsTheHeadroom() {
        let sim = SpeakerProfile.phone.simulation
        let curve = ResponseCurve.logFrequencies(count: 480)
        let device = ResponseCurve.responseDB(bands: sim.bands, frequencies: curve, sampleRate: sr)

        // A flat EQ leaves no headroom: the simulation may not raise anything above 0 dB.
        var flat = DSPSettings.flat
        flat.effects.limiterEnabled = false
        let safe = sim.makeupGainDB(after: flat, sampleRate: sr)
        #expect(safe < sim.makeupGainDB(sampleRate: sr))
        #expect((device.max() ?? 0) + safe <= 1e-6)
        #expect(abs(sim.levelShortfallDB(after: flat, sampleRate: sr) - (sim.makeupGainDB(sampleRate: sr) - safe)) < 1e-9)

        // A correction's preamp leaves room in the midrange: the full make-up gain fits.
        var corrected = DSPSettings(bands: [EQBand(type: .lowShelf, frequency: 105, gain: 6, q: 0.7)])
        corrected.effects.limiterEnabled = false
        #expect(abs(sim.makeupGainDB(after: corrected, sampleRate: sr) - sim.makeupGainDB(sampleRate: sr)) < 1e-9)
        #expect(sim.levelShortfallDB(after: corrected, sampleRate: sr) == 0)

        // With the limiter on, the kernel gets the exact level match.
        flat.effects.limiterEnabled = true
        let p = flat.kernelParams(sampleRate: sr, simulation: sim, simulating: true)
        #expect(abs(p.simulation.gain - Float(dbToLinear(sim.makeupGainDB(sampleRate: sr)))) < 1e-6)
        flat.effects.limiterEnabled = false
        let q = flat.kernelParams(sampleRate: sr, simulation: sim, simulating: true)
        #expect(abs(q.simulation.gain - Float(dbToLinear(safe))) < 1e-6)
    }
}

@Suite struct SimulationStageBudgetTests {
    @Test func fullEQToneAndSimulationAllGetStages() {
        var s = DSPSettings()
        s.bands = (0..<DSPSettings.maxBands).map { EQBand(frequency: 30 * pow(1.3, Double($0)), gain: 1, q: 2) }
        s.tone = ToneSettings(bass: 2, warmth: 1, clarity: -1, air: 2)
        let sim = DeviceSimulation(id: "x", name: "x", kind: .measured,
                                   bands: (0..<DeviceSimulation.maxStages).map { EQBand(frequency: 40 * pow(1.4, Double($0)), gain: -1, q: 1) })
        let p = s.kernelParams(sampleRate: 48_000, simulation: sim, simulating: true)
        let eq = withUnsafeBytes(of: p.stages) { Array($0.bindMemory(to: tl_biquad.self)) }
        let block = withUnsafeBytes(of: p.simulation.stages) { Array($0.bindMemory(to: tl_biquad.self)) }
        #expect(eq.filter { $0.active == 1 }.count == DSPSettings.maxBands + 4)
        #expect(block.count == DeviceSimulation.maxStages)
        #expect(block.allSatisfy { $0.active == 1 })
        #expect(p.simulation.enabled == 1)

        // Chosen but switched off: the filters stay in place so switching fades.
        let off = s.kernelParams(sampleRate: 48_000, simulation: sim, simulating: false)
        #expect(off.simulation.enabled == 0)
        #expect(withUnsafeBytes(of: off.simulation.stages) { Array($0.bindMemory(to: tl_biquad.self)) }.allSatisfy { $0.active == 1 })
        // No simulation: a neutral block.
        let none = s.kernelParams(sampleRate: 48_000)
        #expect(none.simulation.enabled == 0 && none.simulation.gain == 1 && none.simulation.width == 1)
    }

    @Test func aSimulationKeepsAtMostTheKernelsStages() throws {
        let many = (0..<20).map { EQBand(frequency: 100 + 100 * Double($0), gain: 1, q: 1) }
        let sim = DeviceSimulation(id: "x", name: "x", kind: .measured, bands: many, width: 3)
        #expect(sim.bands.count == DeviceSimulation.maxStages)
        #expect(sim.bands == Array(many.prefix(DeviceSimulation.maxStages)))
        #expect(sim.width == 1)

        // Stored by a version with more stages: still within the budget.
        var stored = try JSONEncoder().encode(sim)
        var json = try #require(try JSONSerialization.jsonObject(with: stored) as? [String: Any])
        json["bands"] = try JSONSerialization.jsonObject(with: JSONEncoder().encode(many))
        json["width"] = -1
        stored = try JSONSerialization.data(withJSONObject: json)
        let decoded = try JSONDecoder().decode(DeviceSimulation.self, from: stored)
        #expect(decoded.bands.count == DeviceSimulation.maxStages)
        #expect(decoded.width == 0)
        #expect(try JSONDecoder().decode(DeviceSimulation.self, from: JSONEncoder().encode(sim)) == sim)
        let measured = DeviceSimulation(id: "db|AirPods", name: "AirPods", kind: .measured, bands: many, source: "Rig A")
        #expect(try JSONDecoder().decode(DeviceSimulation.self, from: JSONEncoder().encode(measured)).source == "Rig A")
    }

    @Test func aFittedSimulationFitsTheBudget() {
        let grid = ResponseCurve.logFrequencies(count: 400)
        let wobbly = FrequencyResponse(frequencies: grid, levels: grid.map { 70 + 6 * sin(log2($0) * 3) })
        let flat = FrequencyResponse(frequencies: grid, levels: grid.map { _ in 70 })
        let r = DeviceSimulation.fit(device: wobbly, reference: flat, filters: 40)
        #expect(!r.bands.isEmpty && r.bands.count <= DeviceSimulation.maxStages)
    }
}

@Suite struct HeadphoneSimulationTests {
    let grid = ResponseCurve.logFrequencies(count: 600)
    let sr = 48_000.0

    /// A "neutral" curve with the shape of a headphone target (bass shelf, ear gain).
    var neutral: [EQBand] { [EQBand(type: .lowShelf, frequency: 105, gain: 6, q: 0.7), EQBand(frequency: 3000, gain: 8, q: 1)] }
    /// What two measurement rigs add to everything they measure, targets included.
    var rigA: [EQBand] { [EQBand(type: .lowShelf, frequency: 80, gain: -4, q: 0.7), EQBand(frequency: 4000, gain: 5, q: 2)] }
    var rigB: [EQBand] { [EQBand(type: .lowShelf, frequency: 200, gain: 3, q: 0.7), EQBand(frequency: 1500, gain: 4, q: 1.2)] }
    /// How the simulated device differs from neutral: leaky earbud bass, a presence peak, a dip.
    var device: [EQBand] {
        [EQBand(type: .lowShelf, frequency: 150, gain: -10, q: 0.7), EQBand(frequency: 2500, gain: 4, q: 1.5), EQBand(frequency: 6000, gain: -3, q: 3)]
    }

    func curve(_ bands: [EQBand]...) -> FrequencyResponse {
        let all = bands.flatMap { $0 }
        return FrequencyResponse(frequencies: grid, levels: ResponseCurve.responseDB(bands: all, frequencies: grid, sampleRate: sr).map { 80 + $0 })
    }

    /// RMS difference between two curves from 30 Hz to 5 kHz, after matching their level at 200 Hz - 1.2 kHz.
    func deviation(_ a: [Double], _ b: [Double], frequencies f: [Double]) -> Double {
        let mids = f.indices.filter { f[$0] >= 200 && f[$0] <= 1200 }
        let offset = mids.reduce(0) { $0 + a[$1] - b[$1] } / Double(mids.count)
        let band = f.indices.filter { f[$0] >= 30 && f[$0] <= 5000 }
        return sqrt(band.reduce(0) { $0 + pow(a[$1] - b[$1] - offset, 2) } / Double(band.count))
    }

    func simulatedChange(_ r: EQFitter.Result) -> [Double] {
        ResponseCurve.responseDB(bands: r.bands, frequencies: r.frequencies, sampleRate: sr)
    }

    var truth: [Double] { ResponseCurve.responseDB(bands: device, frequencies: EQFitter.grid, sampleRate: sr) }

    @Test func fitsTheDifferenceToTheDevice() {
        let r = DeviceSimulation.fit(device: curve(neutral, device), reference: curve(neutral))
        #expect(r.bands.count <= DeviceSimulation.fittedFilters)
        // The user's sound with the simulation follows the device...
        #expect(deviation(r.predicted, r.target, frequencies: r.frequencies) < 0.8)
        // ...because the EQ is the device's difference from the reference.
        #expect(deviation(simulatedChange(r), truth, frequencies: r.frequencies) < 0.8)
        // Earbud bass is far below what a correction would cut.
        #expect(simulatedChange(r)[0] < -7)
    }

    @Test func sameRigUsesTheRawDifference() throws {
        let correction = CorrectionReference(measurementDatabase: "A", targetDatabase: "A", targetSite: "Rig A", targetName: "Harman")
        let plan = try #require(DeviceSimulation.plan(deviceDatabase: "A", targetDatabase: "A", targets: ["Diffuse field", "Harman"], correction: correction))
        #expect(plan.comparison == .sameRig)
        #expect(plan.referenceDatabase == "A" && plan.referenceTarget == "Harman")
        #expect(DeviceSimulation.comparison(deviceDatabase: "A", correction: correction) == .sameRig)
        #expect(DeviceSimulation.comparison(deviceDatabase: "B", correction: correction) == .crossRig)
        #expect(DeviceSimulation.comparison(deviceDatabase: "A", correction: nil) == .crossRig)
        // The user can compare with another target of the same database.
        #expect(DeviceSimulation.plan(deviceDatabase: "A", targetDatabase: "A", targets: ["Diffuse field", "Harman"], correction: correction,
                                      preferredTarget: "Diffuse field")?.referenceTarget == "Diffuse field")
        // The device and the user's target, both as rig A measures them.
        let r = DeviceSimulation.fit(device: curve(neutral, device, rigA), reference: curve(neutral, rigA))
        #expect(deviation(simulatedChange(r), truth, frequencies: r.frequencies) < 0.8)
    }

    @Test func crossRigGoesThroughEachDatabasesTarget() throws {
        let correction = CorrectionReference(measurementDatabase: "A", targetDatabase: "A", targetSite: "Rig A", targetName: "Harman 2018")
        let plan = try #require(DeviceSimulation.plan(deviceDatabase: "B", targetDatabase: "B", targets: ["Flat", "harman 2018", "Harman 2019"], correction: correction))
        #expect(plan.comparison == .crossRig)
        #expect(plan.referenceDatabase == "B" && plan.referenceTarget == "harman 2018")

        // The device on rig B against rig B's target: the rigs' differences cancel.
        let deviceOnB = curve(neutral, device, rigB)
        let viaTarget = DeviceSimulation.fit(device: deviceOnB, reference: curve(neutral, rigB))
        #expect(deviation(simulatedChange(viaTarget), truth, frequencies: viaTarget.frequencies) < 0.8)
        // The raw difference to the user's target on rig A would carry both rigs along.
        let raw = DeviceSimulation.fit(device: deviceOnB, reference: curve(neutral, rigA))
        #expect(deviation(simulatedChange(raw), truth, frequencies: raw.frequencies) > 2)
    }

    @Test func crossRigPicksTheClosestTarget() {
        let harman = CorrectionReference(measurementDatabase: nil, targetDatabase: "B", targetSite: "Rig B", targetName: "Harman")
        // The user's own measurement compared with a device from the database its target came from:
        // a cross-rig comparison, with the very same target.
        #expect(DeviceSimulation.plan(deviceDatabase: "B", targetDatabase: "B", targets: ["Harman"], correction: harman)?.comparison == .crossRig)
        // No correction known: a Harman target, else the first.
        #expect(DeviceSimulation.plan(deviceDatabase: "C", targetDatabase: "C", targets: ["Flat", "IEF Neutral", "Harman IE 2019"], correction: nil)?.referenceTarget == "Harman IE 2019")
        #expect(DeviceSimulation.plan(deviceDatabase: "C", targetDatabase: "C", targets: ["Flat", "Neutral"], correction: nil)?.referenceTarget == "Flat")
        // A fallback database's targets when the device's database has none.
        #expect(DeviceSimulation.plan(deviceDatabase: "C", targetDatabase: "F", targets: ["Harman"], correction: nil)?.referenceDatabase == "F")
        // The user's choice wins, if the database has it.
        #expect(DeviceSimulation.plan(deviceDatabase: "C", targetDatabase: "C", targets: ["Flat", "Harman"], correction: nil, preferredTarget: "Flat")?.referenceTarget == "Flat")
        #expect(DeviceSimulation.plan(deviceDatabase: "C", targetDatabase: "C", targets: ["Flat", "Harman"], correction: nil, preferredTarget: "Gone")?.referenceTarget == "Harman")
        // Nothing to compare with.
        #expect(DeviceSimulation.plan(deviceDatabase: "C", targetDatabase: "C", targets: [], correction: harman) == nil)
    }

    @Test func trebleIsOnlyBroadlySimulated() {
        // A narrow 13 kHz peak, typical of one rig and one fit, is not worth copying.
        let peaky = curve(neutral, [EQBand(frequency: 13_000, gain: 8, q: 8)])
        let r = DeviceSimulation.fit(device: peaky, reference: curve(neutral))
        #expect(r.bands.filter { $0.type == .peak }.allSatisfy { $0.frequency <= 10_000 })
        let at13k = ResponseCurve.responseDB(bands: r.bands, frequencies: [13_000], sampleRate: sr)[0]
        #expect(at13k < 3, "13 kHz: \(at13k) dB")
    }
}

extension KernelTests {
    /// One cycle of a sine on both channels (or only the left), `frames` long, continuing from `position`.
    func sine(_ frequency: Double, amplitude: Double, frames: Int, position: inout Int, leftOnly: Bool = false) -> [Float] {
        let start = position
        position += frames
        return (0..<frames).flatMap { i -> [Float] in
            let v = Float(amplitude * sin(2 * Double.pi * frequency * Double(start + i) / 48_000))
            return [v, leftOnly ? 0 : v]
        }
    }

    func rms(_ x: [Float]) -> Double { sqrt(x.reduce(0) { $0 + Double($1) * Double($1) } / Double(x.count)) }

    func simulatingKernel(_ s: DSPSettings, _ sim: DeviceSimulation?, on: Bool) -> DSPKernel {
        let kernel = DSPKernel()
        kernel.configure(sampleRate: 48_000, layout: KernelLayout(sourceBufferIndices: [1], leftBuffer: 0, leftChannel: 0, rightBuffer: 0, rightChannel: 1))
        kernel.setSettings(s, sampleRate: 48_000, simulation: sim, simulating: on)
        return kernel
    }

    @Test func simulationPlaysTheDeviceCurveAndMakeupGain() {
        var s = DSPSettings()
        s.effects.limiterEnabled = false
        let sim = SpeakerProfile.phone.simulation
        // 2 kHz is 24 samples per period: 480 frames are 20 whole periods.
        let frames = 480
        var position = 0
        let kernel = simulatingKernel(s, sim, on: true)
        var out: (left: [Float], right: [Float]) = ([], [])
        for _ in 0..<8 { out = cycle(kernel, input: sine(2000, amplitude: 0.25, frames: frames, position: &position), frames: frames) }
        // Without the limiter, the make-up gain keeps the headroom (the phone's presence peak).
        let curve = ResponseCurve.responseDB(bands: sim.bands, frequencies: [2000], sampleRate: 48_000)[0]
        let want = 0.25 / sqrt(2) * dbToLinear(curve + sim.makeupGainDB(after: s))
        #expect(abs(rms(out.left) / want - 1) < 0.01, "got \(rms(out.left)), want \(want)")

        // Chosen but off: the sound is left alone.
        let off = simulatingKernel(s, sim, on: false)
        for _ in 0..<4 { out = cycle(off, input: sine(2000, amplitude: 0.25, frames: frames, position: &position), frames: frames) }
        #expect(abs(rms(out.left) / (0.25 / sqrt(2)) - 1) < 0.001)
    }

    @Test func simulationHandlesTheChannelsLikeTheDevice() {
        var s = DSPSettings()
        s.effects.limiterEnabled = false
        let frames = 480
        var position = 0
        // A phone is mono: a sound on the left comes out of both sides.
        let phone = simulatingKernel(s, SpeakerProfile.phone.simulation, on: true)
        var out: (left: [Float], right: [Float]) = ([], [])
        for _ in 0..<6 { out = cycle(phone, input: sine(2000, amplitude: 0.25, frames: frames, position: &position, leftOnly: true), frames: frames) }
        #expect(zip(out.left, out.right).allSatisfy { abs($0 - $1) < 1e-6 })
        #expect(rms(out.left) > 0.01)

        // Laptop speakers are narrow: the right side gets (1 - w) / (1 + w) of the left.
        let laptop = SpeakerProfile.laptop
        let narrow = simulatingKernel(s, laptop.simulation, on: true)
        for _ in 0..<6 { out = cycle(narrow, input: sine(2000, amplitude: 0.25, frames: frames, position: &position, leftOnly: true), frames: frames) }
        #expect(abs(rms(out.right) / rms(out.left) - (1 - laptop.width) / (1 + laptop.width)) < 0.01)
    }

    @Test func simulationPlaysWhileTheEQIsBypassed() {
        var s = DSPSettings()
        s.bands = [EQBand(type: .lowShelf, frequency: 105, gain: 6, q: 0.7)]
        s.eqEnabled = false
        s.effects.limiterEnabled = false
        let sim = SpeakerProfile.tv.simulation
        let frames = 480
        var position = 0
        let kernel = simulatingKernel(s, sim, on: true)
        var out: (left: [Float], right: [Float]) = ([], [])
        for _ in 0..<8 { out = cycle(kernel, input: sine(2000, amplitude: 0.25, frames: frames, position: &position), frames: frames) }
        let curve = ResponseCurve.responseDB(bands: sim.bands, frequencies: [2000], sampleRate: 48_000)[0]
        let want = 0.25 / sqrt(2) * dbToLinear(s.bypassGainDB() + curve + sim.makeupGainDB(after: s))
        #expect(abs(rms(out.left) / want - 1) < 0.01, "got \(rms(out.left)), want \(want)")
    }

    /// Like the EQ, switching the simulation crossfades: no jump larger than the steepest
    /// slope of the louder of the two sines. The phone's high-pass shifts the phase at 1 kHz.
    @Test func switchingTheSimulationDoesNotClick() {
        var s = DSPSettings()
        s.effects.limiterEnabled = false
        let sim = SpeakerProfile.phone.simulation
        let frames = 512
        let amplitude = 0.3
        var position = 0
        let kernel = simulatingKernel(s, sim, on: false)
        func largestStep(_ x: [Float]) -> Float { zip(x.dropFirst(), x).map { abs($0 - $1) }.max() ?? 0 }
        let gain = max(1, dbToLinear(ResponseCurve.responseDB(bands: sim.bands, frequencies: [1000], sampleRate: 48_000)[0] + sim.makeupGainDB()))
        let slope = Float(2 * Double.pi * 1000 / 48_000 * amplitude * gain)

        var out: [Float] = []
        for _ in 0..<6 { out = cycle(kernel, input: sine(1000, amplitude: amplitude, frames: frames, position: &position), frames: frames).left }
        for on in [true, false, true] {
            kernel.setSettings(s, sampleRate: 48_000, simulation: sim, simulating: on)
            var stream = out
            for _ in 0..<2 { stream += cycle(kernel, input: sine(1000, amplitude: amplitude, frames: frames, position: &position), frames: frames).left }
            #expect(largestStep(stream) < slope * 1.1, "simulation \(on ? "on" : "off"): step \(largestStep(stream)), sine slope \(slope)")
            out = Array(stream.suffix(frames))
        }
    }

    @Test func nonFiniteSimulationParametersAreNeutralised() {
        var s = DSPSettings()
        s.effects.limiterEnabled = false
        let kernel = makeKernel(s)
        var p = s.kernelParams(sampleRate: 48_000)
        p.simulation.enabled = 1
        p.simulation.gain = .nan
        p.simulation.width = .infinity
        p.simulation.stages.0 = tl_biquad(b0: .nan, b1: 0, b2: 0, a1: 0, a2: 0, active: 1, channels: 0)
        tl_kernel_set_params(kernel.pointer, &p)
        var out: (left: [Float], right: [Float]) = ([], [])
        for _ in 0..<3 { out = cycle(kernel, input: dcInput(frames: 512, l: 0.5, r: -0.25), frames: 512) }
        #expect(out.left.allSatisfy(\.isFinite) && out.right.allSatisfy(\.isFinite))
        #expect(abs(out.left[511] - 0.5) < 1e-5 && abs(out.right[511] + 0.25) < 1e-5)

        for bad: Float in [5, -1] {
            p.simulation.gain = bad
            p.simulation.width = bad
            tl_kernel_set_params(kernel.pointer, &p)
            for _ in 0..<2 { out = cycle(kernel, input: dcInput(frames: 512, l: 0.5, r: -0.25), frames: 512) }
            #expect(abs(out.left[511] - 0.5) < 1e-5, "gain \(bad) gave \(out.left[511])")
        }
    }
}
