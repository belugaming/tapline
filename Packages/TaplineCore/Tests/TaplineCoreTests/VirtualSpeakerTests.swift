import CoreAudio
import Foundation
import Testing
@testable import TaplineCore
import TaplineDSP

/// Deterministic noise for test signals and impulse responses.
private struct Noise {
    var state: UInt64
    init(seed: UInt64) { state = seed &* 6364136223846793005 &+ 1442695040888963407 }
    mutating func next() -> Float {
        state = state &* 6364136223846793005 &+ 1442695040888963407
        return Float(Int64(bitPattern: state >> 11) & 0xFFFFF) / Float(0xFFFFF) * 2 - 1
    }
    mutating func signal(_ count: Int, amplitude: Float = 0.3) -> [Float] { (0..<count).map { _ in next() * amplitude } }
    /// A decaying random impulse response with roughly unit energy.
    mutating func ir(_ count: Int) -> [Float] {
        let tau = max(1, Double(count) / 4)
        let raw = (0..<count).map { i in next() * Float(exp(-Double(i) / tau)) }
        let energy = raw.reduce(0) { $0 + $1 * $1 }
        return raw.map { $0 / max(energy, 1e-9).squareRoot() }
    }
}

@Suite struct VirtualSpeakerKernelTests {
    static let rate = 48_000.0
    /// The limiter's look-ahead is always in the path: lround(1.5 ms).
    static func lookahead(_ rate: Double) -> Int { Int((0.0015 * rate).rounded()) }
    static let latency = DSPKernel.virtualSpeakerLatency
    /// Samples until a switched-on set plays fully: one block, then the 30 ms fade.
    static let settle = latency + Int(0.030 * rate) + 8

    static func settings(enabled: Bool) -> DSPSettings {
        var s = DSPSettings()
        s.effects.limiterEnabled = false
        s.effects.virtualSpeakersEnabled = enabled
        return s
    }

    static func makeKernel(rate: Double = rate, enabled: Bool = true) -> DSPKernel {
        let kernel = DSPKernel()
        kernel.configure(sampleRate: rate, layout: KernelLayout(sourceBufferIndices: [0], leftBuffer: 0, leftChannel: 0, rightBuffer: 0, rightChannel: 1))
        kernel.setSettings(settings(enabled: enabled), sampleRate: rate)
        return kernel
    }

    /// Runs the IOProc over a stereo signal in IO cycles of the given sizes (repeated).
    static func process(_ kernel: DSPKernel, _ left: [Float], _ right: [Float], cycles: [Int] = [512]) -> (left: [Float], right: [Float]) {
        let inList = AudioBufferList.allocate(maximumBuffers: 1)
        let outList = AudioBufferList.allocate(maximumBuffers: 1)
        defer { free(inList.unsafeMutablePointer); free(outList.unsafeMutablePointer) }
        var outL: [Float] = [], outR: [Float] = []
        var start = 0, c = 0
        while start < left.count {
            let n = min(cycles[c % cycles.count], left.count - start)
            c += 1
            var src = [Float](repeating: 0, count: n * 2)
            for i in 0..<n {
                src[2 * i] = left[start + i]
                src[2 * i + 1] = right[start + i]
            }
            var out = [Float](repeating: 0, count: n * 2)
            src.withUnsafeMutableBytes { s in
                out.withUnsafeMutableBytes { o in
                    inList[0] = AudioBuffer(mNumberChannels: 2, mDataByteSize: UInt32(s.count), mData: s.baseAddress)
                    outList[0] = AudioBuffer(mNumberChannels: 2, mDataByteSize: UInt32(o.count), mData: o.baseAddress)
                    var ts = AudioTimeStamp()
                    _ = tl_ioproc(0, &ts, inList.unsafePointer, &ts, outList.unsafeMutablePointer, &ts, kernel.clientData)
                }
            }
            for i in 0..<n {
                outL.append(out[2 * i])
                outR.append(out[2 * i + 1])
            }
            start += n
        }
        return (outL, outR)
    }

