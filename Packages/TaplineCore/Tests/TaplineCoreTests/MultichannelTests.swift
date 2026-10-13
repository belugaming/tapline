import CoreAudio
import Foundation
import Testing
@testable import TaplineCore
import TaplineDSP

/// A 7.1.4 output processed by the kernel: one 12-channel stream, tapped as a copy of itself.
@Suite struct MultichannelKernelTests {
    static let rate = 48_000.0
    static let layout = SpeakerLayout.atmos714
    static let channels = 12
    /// The limiter's look-ahead is always in the path: lround(1.5 ms).
    static let lookahead = Int((0.0015 * rate).rounded())
    // Channels of SpeakerLayout.atmos714.
    static let left = 0, right = 1, center = 2, lfe = 3, surroundLeft = 4, topRearRight = 11

    static func settings() -> DSPSettings {
        var s = DSPSettings()
        s.effects.limiterEnabled = false
        return s
    }

    static func makeKernel(_ s: DSPSettings, layout: SpeakerLayout? = layout, roles: [KernelChannelRole]? = nil) -> DSPKernel {
        let kernel = DSPKernel()
        kernel.configure(sampleRate: rate, layout: KernelLayout(sourceBufferIndices: [0], sourceLeftChannel: 0, sourceRightChannel: 1,
                                                                leftBuffer: 0, leftChannel: 0, rightBuffer: 0, rightChannel: 1, passthrough: true,
                                                                channelRoles: roles ?? Self.layout.positions.map(KernelChannelRole.init)))
        kernel.setSettings(s, sampleRate: rate)
        kernel.setSpeakers(s, layout: layout, sampleRate: rate)
        return kernel
    }

    /// One IO cycle over a 12-channel source (`input[channel][frame]`); returns the output by channel.
    static func cycle(_ kernel: DSPKernel, _ input: [[Float]]) -> [[Float]] {
        let frames = input[0].count
        let inList = AudioBufferList.allocate(maximumBuffers: 1)
        let outList = AudioBufferList.allocate(maximumBuffers: 1)
        defer { free(inList.unsafeMutablePointer); free(outList.unsafeMutablePointer) }
        var src = [Float](repeating: 0, count: frames * channels)
        for c in 0..<channels { for i in 0..<frames { src[i * channels + c] = input[c][i] } }
        var out = [Float](repeating: 0, count: frames * channels)
        src.withUnsafeMutableBytes { sr in
            out.withUnsafeMutableBytes { orr in
                inList[0] = AudioBuffer(mNumberChannels: UInt32(channels), mDataByteSize: UInt32(sr.count), mData: sr.baseAddress)
                outList[0] = AudioBuffer(mNumberChannels: UInt32(channels), mDataByteSize: UInt32(orr.count), mData: orr.baseAddress)
                var ts = AudioTimeStamp()
                _ = tl_ioproc(0, &ts, inList.unsafePointer, &ts, outList.unsafeMutablePointer, &ts, kernel.clientData)
            }
        }
        return (0..<channels).map { c in (0..<frames).map { out[$0 * channels + c] } }
    }

    /// Runs `seconds` of input made by `signal(channel, sample index)` in 512-frame cycles.
    static func run(_ kernel: DSPKernel, frames total: Int, _ signal: (Int, Int) -> Float) -> [[Float]] {
        var out = [[Float]](repeating: [], count: channels)
        var start = 0
        while start < total {
            let n = min(512, total - start)
            let input = (0..<channels).map { c in (0..<n).map { signal(c, start + $0) } }
            let o = cycle(kernel, input)
            for c in 0..<channels { out[c] += o[c] }
            start += n
        }
        return out
    }

    static func sine(_ f: Double, amplitude: Float = 0.25) -> (Int, Int) -> Float {
        { _, i in amplitude * Float(sin(2 * Double.pi * f * Double(i) / rate)) }
    }

    /// Peak of the last `count` samples.
    static func peak(_ x: [Float], last count: Int = 4800) -> Float {
        x.suffix(count).map(abs).max() ?? 0
    }

