import Foundation
import Testing
@testable import TaplineCore

/// The layout RME's ADI-2 Remote writes (factory values, as saved by the app).
private let rmeSample = """
	<ADI-2DAC>
		<General>
			<val e="Version" v="2"/>
		</General>
		<EQPreset1Left>
			<val e="EQEnable" v="1.00,"/>
			<val e="EQBand1Type" v="1.00,"/>
			<val e="EQBand1Gain" v="0.00,"/>
			<val e="EQBand1Freq" v="100.00,"/>
			<val e="EQBand1Q" v="1.00,"/>
			<val e="EQBand2Gain" v="0.00,"/>
			<val e="EQBand2Freq" v="500.00,"/>
			<val e="EQBand2Q" v="1.00,"/>
			<val e="EQBand3Gain" v="0.00,"/>
			<val e="EQBand3Freq" v="1000.00,"/>
			<val e="EQBand3Q" v="1.00,"/>
			<val e="EQBand4Gain" v="0.00,"/>
			<val e="EQBand4Freq" v="5000.00,"/>
			<val e="EQBand4Q" v="1.00,"/>
			<val e="EQBand5Type" v="1.00,"/>
			<val e="EQBand5Gain" v="0.00,"/>
			<val e="EQBand5Freq" v="10000.00,"/>
			<val e="EQBand5Q" v="1.00,"/>
			<val e="BassTrebleEn" v="1.00,"/>
			<val e="EQBassGain" v="0.00,"/>
			<val e="EQBassFreq" v="85.00,"/>
			<val e="EQBassQ" v="0.90,"/>
			<val e="EQTrebleGain" v="0.00,"/>
			<val e="EQTrebleFreq" v="6500.00,"/>
			<val e="EQTrebleQ" v="0.70,"/>
			<val e="DualEQ" v="0.00,"/>
			<val e="EQ Preset Select" v="1.00,"/>
			<val e="Load BT w. EQ" v="1.00,"/>
		</EQPreset1Left>
		<EQPreset1Right>
			<val e="EQEnable" v="1.00,"/>
			<val e="EQBand1Type" v="1.00,"/>
			<val e="EQBand1Gain" v="0.00,"/>
			<val e="EQBand1Freq" v="100.00,"/>
			<val e="EQBand1Q" v="1.00,"/>
			<val e="EQBand2Gain" v="0.00,"/>
			<val e="EQBand2Freq" v="500.00,"/>
			<val e="EQBand2Q" v="1.00,"/>
			<val e="EQBand3Gain" v="0.00,"/>
			<val e="EQBand3Freq" v="1000.00,"/>
			<val e="EQBand3Q" v="1.00,"/>
			<val e="EQBand4Gain" v="0.00,"/>
			<val e="EQBand4Freq" v="5000.00,"/>
			<val e="EQBand4Q" v="1.00,"/>
			<val e="EQBand5Type" v="1.00,"/>
			<val e="EQBand5Gain" v="0.00,"/>
			<val e="EQBand5Freq" v="10000.00,"/>
			<val e="EQBand5Q" v="1.00,"/>
		</EQPreset1Right>
		<PresetNames>
			<val e="EQPresetName1" v="Factory"/>
		</PresetNames>
	</ADI-2DAC>

"""

@Suite struct ADI2PresetTests {
    /// Element names and keys, in order: what the app expects to find.
    func skeleton(_ text: String) -> [String] {
        text.split(separator: "\n").map { line in
            if let r = line.range(of: "v=\"") { return String(line[..<r.lowerBound]) }
            return String(line)
        }
    }

    @Test func exportHasTheLayoutOfRMEsOwnFiles() throws {
        let file = try ADI2Preset.export(left: [], name: "Factory")
        #expect(skeleton(file) == skeleton(rmeSample))
        #expect(file.hasPrefix("\t<ADI-2DAC>\n\t\t<General>\n\t\t\t<val e=\"Version\" v=\"2\"/>\n"))
        #expect(file.hasSuffix("\t</ADI-2DAC>\n"))
    }