    /// Direct convolution at one output index: ear 0 = left, 1 = right.
    static func expected(_ irs: [[Float]], _ left: [Float], _ right: [Float], at n: Int, ear: Int, gain: Double = 1) -> Double {
        var acc = 0.0
        let fromLeft = irs[ear], fromRight = irs[2 + ear]
        for j in 0..<fromLeft.count where n - j >= 0 && n - j < left.count {
            acc += Double(fromLeft[j]) * Double(left[n - j]) + Double(fromRight[j]) * Double(right[n - j])
        }
        return acc * gain
    }

    /// Largest difference from direct convolution over output indices `range`.
    static func maxError(_ out: (left: [Float], right: [Float]), _ irs: [[Float]], _ left: [Float], _ right: [Float],
                         range: Range<Int>, delay: Int, gain: Double = 1) -> Double {
        var worst = 0.0
        for t in range {
            worst = max(worst, abs(Double(out.left[t]) - expected(irs, left, right, at: t - delay, ear: 0, gain: gain)))
            worst = max(worst, abs(Double(out.right[t]) - expected(irs, left, right, at: t - delay, ear: 1, gain: gain)))
        }
        return worst
    }

    static func maxStep(_ x: [Float], in range: Range<Int>) -> Float {
        range.dropFirst().reduce(0) { max($0, abs(x[$1] - x[$1 - 1])) }
    }

    @Test(arguments: [1, 255, 256, 257, 1000, 3000])
    func matchesDirectConvolution(irLength: Int) {
        var noise = Noise(seed: UInt64(irLength))
        let irs = (0..<4).map { _ in noise.ir(irLength) }
        let total = 6000 + irLength
        let left = noise.signal(total), right = noise.signal(total)
        let kernel = Self.makeKernel()
        kernel.setVirtualSpeakers(BinauralIRs(sampleRate: Self.rate, responses: irs), gain: 1, key: "test")
        // Odd IO sizes so blocks straddle IO cycles in every way.
        let out = Self.process(kernel, left, right, cycles: [64, 100, 333, 512, 7, 1024, 256])
        let delay = Self.latency + Self.lookahead(Self.rate)
        let range = (Self.settle + Self.lookahead(Self.rate))..<total
        let error = Self.maxError(out, irs, left, right, range: range, delay: delay)
        #expect(error < 2e-4, "max error \(error)")
    }

    @Test func gainScalesTheOutput() {
        var noise = Noise(seed: 7)
        let irs = (0..<4).map { _ in noise.ir(300) }
        let left = noise.signal(4000), right = noise.signal(4000)
        let kernel = Self.makeKernel()
        kernel.setVirtualSpeakers(BinauralIRs(sampleRate: Self.rate, responses: irs), gain: 0.5, key: "half")
        let out = Self.process(kernel, left, right)
        let range = (Self.settle + 100)..<4000
        let error = Self.maxError(out, irs, left, right, range: range, delay: Self.latency + Self.lookahead(Self.rate), gain: 0.5)
        #expect(error < 1e-4, "max error \(error)")
    }

    @Test func swapsSetsSmoothly() {
        var noise = Noise(seed: 11)
        let a = (0..<4).map { _ in noise.ir(700) }
        let b = (0..<4).map { _ in noise.ir(1500) }
        let count = 16_000
        let left = (0..<count).map { Float(0.4 * sin(2 * Double.pi * 200 * Double($0) / Self.rate)) }
        let right = (0..<count).map { Float(0.4 * sin(2 * Double.pi * 310 * Double($0) / Self.rate)) }
        let kernel = Self.makeKernel()
        kernel.setVirtualSpeakers(BinauralIRs(sampleRate: Self.rate, responses: a), gain: 1, key: "a")
        let swapAt = 6000
        let first = Self.process(kernel, Array(left[..<swapAt]), Array(right[..<swapAt]), cycles: [480])
        kernel.setVirtualSpeakers(BinauralIRs(sampleRate: Self.rate, responses: b), gain: 1, key: "b")
        let second = Self.process(kernel, Array(left[swapAt...]), Array(right[swapAt...]), cycles: [480])
        let out = (left: first.left + second.left, right: first.right + second.right)

        let delay = Self.latency + Self.lookahead(Self.rate)
        let look = Self.lookahead(Self.rate)
        #expect(Self.maxError(out, a, left, right, range: (Self.settle + look)..<(swapAt + look), delay: delay) < 2e-4)
        let swapped = swapAt + Int(0.050 * Self.rate) + look + 4
        #expect(Self.maxError(out, b, left, right, range: swapped..<count, delay: delay) < 2e-4)

        // No jump: the output moves no faster than either convolution does.
        let window = (swapAt - 500)..<(swapped + 500)
        let refA = window.map { Float(Self.expected(a, left, right, at: $0 - delay, ear: 0)) }
        let refB = window.map { Float(Self.expected(b, left, right, at: $0 - delay, ear: 0)) }
        let allowed = max(Self.maxStep(refA, in: 0..<refA.count), Self.maxStep(refB, in: 0..<refB.count)) * 1.1 + 1e-4
        #expect(Self.maxStep(out.left, in: window) <= allowed)
    }