    @Test func everyChannelPassesAtUnity() {
        let kernel = Self.makeKernel(Self.settings())
        let out = Self.run(kernel, frames: 2048) { c, _ in 0.1 * Float(c + 1) / 12 }
        for c in 0..<Self.channels {
            #expect(abs(out[c][1500] - 0.1 * Float(c + 1) / 12) < 1e-6, "channel \(c): \(out[c][1500])")
        }
    }

    @Test func trimsDelaysAndFiltersEachSpeaker() {
        var s = Self.settings()
        s.speakers.layout = Self.layout
        s.speakers.corrections = [
            SpeakerCorrection(channel: Self.center, position: .center, bands: [EQBand(frequency: 1000, gain: 6, q: 1)]),
            SpeakerCorrection(channel: Self.surroundLeft, position: .surroundLeft, trimDB: -6),
            SpeakerCorrection(channel: Self.topRearRight, position: .topRearRight, delayMS: 10),
        ]
        let kernel = Self.makeKernel(s)
        let out = Self.run(kernel, frames: 48_000, Self.sine(1000))
        let reference = Self.peak(out[Self.left])
        // The correction's 6 dB boost takes 6 dB of headroom from every speaker alike.
        #expect(abs(linearToDB(Double(reference / 0.25)) + 6) < 0.1, "front left \(reference)")
        #expect(abs(linearToDB(Double(Self.peak(out[Self.center]) / reference)) - 6) < 0.2, "centre")
        #expect(abs(linearToDB(Double(Self.peak(out[Self.surroundLeft]) / reference)) + 6) < 0.1, "surround left")
        #expect(abs(Self.peak(out[Self.topRearRight]) - reference) < 1e-3, "delay keeps the level")

        // The delay: an impulse comes out 480 samples (10 ms) later than on the front left,
        // once the delay has faded in (20 ms from the start).
        var t = Self.settings()
        t.speakers = s.speakers
        t.speakers.corrections = [s.speakers.corrections[2]]
        let k2 = Self.makeKernel(t)
        let impulse = Self.run(k2, frames: 4096) { _, i in i == 2000 ? 1 : 0 }
        let front = impulse[Self.left].firstIndex { abs($0) > 0.5 }
        let delayed = impulse[Self.topRearRight].firstIndex { abs($0) > 0.5 }
        #expect(front == 2000 + Self.lookahead)
        #expect(delayed == 2000 + Self.lookahead + 480)
    }

    @Test func correctionsOnlyPlayOnTheirLayoutAndWhenEnabled() {
        var s = Self.settings()
        s.speakers.layout = SpeakerLayout.surround51
        s.speakers.corrections = [SpeakerCorrection(channel: Self.center, position: .center, trimDB: -12)]
        let other = Self.run(Self.makeKernel(s), frames: 2048) { _, _ in 0.2 }
        #expect(abs(other[Self.center][1500] - 0.2) < 1e-6, "measured on another layout")
        s.speakers.layout = Self.layout
        s.speakers.correctionEnabled = false
        let off = Self.run(Self.makeKernel(s), frames: 2048) { _, _ in 0.2 }
        #expect(abs(off[Self.center][1500] - 0.2) < 1e-6, "switched off")
        s.speakers.correctionEnabled = true
        let on = Self.run(Self.makeKernel(s), frames: 2048) { _, _ in 0.2 }
        #expect(abs(on[Self.center][1500] - 0.2 * Float(dbToLinear(-12))) < 1e-5, "on: \(on[Self.center][1500])")
    }

    @Test func theEQPlaysOnEverySpeakerOrTheFrontPair() {
        var s = Self.settings()
        s.autoPreamp = false
        s.bands = [EQBand(frequency: 1000, gain: 6, q: 1)]
        let all = Self.run(Self.makeKernel(s), frames: 24_000, Self.sine(1000))
        for c in [Self.left, Self.center, Self.surroundLeft, Self.topRearRight] {
            #expect(abs(linearToDB(Double(Self.peak(all[c]) / 0.25)) - 6) < 0.2, "channel \(c) \(Self.peak(all[c]))")
        }
        s.speakers.eqOnAllSpeakers = false
        let front = Self.run(Self.makeKernel(s), frames: 24_000, Self.sine(1000))
        #expect(abs(linearToDB(Double(Self.peak(front[Self.right]) / 0.25)) - 6) < 0.2)
        #expect(abs(Self.peak(front[Self.center]) - 0.25) < 1e-3)
        #expect(abs(Self.peak(front[Self.surroundLeft]) - 0.25) < 1e-3)
    }