    @Test func exportsBellsRoundedIntoTheirBands() throws {
        let left = [EQBand(frequency: 47.3, gain: -7.74, q: 4.96), EQBand(frequency: 112, gain: 2.2, q: 3), EQBand(frequency: 260, gain: -3.1, q: 2.04)]
        let file = try ADI2Preset.export(left: left, name: "Room")
        #expect(file.contains("<val e=\"EQBand1Type\" v=\"0.00,\"/>"))
        #expect(file.contains("<val e=\"EQBand1Gain\" v=\"-7.50,\"/>"))
        #expect(file.contains("<val e=\"EQBand1Freq\" v=\"47.00,\"/>"))
        #expect(file.contains("<val e=\"EQBand1Q\" v=\"5.00,\"/>"))
        #expect(file.contains("<val e=\"EQBand2Gain\" v=\"2.00,\"/>"))
        #expect(file.contains("<val e=\"EQBand3Freq\" v=\"260.00,\"/>"))
        // Unused bands stay flat.
        #expect(file.contains("<val e=\"EQBand4Gain\" v=\"0.00,\"/>"))
        #expect(file.contains("<val e=\"DualEQ\" v=\"0.00,\"/>"))
        #expect(file.contains("<val e=\"EQPresetName1\" v=\"Room\"/>"))
    }

    @Test func dualEQRoundTrips() throws {
        let left = [EQBand(frequency: 45, gain: -6, q: 5, channels: .left), EQBand(frequency: 300, gain: -2, q: 2, channels: .left)]
        let right = [EQBand(frequency: 52, gain: -8.5, q: 6.2, channels: .right)]
        let file = try ADI2Preset.export(left: left, right: right, name: "Room L/R")
        #expect(file.contains("<val e=\"DualEQ\" v=\"1.00,\"/>"))
        let parsed = try ADI2Preset.parse(file)
        #expect(parsed.name == "Room L/R")
        #expect(parsed.bands.filter { $0.channels == .left }.map(\.frequency) == [45, 300])
        let r = try #require(parsed.bands.first { $0.channels == .right })
        #expect(r.frequency == 52 && r.gain == -8.5 && abs(r.q - 6.2) < 1e-9 && r.type == .peak)
    }