    @Test func switchesOnAndOffWithoutClicks() {
        let count = 12_000
        let tone = (0..<count).map { Float(0.5 * sin(2 * Double.pi * 440 * Double($0) / Self.rate)) }
        // Unity responses: the convolved signal is the dry one, one block later. A hard switch
        // between them would jump by up to the full amplitude.
        let unit: [Float] = [1]
        let irs: [[Float]] = [unit, [0], [0], unit]
        let kernel = Self.makeKernel(enabled: false)
        kernel.setVirtualSpeakers(BinauralIRs(sampleRate: Self.rate, responses: irs), gain: 1, key: "unit")
        let look = Self.lookahead(Self.rate)

        let off = Self.process(kernel, Array(tone[..<3000]), Array(tone[..<3000]))
        #expect(zip(off.left[look...], tone).allSatisfy { $0 == $1 }, "dry while off")

        kernel.setSettings(Self.settings(enabled: true), sampleRate: Self.rate)
        let on = Self.process(kernel, Array(tone[3000..<8000]), Array(tone[3000..<8000]))
        kernel.setSettings(Self.settings(enabled: false), sampleRate: Self.rate)
        let offAgain = Self.process(kernel, Array(tone[8000...]), Array(tone[8000...]))
        let out = off.left + on.left + offAgain.left

        let natural = Self.maxStep(tone, in: 1..<count)
        #expect(Self.maxStep(out, in: 1..<count) < natural * 1.2, "no click")
        // Fully on: one block late.
        for t in (3000 + Self.settle + look)..<8000 {
            #expect(abs(out[t] - tone[t - look - Self.latency]) < 1e-5)
        }
        // Off again after the fade: exactly dry.
        let dryFrom = 8000 + Int(0.030 * Self.rate) + look + 4
        #expect((dryFrom..<count).allSatisfy { out[$0] == tone[$0 - look] })
    }

    @Test func removingTheSetFadesToDry() {
        var noise = Noise(seed: 3)
        let irs = (0..<4).map { _ in noise.ir(400) }
        let tone = (0..<10_000).map { Float(0.3 * sin(2 * Double.pi * 300 * Double($0) / Self.rate)) }
        let kernel = Self.makeKernel()
        kernel.setVirtualSpeakers(BinauralIRs(sampleRate: Self.rate, responses: irs), gain: 1, key: "x")
        _ = Self.process(kernel, Array(tone[..<4000]), Array(tone[..<4000]))
        kernel.setVirtualSpeakers(nil, gain: 1, key: nil)
        #expect(kernel.virtualSpeakersKey == nil)
        let out = Self.process(kernel, Array(tone[4000...]), Array(tone[4000...]))
        let look = Self.lookahead(Self.rate)
        let dry = Int(0.030 * Self.rate) + look + 4
        #expect((dry..<6000).allSatisfy { out.left[$0] == tone[4000 + $0 - look] })
    }