    @Test func theLFELeavesOutBoostsAbove120Hz() {
        var s = Self.settings()
        s.autoPreamp = false
        s.bands = [EQBand(frequency: 1000, gain: 6, q: 1), EQBand(type: .lowShelf, frequency: 105, gain: 6, q: 0.7),
                   EQBand(frequency: 300, gain: -6, q: 2)]
        #expect(s.lfeSkippedStages == 1)
        let out = Self.run(Self.makeKernel(s), frames: 24_000, Self.sine(1000))
        // The cuts and the low shelf still play on the LFE channel; the boost at 1 kHz does not.
        let lfe = linearToDB(Double(Self.peak(out[Self.lfe]) / 0.25))
        let center = linearToDB(Double(Self.peak(out[Self.center]) / 0.25))
        #expect(abs(lfe) < 1, "LFE \(lfe) dB")
        #expect(center > 5, "centre \(center) dB")
        s.tone.air = 4
        #expect(s.lfeSkippedStages == 1 | (1 << (UInt32(TL_MAX_STAGES) - 1)))
    }

    @Test func outputGainReachesEverySpeakerButNotUnknownChannels() {
        var s = Self.settings()
        s.effects.outputGainDB = -6
        var roles = Self.layout.positions.map(KernelChannelRole.init)
        roles[10] = .passthrough
        let out = Self.run(Self.makeKernel(s, roles: roles), frames: 2048) { _, _ in 0.2 }
        let g = Float(dbToLinear(-6))
        for c in [Self.left, Self.center, Self.lfe, Self.topRearRight] {
            #expect(abs(out[c][1500] - 0.2 * g) < 1e-5, "channel \(c)")
        }
        #expect(abs(out[10][1500] - 0.2) < 1e-6, "an unknown channel passes untouched")
    }

    @Test func headphoneEffectsStayOffOnSpeakers() {
        var s = Self.settings()
        s.effects.crossfeedEnabled = true
        s.effects.crossfeed = 1
        let out = Self.run(Self.makeKernel(s), frames: 4096) { c, _ in c == 0 ? 0.3 : 0 }
        #expect(out[Self.right].allSatisfy { $0 == 0 }, "crossfeed leaked into the right speaker")
        // Width and balance act on the front pair only.
        var t = Self.settings()
        t.effects.width = 0
        t.effects.balance = 1
        let narrow = Self.run(Self.makeKernel(t), frames: 4096) { c, _ in c == 0 ? 0.3 : (c == 4 ? 0.1 : 0) }
        #expect(abs(narrow[Self.left][3000]) < 1e-6, "balance right")
        #expect(abs(narrow[Self.right][3000] - 0.15) < 1e-5, "mono front pair")
        #expect(abs(narrow[Self.surroundLeft][3000] - 0.1) < 1e-6, "surround untouched")
    }