    @Test func refusesWhatTheHardwareCannotHold() {
        let six = (0..<6).map { EQBand(frequency: 300 + Double($0) * 100, gain: -2, q: 1) }
        #expect(throws: ADI2Preset.Error.doesNotFit) { try ADI2Preset.export(left: six, name: "x") }
        // Bands 4 and 5 start at 200 Hz: four bells in the bass do not fit.
        let bass = [40.0, 60, 90, 130].map { EQBand(frequency: $0, gain: -3, q: 4) }
        #expect(throws: ADI2Preset.Error.doesNotFit) { try ADI2Preset.export(left: bass, name: "x") }
        // Bass covers 20-150 Hz only, and there is one of it.
        #expect(throws: ADI2Preset.Error.unsupportedFilter(.lowShelf)) {
            try ADI2Preset.export(left: [EQBand(type: .lowShelf, frequency: 300, gain: 3, q: 0.7)], name: "x")
        }
        #expect(throws: ADI2Preset.Error.unsupportedFilter(.lowShelf)) {
            try ADI2Preset.export(left: [EQBand(type: .lowShelf, frequency: 100, gain: 3, q: 0.7), EQBand(type: .lowShelf, frequency: 60, gain: 2, q: 0.7)], name: "x")
        }
        #expect(throws: ADI2Preset.Error.unsupportedFilter(.highPass)) {
            try ADI2Preset.export(left: [EQBand(type: .highPass, frequency: 30, q: 0.7)], name: "x")
        }
    }

    @Test func shelvesBecomeBassAndTreble() throws {
        // An AutoEQ-style headphone preset.
        let bands = [EQBand(type: .lowShelf, frequency: 105, gain: 5.6, q: 0.7), EQBand(frequency: 180, gain: -3.2, q: 0.9),
                     EQBand(frequency: 2800, gain: 2.1, q: 2.2), EQBand(type: .highShelf, frequency: 10_000, gain: -2.4, q: 0.7)]
        let file = try ADI2Preset.export(left: bands, name: "Headphones")
        #expect(file.contains("<val e=\"EQBassGain\" v=\"5.50,\"/>"))
        #expect(file.contains("<val e=\"EQBassFreq\" v=\"105.00,\"/>"))
        #expect(file.contains("<val e=\"EQTrebleGain\" v=\"-2.50,\"/>"))
        #expect(file.contains("<val e=\"Load BT w. EQ\" v=\"1.00,\"/>"))
        let back = try ADI2Preset.parse(file).bands
        #expect(back.map(\.type) == [.lowShelf, .peak, .peak, .highShelf])
    }

    @Test func readsSharedPresetsWithBassAndTreble() throws {
        var text = rmeSample.replacingOccurrences(of: "<val e=\"EQBand1Type\" v=\"1.00,\"/>", with: "<val e=\"EQBand1Type\" v=\"0.00,\"/>")
        text = text.replacingOccurrences(of: "<val e=\"EQBand1Gain\" v=\"0.00,\"/>", with: "<val e=\"EQBand1Gain\" v=\"2.50,\"/>")
        text = text.replacingOccurrences(of: "<val e=\"EQBassGain\" v=\"0.00,\"/>", with: "<val e=\"EQBassGain\" v=\"3.00,\"/>")
        let parsed = try ADI2Preset.parse(text)
        #expect(parsed.name == "Factory")
        #expect(parsed.bands.count == 2)
        #expect(parsed.bands[0].type == .lowShelf && parsed.bands[0].gain == 3 && parsed.bands[0].frequency == 85)
        #expect(parsed.bands[1].type == .peak && parsed.bands[1].gain == 2.5 && parsed.bands[1].channels == .both)
        #expect(throws: ADI2Preset.Error.notAPreset) { try ADI2Preset.parse("Filter 1: ON PK Fc 100 Hz Gain 1 dB Q 1") }
    }

    @Test func fitStaysWithinTheHardware() throws {
        let grid = EQFitter.grid
        // Five resonances below 200 Hz and one above: more than bands 1-3 can take.
        let room = ResponseCurve.responseDB(bands: [
            EQBand(frequency: 38, gain: 7, q: 5), EQBand(frequency: 55, gain: 9, q: 7), EQBand(frequency: 82, gain: 6, q: 5),
            EQBand(frequency: 120, gain: 5, q: 6), EQBand(frequency: 170, gain: 6, q: 8), EQBand(frequency: 320, gain: 4, q: 3),
        ], frequencies: grid, sampleRate: 48_000)
        let measured = FrequencyResponse(frequencies: grid, levels: room.map { 80 + $0 })
        var base = EQFitter.Options()
        base.correctOnlyBelow = 500
        base.maxBoostDB = 3
        base.maxCutDB = 15
        base.maxQ = 10
        base.lowSmoothingOctaves = 1.0 / 12
        let result = EQFitter.fit(measurement: measured, target: RoomResponse.flatTarget(), options: ADI2Preset.fitOptions(base))

        #expect(result.bands.count <= 5)
        #expect(result.bands.filter { $0.frequency < 200 }.count <= 3)
        #expect(result.bands.allSatisfy { $0.type == .peak && abs($0.gain) <= 12 })
        #expect(result.bands.allSatisfy { abs($0.gain * 2 - ($0.gain * 2).rounded()) < 1e-9 }, "gain steps: \(result.bands.map(\.gain))")
        #expect(result.bands.allSatisfy { abs($0.q * 10 - ($0.q * 10).rounded()) < 1e-6 }, "Q steps: \(result.bands.map(\.q))")
        #expect(result.errorAfterDB < result.errorBeforeDB * 0.75, "\(result.errorBeforeDB) -> \(result.errorAfterDB)")
        // What the fitter made, the exporter takes as is.
        let file = try ADI2Preset.export(left: result.bands, name: "Room")
        let back = try ADI2Preset.parse(file).bands
        #expect(Set(back.map(\.frequency)) == Set(result.bands.map(\.frequency)))
    }
}