    @Test func handsOverSetsOneAtATime() {
        var noise = Noise(seed: 5)
        let sets = (0..<4).map { _ in (0..<4).map { _ in noise.ir(500) } }
        let total = 20_000
        let left = noise.signal(total), right = noise.signal(total)
        let kernel = Self.makeKernel()
        var out: (left: [Float], right: [Float]) = ([], [])
        var pos = 0
        func run(_ n: Int) {
            let r = Self.process(kernel, Array(left[pos..<(pos + n)]), Array(right[pos..<(pos + n)]), cycles: [128])
            out.left += r.left
            out.right += r.right
            pos += n
            kernel.collectReleasedSets()
        }
        kernel.setVirtualSpeakers(BinauralIRs(sampleRate: Self.rate, responses: sets[0]), gain: 1, key: "0")
        run(3000)
        // Changes faster than the crossfades: some sets are replaced before the IO thread
        // ever sees them, the rest are handed back one by one.
        for (i, set) in sets.enumerated().dropFirst() {
            kernel.setVirtualSpeakers(BinauralIRs(sampleRate: Self.rate, responses: set), gain: 1, key: "\(i)")
            run(256)
        }
        while pos < total { run(min(1000, total - pos)) }
        let delay = Self.latency + Self.lookahead(Self.rate)
        let error = Self.maxError(out, sets[3], left, right, range: (total - 4000)..<total, delay: delay)
        #expect(error < 2e-4, "the last set plays: \(error)")
    }

    @Test func aSampleRateChangeDropsTheSetUntilANewOneArrives() {
        var noise = Noise(seed: 9)
        let irs = BinauralIRs(sampleRate: Self.rate, responses: (0..<4).map { _ in noise.ir(600) })
        let kernel = Self.makeKernel()
        kernel.setVirtualSpeakers(irs, gain: 1, key: "48k")
        let left = noise.signal(6000), right = noise.signal(6000)
        _ = Self.process(kernel, left, right)

        let newRate = 44_100.0
        kernel.configure(sampleRate: newRate, layout: KernelLayout(sourceBufferIndices: [0], leftBuffer: 0, leftChannel: 0, rightBuffer: 0, rightChannel: 1))
        kernel.setSettings(Self.settings(enabled: true), sampleRate: newRate)
        #expect(kernel.virtualSpeakersKey == nil, "the app has to send a set for the new rate")
        let dry = Self.process(kernel, left, right)
        let look = Self.lookahead(newRate)
        #expect((look..<6000).allSatisfy { dry.left[$0] == left[$0 - look] })

        // Prepared at 48 kHz: the kernel wrapper resamples it.
        kernel.setVirtualSpeakers(irs, gain: 1, key: "44.1k")
        let wet = Self.process(kernel, left, right)
        let resampled = irs.resampled(to: newRate).responses
        let settle = Self.latency + Int(0.030 * newRate) + 8 + look
        let error = Self.maxError(wet, resampled, left, right, range: settle..<6000, delay: Self.latency + look)
        #expect(error < 2e-4, "max error \(error)")
    }

    @Test func ignoresASetMadeForTheRateBeforeAReconfigure() {
        var noise = Noise(seed: 13)
        let irs = BinauralIRs(sampleRate: Self.rate, responses: (0..<4).map { _ in noise.ir(200) })
        let kernel = Self.makeKernel()
        kernel.setVirtualSpeakers(irs, gain: 1, key: "48k")   // not picked up yet
        kernel.configure(sampleRate: 96_000, layout: KernelLayout(sourceBufferIndices: [0], leftBuffer: 0, leftChannel: 0, rightBuffer: 0, rightChannel: 1))
        kernel.setSettings(Self.settings(enabled: true), sampleRate: 96_000)
        let left = noise.signal(4000), right = noise.signal(4000)
        let out = Self.process(kernel, left, right)
        let look = Self.lookahead(96_000)
        #expect((look..<4000).allSatisfy { out.left[$0] == left[$0 - look] })
        kernel.collectReleasedSets()
    }