    @Test func oneLimiterHoldsEverySpeaker() {
        var s = DSPSettings()
        s.effects.limiterEnabled = true
        s.effects.limiterCeilingDB = -6
        let out = Self.run(Self.makeKernel(s), frames: 9600) { c, i in c == Self.center ? 0.9 * Float(sin(Double(i) * 0.05)) : 0.1 }
        let ceiling = Float(dbToLinear(-6))
        for c in 0..<Self.channels { #expect(out[c].allSatisfy { abs($0) <= ceiling + 1e-6 }) }
        // The quiet speakers are turned down with the loud one, keeping the balance.
        #expect(out[Self.left].suffix(2000).min()! < 0.1 * 0.9)
    }

    @Test func changingADelayGlidesWithoutJumps() {
        var s = Self.settings()
        s.speakers.layout = Self.layout
        let kernel = Self.makeKernel(s)
        let tone = Self.sine(200, amplitude: 0.5)
        var position = 0
        func next(_ n: Int) -> [Float] {
            let o = Self.run(kernel, frames: n) { c, i in tone(c, position + i) }
            position += n
            return o[Self.center]
        }
        var stream = next(4096)
        s.speakers.corrections = [SpeakerCorrection(channel: Self.center, position: .center, delayMS: 3.7)]
        kernel.setSpeakers(s, layout: Self.layout, sampleRate: Self.rate)
        stream += next(4096)
        let slope = Float(2 * Double.pi * 200 / Self.rate * 0.5)
        let step = zip(stream.dropFirst(), stream).map { abs($0 - $1) }.max() ?? 0
        #expect(step < slope * 1.2, "step \(step), sine slope \(slope)")
    }

    @Test func nothingNonFiniteReachesTheSpeakers() {
        var s = DSPSettings()
        s.bands = [EQBand(frequency: 30, gain: 24, q: 18), EQBand(type: .highPass, frequency: 20_000, q: 0.1)]
        s.speakers.layout = Self.layout
        s.speakers.corrections = (0..<Self.channels).map {
            SpeakerCorrection(channel: $0, position: Self.layout.positions[$0], bands: [EQBand(frequency: 20, gain: 24, q: 18)],
                              trimDB: .nan, delayMS: .infinity)
        }
        let kernel = Self.makeKernel(s)
        var p = tl_speakers()
        tl_speakers_init(&p)
        withUnsafeMutableBytes(of: &p.channel) { raw in
            let ch = raw.bindMemory(to: tl_speaker.self)
            ch[2].gain = .nan
            ch[3].delay = -1
            ch[4].stages.0 = tl_biquad(b0: .nan, b1: 0, b2: 0, a1: 0, a2: 0, active: 1, channels: 0)
        }
        tl_kernel_set_speakers(kernel.pointer, &p)
        let out = Self.run(kernel, frames: 8192) { c, i in
            if c == 5 && i % 97 == 0 { return .nan }
            if c == 6 && i % 89 == 0 { return .infinity }
            return Float(sin(Double(i * (c + 1)) * 0.01))
        }
        for c in 0..<Self.channels { #expect(out[c].allSatisfy { $0.isFinite }, "channel \(c)") }
        kernel.setSpeakers(s, layout: Self.layout, sampleRate: Self.rate)
        let again = Self.run(kernel, frames: 4096) { c, i in Float(sin(Double(i * (c + 1)) * 0.01)) }
        for c in 0..<Self.channels { #expect(again[c].allSatisfy { $0.isFinite }, "channel \(c)") }
    }

    /// A stereo output with the same settings plays exactly as before: the speaker block is ignored.
    @Test func stereoOutputsIgnoreTheSpeakerBlock() {
        var s = Self.settings()
        s.bands = [EQBand(frequency: 1000, gain: 3, q: 1)]
        func render(_ speakers: SpeakerSetup) -> [Float] {
            var t = s
            t.speakers = speakers
            let kernel = DSPKernel()
            kernel.configure(sampleRate: Self.rate, layout: KernelLayout(sourceBufferIndices: [0], sourceLeftChannel: 0, sourceRightChannel: 1,
                                                                         leftBuffer: 0, leftChannel: 0, rightBuffer: 0, rightChannel: 1, passthrough: true))
            kernel.setSettings(t, sampleRate: Self.rate)
            kernel.setSpeakers(t, layout: Self.layout, sampleRate: Self.rate)
            return Self.run(kernel, frames: 4096) { c, i in Float(sin(Double(i * (c + 1)) * 0.02)) * 0.3 }.flatMap { $0 }
        }
        let plain = render(SpeakerSetup())
        let corrected = render(SpeakerSetup(eqOnAllSpeakers: false, corrections: [SpeakerCorrection(channel: 0, position: .frontLeft, trimDB: -6, delayMS: 5)],
                                            layout: Self.layout))
        #expect(plain == corrected)
    }
}

// MARK: - Layouts

@Suite struct SpeakerLayoutTests {
    @Test func namesCountEarLevelLFEAndHeightSpeakers() {
        #expect(SpeakerLayout.surround51.name == "5.1")
        #expect(SpeakerLayout.surround71.name == "7.1")
        #expect(SpeakerLayout.atmos514.name == "5.1.4")
        #expect(SpeakerLayout.atmos714.name == "7.1.4")
        #expect(SpeakerLayout.atmos916.name == "9.1.6")
        #expect(SpeakerLayout([.frontLeft, .frontRight]).name == "2.0")
    }

    @Test func coreAudioLabelsBecomePositions() {
        // kAudioChannelLayoutTag_Atmos_7_1_4: L R C LFE Ls Rs Rls Rrs Vhl Vhr Ltr Rtr.
        let labels: [UInt32?] = [1, 2, 3, 4, 5, 6, 33, 34, 13, 15, 52, 54]
        let layout = SpeakerLayout.fromCoreAudio(labels: labels, channelCount: 12, isHDMI: false)
        #expect(layout == .atmos714)
        #expect(layout.isSurround)
        #expect(layout.referenceChannel == 0)
    }

    @Test func unlabelledOutputsFallBackSensibly() {
        // An AV receiver over HDMI that labels nothing: the usual order for its channel count.
        #expect(SpeakerLayout.fromCoreAudio(labels: [], channelCount: 8, isHDMI: true) == .surround71)
        #expect(SpeakerLayout.fromCoreAudio(labels: [0xFFFF_FFFF, 0xFFFF_FFFF], channelCount: 12, isHDMI: true) == .atmos714)
        // An audio interface with ten outputs is a stereo pair plus channels that pass through.
        let interface = SpeakerLayout.fromCoreAudio(labels: [1, 2] + [UInt32?](repeating: (1 << 16) | 3, count: 8), channelCount: 10, isHDMI: false)
        #expect(!interface.isSurround)
        #expect(interface.positions[0] == .frontLeft && interface.positions[9] == .unknown)
        // A label used twice names one speaker.
        let doubled = SpeakerLayout.fromCoreAudio(labels: [1, 2, 3, 3, 4, 5], channelCount: 6, isHDMI: false)
        #expect(doubled.positions == [.frontLeft, .frontRight, .center, .unknown, .lfe, .surroundLeft])
    }

    @Test func positionsGroup() {
        #expect(SpeakerPosition.lfe.isLFE && SpeakerPosition.lfe2.isLFE)
        #expect(SpeakerPosition.topRearLeft.isHeight)
        #expect(SpeakerPosition.rearLeft.group == .surround)
        #expect(SpeakerPosition.allCases.allSatisfy { !$0.abbreviation.isEmpty })
    }
}

// MARK: - Measurement

@Suite struct SpeakerMeasurementTests {
    static let rate = 48_000.0

    /// Runs `x` through biquads, like a speaker and room would colour it.
    static func filter(_ x: [Float], _ bands: [EQBand]) -> [Float] {
        var y = x.map(Double.init)
        for band in bands {
            let c = BiquadCoefficients(band: band, sampleRate: rate)
            var z1 = 0.0, z2 = 0.0
            for i in y.indices {
                let input = y[i]
                let out = c.b0 * input + z1
                z1 = c.b1 * input - c.a1 * out + z2
                z2 = c.b2 * input - c.a2 * out
                y[i] = out
            }
        }
        return y.map(Float.init)
    }

    struct Room {
        var delays: [Int]       // samples, distance included
        var gainsDB: [Double]
        var inverted: Set<Int>
        var colour: [[EQBand]]
    }

    /// Plays every speaker's signal into one microphone: 25 ms of system latency plus each
    /// speaker's distance, gain, polarity and colouring, and a little noise.
    static func record(_ plan: SpeakerSweepPlan, _ room: Room) -> [Float] {
        let signals = plan.render()
        let latency = Int(0.025 * rate)
        var out = [Float](repeating: 0, count: signals[0].count + latency + Int(0.2 * rate))
        for (i, signal) in signals.enumerated() {
            var played = filter(signal, room.colour[i])
            let g = Float(dbToLinear(room.gainsDB[i])) * (room.inverted.contains(i) ? -1 : 1)
            for j in played.indices { played[j] *= g }
            let start = latency + room.delays[i]
            for (j, v) in played.enumerated() where start + j < out.count { out[start + j] += v }
        }
        var seed: UInt64 = 7
        for i in out.indices {
            seed = seed &* 6364136223846793005 &+ 1442695040888963407
            out[i] += (Float(Int64(bitPattern: seed >> 11) & 0xFFFF) / Float(0xFFFF) - 0.5) * 2e-4
        }
        return out
    }

    @Test func measuresLevelsDistancesAndPolarityOfEachSpeaker() throws {
        let layout = SpeakerLayout.surround51
        let speakers = layout.positions.indices.map { SpeakerSweepPlan.Speaker(channel: $0, isLFE: layout.positions[$0].isLFE) }
        let plan = SpeakerSweepPlan(sampleRate: Self.rate, speakers: speakers, sweepSeconds: 2)
        let lowPass = [EQBand(type: .lowPass, frequency: 100, q: 0.71), EQBand(type: .lowPass, frequency: 100, q: 0.71)]
        let room = Room(delays: [0, 29, -40, 96, 120, 55],
                        gainsDB: [0, -0.5, 2, 4, -3, -1.5],
                        inverted: [4],
                        colour: [[], [EQBand(frequency: 80, gain: 6, q: 4)], [], lowPass, [], []])
        let recording = Self.record(plan, room)
        let analysis = try #require(SpeakerSweepPlan.analyze(recording: recording, inputRate: Self.rate, speakers: speakers, sweepSeconds: 2))
        #expect(analysis.speakers.count == 6)
        let result = Dictionary(uniqueKeysWithValues: analysis.speakers.map { ($0.channel, $0) })

        // Levels in each speaker's own band, relative to the front left.
        let levels = layout.positions.indices.map { SpeakerAlignment.level(result[$0]!.response, position: layout.positions[$0]) }
        for c in [1, 2, 4, 5] {
            #expect(abs((levels[c] - levels[0]) - room.gainsDB[c]) < 0.5, "channel \(c): \(levels[c] - levels[0]) dB")
        }
        // Arrivals relative to the front left, to a few microseconds for full-range speakers.
        for c in [1, 2, 4, 5] {
            let arrival = try #require(result[c]!.arrival, "channel \(c)")
            #expect(abs(arrival * Self.rate - Double(room.delays[c])) < 0.5, "channel \(c): \(arrival * Self.rate) samples")
        }
        let reference = try #require(result[0]!.arrival)
        #expect(abs(reference) < 1e-5)
        // The subwoofer: later by its distance plus its low-pass filter's delay.
        let sub = try #require(result[3]!.arrival) * Self.rate
        #expect(sub > Double(room.delays[3]) && sub < Double(room.delays[3]) + 0.008 * Self.rate, "LFE \(sub) samples")
        #expect(result[3]!.oppositePolarity == nil)
        // Polarity against the front left.
        #expect(result[4]!.oppositePolarity == true)
        for c in [0, 1, 2, 5] { #expect(result[c]!.oppositePolarity == false, "channel \(c)") }
        #expect(result.values.allSatisfy { $0.snrDB > 40 })
        // The colouring shows in the response.
        let fr = result[1]!.response
        #expect(fr.level(at: 80) - fr.level(at: 1000) > 4)
    }

    @Test func thePlanKnowsWhatPlaysWhen() {
        let layout = SpeakerLayout.atmos714
        let speakers = layout.positions.indices.map { SpeakerSweepPlan.Speaker(channel: $0, isLFE: layout.positions[$0].isLFE) }
        let plan = SpeakerSweepPlan(sampleRate: 8000, speakers: speakers)
        #expect(abs(SpeakerSweepPlan.duration(speakers: speakers) - plan.duration) < 1e-9)
        #expect(plan.duration > 90 && plan.duration < 100, "\(plan.duration) s")
        #expect(plan.playing(at: 0.3) == nil)
        #expect(plan.playing(at: plan.sweepStart(2) + 1)?.speaker == 2)
        #expect(plan.playing(at: plan.sweepStart(2) + 1)?.chirps == false)
        let slots = plan.chirpSlots
        #expect(slots.count == 14 && slots.first?.speaker == 0 && slots.last?.speaker == 0)
        #expect(plan.playing(at: slots[4].start + 0.1)?.speaker == 3)
        #expect(plan.playing(at: slots[4].start + 0.1)?.chirps == true)
        // Every speaker's signal plays only on it: its sweep and its click.
        let signals = SpeakerSweepPlan(sampleRate: 8000, speakers: Array(speakers.prefix(3)), sweepSeconds: 1).render()
        #expect(signals.count == 3 && Set(signals.map(\.count)).count == 1)
    }

    @Test func alignmentTrimsToTheQuietestAndWaitsForTheFarthest() {
        let speakers = [
            SpeakerAlignment.Speaker(position: .frontLeft, levelDB: 80, arrival: 0),
            SpeakerAlignment.Speaker(position: .frontRight, levelDB: 81.5, arrival: 0.0004),
            SpeakerAlignment.Speaker(position: .center, levelDB: 83, arrival: -0.001),
            SpeakerAlignment.Speaker(position: .lfe, levelDB: 92, arrival: 0.006),
            SpeakerAlignment.Speaker(position: .surroundLeft, levelDB: 79, arrival: nil),
        ]
        let r = SpeakerAlignment.align(speakers)
        #expect(r.map(\.trimDB) == [-1, -2.5, -4, 0, 0])
        #expect(abs(r[0].delayMS - 6) < 1e-9 && abs(r[2].delayMS - 7) < 1e-9 && r[3].delayMS == 0 && r[4].delayMS == 0)
        // A measurement gone wrong never asks for more than the limits.
        let wild = SpeakerAlignment.align([
            SpeakerAlignment.Speaker(position: .frontLeft, levelDB: 60, arrival: 0),
            SpeakerAlignment.Speaker(position: .frontRight, levelDB: 90, arrival: 0.2),
        ])
        #expect(wild[0].delayMS == SpeakerAlignment.maxDelayMS && wild[1].trimDB == -SpeakerAlignment.maxTrimDB)
    }

    @Test func adviceNamesTheSpeakerToCheck() {
        let speakers = [
            SpeakerAdvice.Speaker(channel: 0, position: .frontLeft, levelDB: 80, arrival: 0, oppositePolarity: false),
            SpeakerAdvice.Speaker(channel: 1, position: .frontRight, levelDB: 80, arrival: 0.0001, oppositePolarity: true),
            SpeakerAdvice.Speaker(channel: 2, position: .center, levelDB: 72, arrival: 0.0002, oppositePolarity: false),
            SpeakerAdvice.Speaker(channel: 3, position: .lfe, levelDB: 88, arrival: 0.01, oppositePolarity: nil),
            SpeakerAdvice.Speaker(channel: 4, position: .surroundLeft, levelDB: 80, arrival: nil, oppositePolarity: nil),
            SpeakerAdvice.Speaker(channel: 5, position: .surroundRight, levelDB: 80, arrival: 0.008, oppositePolarity: false),
        ]
        let advice = SpeakerAdvice.advise(speakers, positions: 5, worstSNRDB: 40)
        let kinds = advice.map(\.kind)
        #expect(kinds.contains(.polarity(channel: 1)))
        #expect(kinds.contains(.silent(channel: 4)))
        #expect(kinds.contains(.level(channel: 2, dB: -8)))
        #expect(kinds.contains(.subwoofer(channel: 3, dB: 8)))
        #expect(kinds.contains { if case .distance(5, let m) = $0 { m > 2.5 } else { false } })
        #expect(advice.first?.severity == .warning)
    }

    @Test func theLFEFitNeverBoostsAbove120Hz() {
        let grid = EQFitter.grid
        // A subwoofer: a mode at 45 Hz, a dip at 70 Hz, rolling off above 100 Hz.
        let sub = ResponseCurve.responseDB(bands: [EQBand(frequency: 45, gain: 8, q: 5), EQBand(frequency: 70, gain: -5, q: 4),
                                                   EQBand(type: .lowPass, frequency: 100, q: 0.71), EQBand(type: .lowPass, frequency: 100, q: 0.71),
                                                   EQBand(type: .highPass, frequency: 22, q: 0.71)],
                                           frequencies: grid, sampleRate: 48_000)
        var options = EQFitter.Options(maxFilters: 6)
        options.correctOnlyBelow = SpeakerSetup.lfeBoostCeiling
        options.noBoostAbove = SpeakerSetup.lfeBoostCeiling
        options.levelMatchRange = 30...80
        options.maxBoostDB = 3
        options.maxQ = 10
        let result = EQFitter.fit(measurement: FrequencyResponse(frequencies: grid, levels: sub.map { 90 + $0 }), target: RoomResponse.flatTarget(), options: options)
        let eq = ResponseCurve.responseDB(bands: result.bands, frequencies: [45, 150, 300, 1000], sampleRate: 48_000)
        #expect(eq[0] < -5, "mode not cut: \(eq[0])")
        #expect(eq[1] < 0.5 && eq[2] < 0.5 && abs(eq[3]) < 0.5, "boost above 120 Hz: \(eq)")
        #expect(!result.bands.contains { $0.gain > 0 && $0.frequency > 120 && $0.type != .lowShelf })
    }
}

// MARK: - Settings and text

@Suite struct SpeakerSettingsTests {
    @Test func olderSettingsDecodeWithoutSpeakers() throws {
        let json = #"{"eqEnabled":true,"preampDB":0,"autoPreamp":true,"bands":[]}"#
        let s = try JSONDecoder().decode(DSPSettings.self, from: Data(json.utf8))
        #expect(s.speakers == SpeakerSetup())
        var t = s
        t.speakers = SpeakerSetup(eqOnAllSpeakers: false, corrections: [SpeakerCorrection(channel: 2, position: .center, bands: [EQBand(frequency: 63, gain: -6, q: 4)],
                                                                                              trimDB: -1.5, delayMS: 2.25)],
                                  layout: .surround51)
        let back = try JSONDecoder().decode(DSPSettings.self, from: JSONEncoder().encode(t))
        #expect(back == t)
    }

    @Test func headroomIsSharedByEverySpeaker() {
        let corrections = [SpeakerCorrection(channel: 0, position: .frontLeft, bands: [EQBand(frequency: 50, gain: 3, q: 2)]),
                           SpeakerCorrection(channel: 2, position: .center, bands: [EQBand(frequency: 80, gain: -6, q: 2)])]
        #expect(abs(SpeakerSetup.headroomDB(corrections) + 3) < 0.05)
        #expect(SpeakerSetup.headroomDB([SpeakerCorrection(channel: 1, position: .frontRight, bands: [EQBand(frequency: 80, gain: -6, q: 2)])]) == 0)
    }

    @Test func filtersAsTextHaveASectionPerSpeaker() {
        let text = ParametricEQFormat.exportSpeakers([
            SpeakerCorrection(channel: 2, position: .center, bands: [EQBand(frequency: 63, gain: -6, q: 4)], trimDB: -1.5, delayMS: 1.25),
            SpeakerCorrection(channel: 0, position: .frontLeft, bands: [EQBand(frequency: 47, gain: -4.5, q: 5), EQBand(frequency: 200, gain: 2, q: 2, isEnabled: false)]),
        ], layoutName: "5.1", headroomDB: -2)
        #expect(text == """
        # Room correction for 5.1 speakers, one section per speaker.

        # L (channel 1)
        Channel: 1
        Preamp: -2.0 dB
        Filter 1: ON PK Fc 47 Hz Gain -4.5 dB Q 5.00

        # C (channel 3)
        Channel: 3
        Preamp: -3.5 dB
        Delay: 1.25 ms
        Filter 1: ON PK Fc 63 Hz Gain -6.0 dB Q 4.00

        Channel: all

        """)
    }
}