@Suite struct LowFrequencyLimitTests {
    @Test func findsTheRollOffAndDoesNotBoostBelowIt() {
        let grid = EQFitter.grid
        // Speakers rolling off at 38 Hz, with a mode at 47 Hz.
        let room = ResponseCurve.responseDB(bands: [EQBand(type: .highPass, frequency: 38, q: 0.8), EQBand(type: .highPass, frequency: 38, q: 0.8),
                                                    EQBand(frequency: 47, gain: 7, q: 5)], frequencies: grid, sampleRate: 48_000)
        let measured = FrequencyResponse(frequencies: grid, levels: room.map { 80 + $0 })
        let limit = RoomResponse.lowFrequencyLimit(measured)
        #expect(limit > 30 && limit < 45, "limit \(limit)")

        var o = EQFitter.Options()
        o.correctOnlyBelow = 500
        o.maxBoostDB = 3
        o.maxQ = 10
        o.lowSmoothingOctaves = 1.0 / 12
        o.noBoostBelow = limit
        let fit = EQFitter.fit(measurement: measured, target: RoomResponse.flatTarget(), options: o)
        let eq = ResponseCurve.responseDB(bands: fit.bands, frequencies: [22, 47], sampleRate: 48_000)
        #expect(eq[0] < 0.5, "boosted the roll-off: \(eq[0])")
        #expect(eq[1] < -4, "mode not cut: \(eq[1])")
    }
}

@Suite struct StereoMeasurementTests {
    /// Speaker + room: a delay (whole samples), a polarity, a colouration.
    func speaker(_ x: [Float], delay: Int, invert: Bool = false, bands: [EQBand] = [], sampleRate: Double) -> [Float] {
        let coloured = RoomTests().filter(x, bands, sampleRate: sampleRate)
        return [Float](repeating: 0, count: delay) + coloured.map { invert ? -$0 : $0 }
    }

    func record(plan: StereoSweepPlan, leftDelay: Int, rightDelay: Int, invertRight: Bool = false, rightGainDB: Double = 0, drift: Double = 0) -> [Float] {
        let sr = plan.sampleRate
        let (l, r) = plan.render()
        let gain = Float(pow(10, rightGainDB / 20))
        let lp = speaker(l, delay: leftDelay, bands: [EQBand(frequency: 60, gain: 6, q: 5)], sampleRate: sr)
        let rp = speaker(r, delay: rightDelay, invert: invertRight, bands: [EQBand(frequency: 75, gain: -5, q: 4)], sampleRate: sr).map { $0 * gain }
        let latency = Int(0.12 * sr)
        var mic = [Float](repeating: 0, count: latency + max(lp.count, rp.count) + Int(0.5 * sr))
        for i in lp.indices { mic[latency + i] += lp[i] }
        for i in rp.indices { mic[latency + i] += rp[i] }
        var seed: UInt64 = 7
        for i in mic.indices {
            seed = seed &* 6364136223846793005 &+ 1442695040888963407
            mic[i] += (Float(seed >> 40) / Float(1 << 24) - 0.5) * 1e-4
        }
        guard drift != 0 else { return mic }
        // The recording interface runs fast by `drift`: it samples the sound at t * (1 + drift).
        let count = Int(Double(mic.count) / (1 + drift)) - 2
        return (0..<count).map { i in
            let x = Double(i) * (1 + drift)
            let j = Int(x), frac = Float(x - Double(j))
            return mic[j] * (1 - frac) + mic[j + 1] * frac
        }
    }