    @Test func recoversFromNonFiniteInput() {
        var noise = Noise(seed: 17)
        let irs = (0..<4).map { _ in noise.ir(300) }
        var s = Self.settings(enabled: true)
        let kernel = Self.makeKernel()
        kernel.setVirtualSpeakers(BinauralIRs(sampleRate: Self.rate, responses: irs), gain: 1, key: "x")
        let dc = [Float](repeating: 0.5, count: 2048)
        _ = Self.process(kernel, dc, dc)
        // An EQ filter that blows up feeds inf and NaN into the convolution.
        var p = s.kernelParams(sampleRate: Self.rate)
        p.stages.0 = tl_biquad(b0: 1, b1: 0, b2: 0, a1: -3, a2: 0, active: 1, channels: 0)
        tl_kernel_set_params(kernel.pointer, &p)
        let blown = Self.process(kernel, dc, dc)
        #expect(blown.left.allSatisfy(\.isFinite) && blown.right.allSatisfy(\.isFinite))

        s.bands = []
        kernel.setSettings(s, sampleRate: Self.rate)
        let after = Self.process(kernel, [Float](repeating: 0.5, count: 8192), [Float](repeating: 0.5, count: 8192))
        let steady = 0.5 * Double(irs[0].reduce(0, +) + irs[2].reduce(0, +))
        #expect(after.left.allSatisfy { $0.isFinite })
        #expect(abs(Double(after.left[8000]) - steady) < 1e-3, "got \(after.left[8000]), want \(steady)")
    }

    @Test func excludesCrossfeed() {
        var s = DSPSettings()
        s.effects.crossfeedEnabled = true
        s.effects.virtualSpeakersEnabled = true
        let p = s.kernelParams(sampleRate: Self.rate)
        #expect(p.crossfeed_enabled == 0 && p.virtual_speakers_enabled == 1)
        s.effects.virtualSpeakersEnabled = false
        #expect(s.kernelParams(sampleRate: Self.rate).crossfeed_enabled == 1)
    }

    @Test func sameKeyIsNotHandedOverTwice() {
        let kernel = Self.makeKernel()
        let irs = BinauralIRs(sampleRate: Self.rate, responses: [[1], [0], [0], [1]])
        kernel.setVirtualSpeakers(irs, gain: 1, key: "k")
        kernel.setVirtualSpeakers(nil, gain: 1, key: "k")
        #expect(kernel.virtualSpeakersKey == "k")
        let tone = (0..<4000).map { Float(0.3 * sin(Double($0) * 0.05)) }
        let out = Self.process(kernel, tone, tone)
        let look = Self.lookahead(Self.rate)
        // Still convolving (one block late), not dry.
        #expect(abs(out.left[3900] - tone[3900 - look - Self.latency]) < 1e-5)
    }
}

@Suite struct WAVFileTests {
    /// A RIFF WAVE file with the given format chunk and raw sample bytes.
    static func wav(tag: UInt16, channels: UInt16, rate: UInt32 = 48_000, bits: UInt16, samples: [UInt8], extensible: Bool = false, extraChunk: Bool = false) -> Data {
        var d = Data()
        func u16(_ v: UInt16) { withUnsafeBytes(of: v.littleEndian) { d.append(contentsOf: $0) } }
        func u32(_ v: UInt32) { withUnsafeBytes(of: v.littleEndian) { d.append(contentsOf: $0) } }
        let align = channels * bits / 8
        d.append(contentsOf: Array("RIFF".utf8)); u32(0)
        d.append(contentsOf: Array("WAVE".utf8))
        if extraChunk {
            // An odd-sized chunk before the format: it is padded to an even length.
            d.append(contentsOf: Array("LIST".utf8)); u32(3); d.append(contentsOf: [1, 2, 3, 0])
        }
        d.append(contentsOf: Array("fmt ".utf8)); u32(extensible ? 40 : 16)
        u16(extensible ? 0xFFFE : tag); u16(channels); u32(rate); u32(rate * UInt32(align)); u16(align); u16(bits)
        if extensible {
            u16(22); u16(bits); u32(0)
            u16(tag); d.append(contentsOf: [0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71])
        }
        d.append(contentsOf: Array("data".utf8)); u32(UInt32(samples.count))
        d.append(contentsOf: samples)
        return d
    }

