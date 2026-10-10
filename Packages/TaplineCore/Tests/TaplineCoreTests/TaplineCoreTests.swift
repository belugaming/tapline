import CoreAudio
import Foundation
import Testing
@testable import TaplineCore
import TaplineDSP

@Suite struct BiquadTests {
    let sr = 48_000.0

    @Test func peakHitsGainAtCenter() {
        let c = BiquadCoefficients.design(type: .peak, frequency: 1000, gainDB: 6, q: 1, sampleRate: sr)
        #expect(abs(c.magnitudeDB(at: 1000, sampleRate: sr) - 6) < 0.01)
        #expect(abs(c.magnitudeDB(at: 20, sampleRate: sr)) < 0.1)
    }

    @Test func shelvesReachTheirPlateau() {
        let low = BiquadCoefficients.design(type: .lowShelf, frequency: 105, gainDB: 4.2, q: 0.7, sampleRate: sr)
        #expect(abs(low.magnitudeDB(at: 20, sampleRate: sr) - 4.2) < 0.3)
        #expect(abs(low.magnitudeDB(at: 10_000, sampleRate: sr)) < 0.05)
        let high = BiquadCoefficients.design(type: .highShelf, frequency: 8000, gainDB: -3, q: 0.7, sampleRate: sr)
        #expect(abs(high.magnitudeDB(at: 20_000, sampleRate: sr) + 3) < 0.3)
    }

    @Test func filtersAttenuate() {
        let hp = BiquadCoefficients.design(type: .highPass, frequency: 100, gainDB: 0, q: 0.707, sampleRate: sr)
        #expect(hp.magnitudeDB(at: 20, sampleRate: sr) < -20)
        #expect(abs(hp.magnitudeDB(at: 100, sampleRate: sr) + 3) < 0.1)
        let notch = BiquadCoefficients.design(type: .notch, frequency: 1000, gainDB: 0, q: 10, sampleRate: sr)
        #expect(notch.magnitudeDB(at: 1000, sampleRate: sr) < -60)
    }

    @Test func autoPreampCancelsBoost() {
        var s = DSPSettings()
        s.bands = [EQBand(type: .peak, frequency: 1000, gain: 6, q: 1)]
        #expect(abs(s.effectivePreampDB() + 6) < 0.05)
        s.bands = [EQBand(type: .peak, frequency: 1000, gain: -6, q: 1)]
        #expect(s.effectivePreampDB() == 0)
    }
}

@Suite struct FormatTests {
    let sample = """
    Preamp: -6.4 dB
    Filter 1: ON LSC Fc 105 Hz Gain 4.2 dB Q 0.70
    Filter 2: ON PK Fc 132 Hz Gain -10.8 dB Q 1.01
    Filter 3: OFF HSC Fc 10000 Hz Gain -1.5 dB Q 0.70
    Filter 4: ON LP Fc 18000 Hz
    """

    @Test func parsesAutoEQ() throws {
        let parsed = try ParametricEQFormat.parse(sample)
        #expect(parsed.preampDB == -6.4)
        #expect(parsed.bands.count == 4)
        #expect(parsed.bands[0].type == .lowShelf)
        #expect(parsed.bands[0].frequency == 105)
        #expect(parsed.bands[0].gain == 4.2)
        #expect(parsed.bands[1].gain == -10.8)
        #expect(parsed.bands[2].isEnabled == false)
        #expect(parsed.bands[3].type == .lowPass)
    }

    @Test func roundTrips() throws {
        let parsed = try ParametricEQFormat.parse(sample)
        let text = ParametricEQFormat.export(bands: parsed.bands, preampDB: parsed.preampDB ?? 0)
        let again = try ParametricEQFormat.parse(text)
        #expect(again.bands.map(\.type) == parsed.bands.map(\.type))
        #expect(again.bands.map(\.isEnabled) == parsed.bands.map(\.isEnabled))
    }

    @Test func rejectsGarbage() {
        #expect(throws: ParametricEQFormat.ParseError.noFilters) { try ParametricEQFormat.parse("hello") }
    }
}