    @Test func findsDistanceBalanceAndBothResponses() throws {
        let sr = 48_000.0
        let plan = StereoSweepPlan(sampleRate: sr, sweepSeconds: 3)
        // Right speaker 7 samples (5.0 cm) farther and 1.5 dB louder.
        let mic = record(plan: plan, leftDelay: 96, rightDelay: 103, rightGainDB: 1.5)
        let a = try #require(StereoSweepPlan.analyze(recording: mic, inputRate: sr, sweepSeconds: 3))
        let dt = try #require(a.arrivalDifference)
        #expect(abs(dt * sr - 7) < 0.15, "arrival difference \(dt * sr) samples")
        #expect(a.oppositePolarity == false)
        let refL = a.left.level(at: 1000), refR = a.right.level(at: 1000)
        #expect(abs(a.left.level(at: 60) - refL - 6) < 1, "left mode \(a.left.level(at: 60) - refL)")
        #expect(abs(a.right.level(at: 75) - refR + 5) < 1, "right dip \(a.right.level(at: 75) - refR)")
        #expect(abs(refR - refL - 1.5) < 0.2)
        #expect(a.snrLeftDB > 40 && a.snrRightDB > 40)
    }

    @Test func noticesOppositePolarity() throws {
        let sr = 48_000.0
        let plan = StereoSweepPlan(sampleRate: sr, sweepSeconds: 3)
        let mic = record(plan: plan, leftDelay: 96, rightDelay: 96, invertRight: true)
        let a = try #require(StereoSweepPlan.analyze(recording: mic, inputRate: sr, sweepSeconds: 3))
        #expect(a.oppositePolarity == true)
        #expect(abs((a.arrivalDifference ?? 1) * sr) < 0.15)
    }

    @Test func cancelsClockDriftBetweenInterfaces() throws {
        let sr = 48_000.0
        let plan = StereoSweepPlan(sampleRate: sr, sweepSeconds: 3)
        // 300 ppm, far worse than real crystals: uncorrected it would read 0.15 ms (5 cm) off.
        let mic = record(plan: plan, leftDelay: 96, rightDelay: 96, drift: 300e-6)
        let a = try #require(StereoSweepPlan.analyze(recording: mic, inputRate: sr, sweepSeconds: 3))
        let cm = (a.arrivalDifference ?? 1) * 34_300
        #expect(abs(cm) < 0.5, "\(cm) cm")
    }
}

@Suite struct RoomAdvisorTests {
    let grid = EQFitter.grid

    func response(_ bands: [EQBand], level: Double = 80) -> FrequencyResponse {
        FrequencyResponse(frequencies: grid, levels: ResponseCurve.responseDB(bands: bands, frequencies: grid, sampleRate: 48_000).map { level + $0 })
    }

    @Test func spotsWhatToFixAtTheSpeakers() {
        let room: [EQBand] = [
            EQBand(type: .lowShelf, frequency: 90, gain: 5, q: 0.7),   // subwoofer too loud
            EQBand(frequency: 47, gain: 8, q: 8),                        // axial mode
            EQBand(frequency: 105, gain: -16, q: 7),                     // cancellation near the crossover
            EQBand(frequency: 165, gain: 6, q: 1.6),                     // desk bounce
        ]
        let input = RoomAdvisor.Input(measurement: response(room), target: RoomResponse.flatTarget(),
                                      left: response(room), right: response(room, level: 81.5),
                                      arrivalDifference: 0.0003, oppositePolarity: false,
                                      positions: 1, worstSNRDB: 48, calibrated: false)
        let advice = RoomAdvisor.advise(input)
        let kinds = advice.map(\.kind)

        #expect(kinds.contains { if case .balance(.right, let db) = $0 { abs(db - 1.5) < 0.2 } else { false } }, "\(kinds)")
        #expect(kinds.contains { if case .distance(.right, let cm) = $0 { abs(cm - 10.3) < 0.2 } else { false } })
        #expect(kinds.contains { if case .subwoofer(let db) = $0 { db > 3 } else { false } })
        #expect(kinds.contains { if case .roomMode(let f, _, let m) = $0 { abs(f - 47) < 3 && abs(m - 3.65) < 0.25 } else { false } })
        #expect(kinds.contains { if case .cancellation(let f, _, _, true) = $0 { abs(f - 105) < 6 } else { false } })
        #expect(kinds.contains { if case .desktop(let f, _) = $0 { abs(f - 165) < 25 } else { false } })
        #expect(kinds.contains { if case .fewPositions = $0 { true } else { false } })
        #expect(!kinds.contains(.polarity))
    }