    @Test func readsSixteenBitPCM() throws {
        let samples: [Int16] = [0, 16384, -32768, 32767]
        let bytes = samples.flatMap { s in withUnsafeBytes(of: s.littleEndian) { Array($0) } }
        let file = try WAVFile.parse(Self.wav(tag: 1, channels: 2, bits: 16, samples: bytes, extraChunk: true))
        #expect(file.sampleRate == 48_000)
        #expect(file.channels == [[0, -1], [0.5, Float(32767) / 32768]])
    }

    @Test func readsTwentyFourBitPCMAndExtensible() throws {
        // 0x400000 = 0.5, 0xC00000 = -0.5
        let bytes: [UInt8] = [0x00, 0x00, 0x40, 0x00, 0x00, 0xC0]
        for extensible in [false, true] {
            let file = try WAVFile.parse(Self.wav(tag: 1, channels: 1, bits: 24, samples: bytes, extensible: extensible))
            #expect(file.channels == [[0.5, -0.5]])
        }
    }

    @Test func readsFloatAndEightBit() throws {
        let floats: [Float] = [0.25, -0.75, .nan]
        let fbytes = floats.flatMap { f in withUnsafeBytes(of: f.bitPattern.littleEndian) { Array($0) } }
        #expect(try WAVFile.parse(Self.wav(tag: 3, channels: 1, bits: 32, samples: fbytes)).channels == [[0.25, -0.75, 0]])
        let doubles: [Double] = [0.5]
        let dbytes = doubles.flatMap { v in withUnsafeBytes(of: v.bitPattern.littleEndian) { Array($0) } }
        #expect(try WAVFile.parse(Self.wav(tag: 3, channels: 1, bits: 64, samples: dbytes)).channels == [[0.5]])
        #expect(try WAVFile.parse(Self.wav(tag: 1, channels: 1, bits: 8, samples: [128, 192, 0])).channels == [[0, 0.5, -1]])
    }

    @Test func roundTripsFloatFiles() throws {
        let file = WAVFile(sampleRate: 44_100, channels: [[0.1, 0.2, -0.3], [1, 0, -1], [0, 0, 0.5], [0.25, 0.5, 0.75]])
        #expect(try WAVFile.parse(file.encoded()) == file)
    }

    @Test func readsATruncatedDataChunkAsFarAsItGoes() throws {
        var data = Self.wav(tag: 1, channels: 1, bits: 16, samples: [0x00, 0x40, 0x00, 0x20])
        data.removeLast(2)
        #expect(try WAVFile.parse(data).channels == [[0.5]])
    }

    @Test func rejectsWhatItCannotRead() {
        #expect(throws: WAVFile.ParseError.notWAV) { try WAVFile.parse(Data("hello world!".utf8)) }
        #expect(throws: WAVFile.ParseError.unsupportedFormat(tag: 2, bits: 4)) {
            try WAVFile.parse(Self.wav(tag: 2, channels: 1, bits: 4, samples: [0, 0]))
        }
        #expect(throws: WAVFile.ParseError.empty) { try WAVFile.parse(Self.wav(tag: 1, channels: 2, bits: 16, samples: [])) }
    }
}

@Suite struct BinauralLayoutTests {
    /// A file whose channel `i` holds the constant (i + 1) / 100.
    static func marked(_ channels: Int, frames: Int = 8) -> WAVFile {
        WAVFile(sampleRate: 48_000, channels: (0..<channels).map { [Float](repeating: Float($0 + 1) / 100, count: frames) })
    }

    static func markers(_ irs: BinauralIRs) -> [Int] { irs.responses.map { Int(($0[0] * 100).rounded()) } }

    @Test func hesuviFourteenChannels() throws {
        // FL-L = 1, FL-R = 2, FR-R = 8, FR-L = 9 (1-based, as HeSuVi numbers them).
        let irs = try BinauralIRs(wav: Self.marked(14))
        #expect(irs.layout == .hesuvi14)
        #expect(Self.markers(irs) == [1, 2, 9, 8])
    }

    @Test func hesuviSevenChannelsMirrorTheLeftSpeaker() throws {
        let irs = try BinauralIRs(wav: Self.marked(7))
        #expect(irs.layout == .hesuvi7)
        #expect(Self.markers(irs) == [1, 2, 2, 1])
    }