@Suite struct KernelTests {
    /// Runs the C IOProc over one interleaved stereo source into a 10-channel output.
    func render(settings: DSPSettings, input: [Float], frames: Int, sourceGain: Float = 1) -> (left: [Float], right: [Float], kernel: DSPKernel) {
        let kernel = DSPKernel()
        kernel.configure(sampleRate: 48_000, layout: KernelLayout(sourceBufferIndices: [1], leftBuffer: 0, leftChannel: 0, rightBuffer: 0, rightChannel: 1))
        kernel.setSettings(settings, sampleRate: 48_000)
        kernel.setSourceGain(sourceGain, at: 0)

        let inList = AudioBufferList.allocate(maximumBuffers: 2)
        let outList = AudioBufferList.allocate(maximumBuffers: 1)
        defer { free(inList.unsafeMutablePointer); free(outList.unsafeMutablePointer) }

        var mic = [Float](repeating: 0.25, count: frames * 10)    // a "hardware input" stream that must be ignored
        var src = input
        var out = [Float](repeating: 0, count: frames * 10)
        var left: [Float] = []
        var right: [Float] = []
        mic.withUnsafeMutableBytes { micRaw in
            src.withUnsafeMutableBytes { srcRaw in
                out.withUnsafeMutableBytes { outRaw in
                    inList[0] = AudioBuffer(mNumberChannels: 10, mDataByteSize: UInt32(micRaw.count), mData: micRaw.baseAddress)
                    inList[1] = AudioBuffer(mNumberChannels: 2, mDataByteSize: UInt32(srcRaw.count), mData: srcRaw.baseAddress)
                    outList[0] = AudioBuffer(mNumberChannels: 10, mDataByteSize: UInt32(outRaw.count), mData: outRaw.baseAddress)
                    var ts = AudioTimeStamp()
                    // Two cycles: the first flushes the limiter look-ahead.
                    for _ in 0..<2 {
                        _ = tl_ioproc(0, &ts, inList.unsafePointer, &ts, outList.unsafeMutablePointer, &ts, kernel.clientData)
                    }
                    let o = outRaw.bindMemory(to: Float.self)
                    left = (0..<frames).map { o[$0 * 10] }
                    right = (0..<frames).map { o[$0 * 10 + 1] }
                    #expect((0..<frames).allSatisfy { i in (2..<10).allSatisfy { o[i * 10 + $0] == 0 } })
                }
            }
        }
        return (left, right, kernel)
    }

    func dcInput(frames: Int, l: Float, r: Float) -> [Float] {
        (0..<frames).flatMap { _ in [l, r] }
    }

    @Test func passesThroughUnity() {
        var s = DSPSettings()
        s.effects.limiterEnabled = false
        let (l, r, _) = render(settings: s, input: dcInput(frames: 512, l: 0.5, r: -0.25), frames: 512)
        #expect(abs(l[400] - 0.5) < 1e-5)
        #expect(abs(r[400] + 0.25) < 1e-5)
    }

    @Test func appliesSourceGainAndSwap() {
        var s = DSPSettings()
        s.effects.limiterEnabled = false
        s.effects.swapChannels = true
        let (l, r, _) = render(settings: s, input: dcInput(frames: 512, l: 0.5, r: -0.25), frames: 512, sourceGain: 0.5)
        #expect(abs(l[400] + 0.125) < 1e-5)
        #expect(abs(r[400] - 0.25) < 1e-5)
    }

    @Test func limiterHoldsCeiling() {
        var s = DSPSettings()
        s.effects.limiterEnabled = true
        s.effects.limiterCeilingDB = -6
        s.effects.outputGainDB = 12
        let input = (0..<1024).flatMap { i -> [Float] in let v = Float(sin(Double(i) * 0.05)) * 0.9; return [v, v] }
        let (l, _, kernel) = render(settings: s, input: input, frames: 1024)
        let ceiling = Float(dbToLinear(-6))
        #expect(l.allSatisfy { abs($0) <= ceiling + 1e-6 })
        #expect(kernel.readMeters(sourceCount: 1).gainReductionDB > 0)
    }

    @Test func meteringAndAnalyzer() {
        var s = DSPSettings()
        s.effects.limiterEnabled = false
        let (_, _, kernel) = render(settings: s, input: dcInput(frames: 512, l: 0.5, r: 0.5), frames: 512)
        let m = kernel.readMeters(sourceCount: 1)
        #expect(abs(m.peakL - 0.5) < 1e-4)
        #expect(m.ioCycles == 2)
        #expect(abs(m.sourcePeaks[0] - 0.5) < 1e-4)
        var buf = [Float](repeating: 0, count: 256)
        let total = kernel.copyAnalyzer(post: true, into: &buf)
        #expect(total == 1024)
        #expect(abs(buf[255] - 0.5) < 1e-4)
    }
}