    @Test func aNarrowModeIsNotADeskBounce() {
        let room = response([EQBand(frequency: 176, gain: 6, q: 6)])
        let advice = RoomAdvisor.advise(RoomAdvisor.Input(measurement: room, target: RoomResponse.flatTarget(), positions: 3, worstSNRDB: 50, calibrated: true))
        #expect(!advice.contains { if case .desktop = $0.kind { true } else { false } }, "\(advice.map(\.kind))")
        #expect(advice.contains { if case .roomMode(let f, _, _) = $0.kind { abs(f - 176) < 8 } else { false } }, "\(advice.map(\.kind))")
    }

    @Test func staysQuietForAGoodRoom() {
        let flat = response([])
        let input = RoomAdvisor.Input(measurement: flat, target: RoomResponse.flatTarget(), left: flat, right: flat,
                                      arrivalDifference: 0.00002, oppositePolarity: false, positions: 4, worstSNRDB: 50, calibrated: true)
        #expect(RoomAdvisor.advise(input).isEmpty)
    }

    @Test func warnsAboutPolarityFirst() {
        let flat = response([])
        let input = RoomAdvisor.Input(measurement: flat, target: RoomResponse.flatTarget(), oppositePolarity: true,
                                      positions: 1, worstSNRDB: 50, calibrated: true)
        #expect(RoomAdvisor.advise(input).first?.kind == .polarity)
    }
}

@Suite struct ChannelTests {
    @Test func oldPresetsDecodeAsBothChannels() throws {
        let json = #"{"id":"6F1C0A8E-34B4-4C59-9E58-1C2B6B7D9A10","type":"peak","frequency":100,"gain":-3,"q":2,"isEnabled":true}"#
        let band = try JSONDecoder().decode(EQBand.self, from: Data(json.utf8))
        #expect(band.channels == .both)
        let again = try JSONDecoder().decode(EQBand.self, from: JSONEncoder().encode(EQBand(frequency: 50, channels: .right)))
        #expect(again.channels == .right)
    }

    @Test func textFormatKeepsChannels() throws {
        let bands = [EQBand(frequency: 100, gain: -2, q: 1), EQBand(frequency: 45, gain: -6, q: 5, channels: .left),
                     EQBand(frequency: 52, gain: -4, q: 4, channels: .right)]
        let text = ParametricEQFormat.export(bands: bands, preampDB: 0)
        #expect(text.contains("Channel: L\nFilter 2:"))
        let parsed = try ParametricEQFormat.parse(text).bands
        #expect(parsed.map(\.channels) == [.both, .left, .right])
        let centre = try ParametricEQFormat.parse("Channel: C\nFilter 1: ON PK Fc 100 Hz Gain -3 dB Q 1\nChannel: all\nFilter 2: ON PK Fc 200 Hz Gain -3 dB Q 1")
        #expect(centre.bands.map(\.frequency) == [200])
    }

    @Test func preampLooksAtEachChannel() {
        var s = DSPSettings()
        s.bands = [EQBand(frequency: 100, gain: 4, q: 2, channels: .left), EQBand(frequency: 100, gain: 5, q: 2, channels: .right)]
        #expect(abs(s.effectivePreampDB() + 5) < 0.05, "\(s.effectivePreampDB())")
    }
}

extension KernelTests {
    @Test func bandsCanTargetOneChannel() {
        var s = DSPSettings()
        s.autoPreamp = false
        // A low shelf passes DC at its full gain: +6 dB on the left only.
        s.bands = [EQBand(type: .lowShelf, frequency: 1000, gain: 6.0206, q: 0.7, channels: .left)]
        let frames = 512
        let out = render(settings: s, input: dcInput(frames: frames, l: 0.2, r: 0.2), frames: frames)
        #expect(abs(out.left[frames - 1] - 0.4) < 0.002, "left \(out.left[frames - 1])")
        #expect(abs(out.right[frames - 1] - 0.2) < 0.002, "right \(out.right[frames - 1])")
    }
}