    @Test func impulciferAndFourChannelFiles() throws {
        let impulcifer = try BinauralIRs(wav: Self.marked(16))
        #expect(impulcifer.layout == .impulcifer16)
        #expect(Self.markers(impulcifer) == [1, 2, 3, 4])
        let four = try BinauralIRs(wav: Self.marked(4))
        #expect(four.layout == .fourChannel)
        #expect(Self.markers(four) == [1, 2, 3, 4])
    }

    @Test func oneStereoSpeakerIsMirrored() throws {
        let left = WAVFile(sampleRate: 48_000, channels: [[0.9, 0.1], [0.2, 0.05]])
        let fromLeft = try BinauralIRs(wav: left)
        #expect(fromLeft.layout == .mirroredStereo)
        #expect(fromLeft.responses == [[0.9, 0.1], [0.2, 0.05], [0.2, 0.05], [0.9, 0.1]])
        // A right speaker: louder at the right ear.
        let right = WAVFile(sampleRate: 48_000, channels: [[0.2, 0.05], [0.9, 0.1]])
        #expect(try BinauralIRs(wav: right).responses == fromLeft.responses)
    }

    @Test func twoStereoFilesInEitherOrder() throws {
        let left = WAVFile(sampleRate: 48_000, channels: [[0.9], [0.2]])
        let right = WAVFile(sampleRate: 48_000, channels: [[0.3], [0.8]])
        let a = try BinauralIRs(wavFiles: [left, right])
        let b = try BinauralIRs(wavFiles: [right, left])
        #expect(a.layout == .stereoPair)
        #expect(a.responses == [[0.9], [0.2], [0.3], [0.8]])
        #expect(b.responses == a.responses)
        #expect(throws: BinauralIRs.ImportError.sameSide) { try BinauralIRs(wavFiles: [left, left]) }
        let other = WAVFile(sampleRate: 44_100, channels: [[0.3], [0.8]])
        #expect(throws: BinauralIRs.ImportError.sampleRateMismatch) { try BinauralIRs(wavFiles: [left, other]) }
    }

    @Test func rejectsOtherLayouts() {
        #expect(throws: BinauralIRs.ImportError.unsupportedChannelCount(6)) { try BinauralIRs(wav: Self.marked(6)) }
        #expect(throws: BinauralIRs.ImportError.unsupportedChannelCount(1)) { try BinauralIRs(wav: Self.marked(1)) }
        #expect(throws: BinauralIRs.ImportError.wrongFileCount(3)) { try BinauralIRs(wavFiles: [Self.marked(2), Self.marked(2), Self.marked(2)]) }
        let silent = WAVFile(sampleRate: 48_000, channels: [[Float]](repeating: [0, 0], count: 14))
        #expect(throws: BinauralIRs.ImportError.silent) { try BinauralIRs(wav: silent) }
    }

    @Test func trimmingKeepsTheDelaysBetweenEars() {
        let rate = 48_000.0
        func pulse(at i: Int, _ total: Int) -> [Float] { (0..<total).map { $0 == i ? 1 : ($0 == i + 1 ? -0.5 : 0) } }
        let irs = BinauralIRs(sampleRate: rate, responses: [pulse(at: 500, 4000), pulse(at: 530, 4000), pulse(at: 540, 4000), pulse(at: 505, 4000)])
        let trimmed = irs.trimmed()
        // 1 ms before the first onset stays; the tail ends after the last sound.
        #expect(trimmed.responses[0].firstIndex(of: 1) == 48)
        #expect(trimmed.responses[1].firstIndex(of: 1) == 78)
        #expect(trimmed.length == 48 + 40 + 2)
        let cut = irs.trimmed(maxLength: 40)
        #expect(cut.length == 40)
        #expect(cut.responses.allSatisfy { $0.last == 0 })
    }