@Suite struct AnalyzerTests {
    @Test func fullScaleSineReadsZeroDB() {
        let analyzer = SpectrumAnalyzer(fftSize: 8192, displayPoints: 320)
        analyzer.decay = 0
        let sr = 48_000.0
        let f = 1000.0
        let samples = (0..<8192).map { Float(sin(2 * Double.pi * f * Double($0) / sr)) }
        let out = analyzer.process(samples, sampleRate: sr)
        let idx = analyzer.displayFrequencies.enumerated().min { abs($0.element - f) < abs($1.element - f) }!.offset
        #expect(abs(out[idx]) < 1.5, "got \(out[idx]) dB")
    }
}

@Suite struct FitterTests {
    let flat = FrequencyResponse(frequencies: [20, 1000, 20_000], levels: [0, 0, 0])

    /// A "headphone" whose response is the inverse of some known filters.
    func synthetic(_ filters: [EQBand]) -> FrequencyResponse {
        let grid = ResponseCurve.logFrequencies(count: 600)
        let r = ResponseCurve.responseDB(bands: filters, frequencies: grid, sampleRate: 48_000)
        return FrequencyResponse(frequencies: grid, levels: r.map { 75 - $0 })
    }

    @Test func parsesREWExport() throws {
        let text = """
        * Measurement data measured by REW V5.40
        * Format: Comma/Tab/Space delimited data
        * Freq(Hz) SPL(dB) Phase(degrees)
        20.000, 81.2, 10.0
        25.000 81.5 9.0
        """ + "\n" + (3...20).map { "\(Double($0) * 1000.0)\t\(70 + Double($0) * 0.1)\t0" }.joined(separator: "\n")
        let fr = try #require(FrequencyResponse.parse(text))
        #expect(fr.frequencies.first == 20)
        #expect(fr.levels.first == 81.2)
        #expect(fr.frequencies.count == 20)
        #expect(abs(fr.level(at: 22.36) - 81.35) < 0.05)
    }

    @Test func recoversKnownCorrection() {
        let truth = [
            EQBand(type: .lowShelf, frequency: 105, gain: 5, q: 0.7),
            EQBand(type: .peak, frequency: 180, gain: -4, q: 1.0),
            EQBand(type: .peak, frequency: 2500, gain: 4, q: 1.4),
            EQBand(type: .peak, frequency: 4500, gain: -5, q: 3),
        ]
        let result = EQFitter.fit(measurement: synthetic(truth), target: flat)
        #expect(result.errorBeforeDB > 2.5)
        #expect(result.errorAfterDB < 0.6, "after \(result.errorAfterDB)")
        #expect(result.bands.count <= 10)
        #expect(result.bands.allSatisfy { $0.gain.magnitude <= 18 })
    }

    @Test func keepsBassUntouched() {
        let truth = [EQBand(type: .lowShelf, frequency: 100, gain: -8, q: 0.7), EQBand(type: .peak, frequency: 3000, gain: 5, q: 1)]
        let result = EQFitter.fit(measurement: synthetic(truth), target: flat, options: .init(keepBassBelow: 200))
        let eq = ResponseCurve.responseDB(bands: result.bands, frequencies: [30, 60, 100], sampleRate: 48_000)
        #expect(eq.allSatisfy { abs($0) < 1.0 }, "bass moved: \(eq)")
        // Absolute level is arbitrary (preamp); compare the shape of the midrange correction.
        let eqMid = ResponseCurve.responseDB(bands: result.bands, frequencies: [1000, 3000], sampleRate: 48_000)
        let truthMid = ResponseCurve.responseDB(bands: truth, frequencies: [1000, 3000], sampleRate: 48_000)
        #expect(abs((eqMid[1] - eqMid[0]) - (truthMid[1] - truthMid[0])) < 1.0, "eq \(eqMid) truth \(truthMid)")
    }

    @Test func appliesPreferences() {
        let result = EQFitter.fit(measurement: synthetic([]), target: flat, options: .init(bassAdjustDB: 6))
        let eq = ResponseCurve.responseDB(bands: result.bands, frequencies: [30, 1000], sampleRate: 48_000)
        #expect(abs(eq[0] - 6) < 1.0, "bass \(eq[0])")
        #expect(abs(eq[1]) < 0.7)
    }
}