    @Test func resamplingKeepsGainAndPitch() {
        let rate = 48_000.0
        // A smooth pulse: its sum is the filter's gain at DC.
        let pulse = (0..<64).map { Float(0.5 - 0.5 * cos(2 * Double.pi * Double($0) / 63)) }
        let irs = BinauralIRs(sampleRate: rate, responses: [pulse, pulse, pulse, pulse])
        for target in [44_100.0, 96_000, 192_000] {
            let r = irs.resampled(to: target)
            #expect(r.sampleRate == target)
            let ratio = r.responses[0].reduce(0, +) / pulse.reduce(0, +)
            #expect(abs(ratio - 1) < 0.01, "DC gain at \(target): \(ratio)")
        }
        #expect(irs.resampled(to: rate) == irs)

        // A 1 kHz tone keeps its period: 96 samples at 96 kHz. (The phase keeps the zero
        // crossings off the sample points.)
        let tone = (0..<4800).map { Float(sin(2 * Double.pi * 1000 * Double($0) / rate + 0.5)) }
        let up = BinauralIRs(sampleRate: rate, responses: [tone, tone, tone, tone]).resampled(to: 96_000).responses[0]
        let crossings = (1000..<8600).filter { up[$0 - 1] < 0 && up[$0] >= 0 }
        let period = Double(crossings.last! - crossings.first!) / Double(crossings.count - 1)
        #expect(abs(period - 96) < 0.01, "period \(period)")
    }

    @Test func loudnessGain() {
        let unit = BinauralIRs(sampleRate: 48_000, responses: [[1], [0], [0], [1]])
        #expect(abs(unit.loudnessGain - 1) < 1e-4)
        let loud = BinauralIRs(sampleRate: 48_000, responses: [[2], [0], [0], [2]])
        #expect(abs(loud.loudnessGain - 0.5) < 1e-4)
        // Full crossfeed: mono content doubles (+6 dB), wide content +3 dB; the gain splits it.
        let blended = BinauralIRs(sampleRate: 48_000, responses: [[1], [1], [1], [1]])
        #expect(abs(blended.loudnessGain - Float(pow(4.0 * 2.0, -0.25))) < 1e-4)
    }

    /// The sets shipped in the app bundle.
    static func bundled(_ name: String) throws -> BinauralIRs {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Tapline/Resources/VirtualSpeakers/\(name).wav")
        return try BinauralIRs(wav: WAVFile.parse(Data(contentsOf: url)))
    }

    @Test(arguments: ["SADIE-II-KU100", "SADIE-II-KU100-Room"])
    func bundledSetsAreFrontSpeakers(name: String) throws {
        let irs = try Self.bundled(name)
        #expect(irs.layout == .fourChannel && irs.sampleRate == 48_000)
        // Each speaker is louder at the ear on its side, and reaches it first.
        let e = irs.responses.map(BinauralIRs.energy)
        #expect(e[0] > 4 * e[1] && e[3] > 4 * e[2])
        func onset(_ x: [Float]) -> Int { x.firstIndex { abs($0) > 0.1 * (x.map(abs).max() ?? 0) } ?? 0 }
        #expect(onset(irs.responses[0]) < onset(irs.responses[1]))
        #expect(onset(irs.responses[3]) < onset(irs.responses[2]))
        #expect((0.3...3).contains(irs.loudnessGain))
        #expect(irs.trimmed().length <= Int(TL_CONV_MAX_TAPS))
    }
}

@Suite struct VirtualSpeakerSettingsTests {
    @Test func olderSettingsDecodeWithDefaults() throws {
        let old = #"{"crossfeedEnabled": true, "crossfeed": 0.4}"#
        let fx = try JSONDecoder().decode(EffectsSettings.self, from: Data(old.utf8))
        #expect(fx.crossfeedEnabled && !fx.virtualSpeakersEnabled)
        #expect(fx.virtualSpeakers == EffectsSettings.defaultVirtualSpeakers)
    }

    @Test func roundTrips() throws {
        var fx = EffectsSettings()
        fx.virtualSpeakersEnabled = true
        fx.virtualSpeakers = "B0B5E0D1-0000-0000-0000-000000000001"
        let again = try JSONDecoder().decode(EffectsSettings.self, from: JSONEncoder().encode(fx))
        #expect(again == fx)
    }
}