/// Manual check against real files:
/// TAPLINE_FR_L=… TAPLINE_FR_R=… TAPLINE_TARGET=… swift test --filter realData
@Test func realData() throws {
    let env = ProcessInfo.processInfo.environment
    guard let l = env["TAPLINE_FR_L"], let t = env["TAPLINE_TARGET"] else { return }
    let left = try #require(FrequencyResponse.parse(try String(contentsOfFile: l, encoding: .isoLatin1)))
    let right = env["TAPLINE_FR_R"].flatMap { try? String(contentsOfFile: $0, encoding: .isoLatin1) }.flatMap(FrequencyResponse.parse)
    let measurement = try #require(FrequencyResponse.average([left] + (right.map { [$0] } ?? []), grid: EQFitter.grid))
    let target = try #require(FrequencyResponse.parse(try String(contentsOfFile: t, encoding: .isoLatin1)))
    let start = Date()
    let r = EQFitter.fit(measurement: measurement, target: target)
    print("REAL fit in \(Date().timeIntervalSince(start))s error \(r.errorBeforeDB) -> \(r.errorAfterDB)")
    print(ParametricEQFormat.export(bands: r.bands, preampDB: 0))
}

@Test func trebleIsOnlyBroadlyCorrected() {
    // A narrow 9 kHz coupler resonance must not produce a deep narrow cut.
    let grid = ResponseCurve.logFrequencies(count: 600)
    let resonance = ResponseCurve.responseDB(bands: [EQBand(type: .peak, frequency: 9000, gain: 10, q: 4)], frequencies: grid, sampleRate: 48_000)
    let measurement = FrequencyResponse(frequencies: grid, levels: resonance.map { 70 + $0 })
    let result = EQFitter.fit(measurement: measurement, target: FrequencyResponse(frequencies: [20, 20_000], levels: [0, 0]))
    let eq = ResponseCurve.responseDB(bands: result.bands, frequencies: [9000], sampleRate: 48_000)[0]
    #expect(eq > -4, "deep treble cut: \(eq) dB, bands \(result.bands.map { "\($0.type) \(Int($0.frequency)) \($0.gain) \($0.q)" })")
}

@Test func deviceStreamSourcePassesOtherChannelsThrough() {
    let kernel = DSPKernel()
    kernel.configure(sampleRate: 48_000, layout: KernelLayout(sourceBufferIndices: [0], sourceLeftChannel: 2, sourceRightChannel: 3,
                                                              leftBuffer: 0, leftChannel: 2, rightBuffer: 0, rightChannel: 3, passthrough: true))
    var s = DSPSettings()
    s.effects.limiterEnabled = false
    kernel.setSettings(s, sampleRate: 48_000)
    let frames = 256
    var src = [Float](repeating: 0, count: frames * 10)
    for i in 0..<frames { src[i * 10 + 2] = 0.5; src[i * 10 + 3] = -0.5; src[i * 10 + 6] = 0.25 }
    var out = [Float](repeating: 0, count: frames * 10)
    let inList = AudioBufferList.allocate(maximumBuffers: 1)
    let outList = AudioBufferList.allocate(maximumBuffers: 1)
    defer { free(inList.unsafeMutablePointer); free(outList.unsafeMutablePointer) }
    src.withUnsafeMutableBytes { sr in
        out.withUnsafeMutableBytes { orr in
            inList[0] = AudioBuffer(mNumberChannels: 10, mDataByteSize: UInt32(sr.count), mData: sr.baseAddress)
            outList[0] = AudioBuffer(mNumberChannels: 10, mDataByteSize: UInt32(orr.count), mData: orr.baseAddress)
            var ts = AudioTimeStamp()
            for _ in 0..<2 { _ = tl_ioproc(0, &ts, inList.unsafePointer, &ts, outList.unsafeMutablePointer, &ts, kernel.clientData) }
        }
    }
    let i = 200
    #expect(abs(out[i * 10 + 2] - 0.5) < 1e-5)
    #expect(abs(out[i * 10 + 3] + 0.5) < 1e-5)
    #expect(abs(out[i * 10 + 6] - 0.25) < 1e-5)
    #expect(out[i * 10 + 0] == 0)
}
