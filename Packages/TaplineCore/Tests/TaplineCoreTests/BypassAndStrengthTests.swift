import CoreAudio
import Foundation
import Testing
@testable import TaplineCore
import TaplineDSP

@Suite struct LoudnessTests {
    let sr = 48_000.0

    @Test func preampAloneIsItsOwnLoudnessChange() {
        var s = DSPSettings(preampDB: -6, autoPreamp: false, bands: [])
        #expect(abs(s.eqLoudnessDB() + 6) < 1e-9)
        #expect(abs(s.bypassGainDB() + 6) < 1e-9)
        s.preampDB = 0
        #expect(abs(s.eqLoudnessDB()) < 1e-9)
        s.bands = [EQBand(frequency: 1000, gain: 0, q: 1)]
        #expect(abs(s.eqLoudnessDB()) < 1e-6)
    }

    @Test func boostsAreLouderAndCutsQuieter() {
        let boost = ResponseCurve.loudnessChangeDB(bands: [EQBand(frequency: 1000, gain: 6, q: 1)], sampleRate: sr)
        let cut = ResponseCurve.loudnessChangeDB(bands: [EQBand(frequency: 1000, gain: -6, q: 1)], sampleRate: sr)
        #expect(boost > 0.8 && boost < 2, "boost \(boost)")
        #expect(cut < -0.4 && cut > -1.5, "cut \(cut)")
    }

    @Test func trebleCountsMoreThanDeepBass() {
        // K-weighting: about -1 dB at 100 Hz, +4 dB above 2 kHz, so a treble shelf changes
        // loudness much more than a bass shelf of the same gain.
        let bass = ResponseCurve.loudnessChangeDB(bands: [EQBand(type: .lowShelf, frequency: 105, gain: 6, q: 0.71)], sampleRate: sr)
        let treble = ResponseCurve.loudnessChangeDB(bands: [EQBand(type: .highShelf, frequency: 4000, gain: 6, q: 0.71)], sampleRate: sr)
        #expect(bass > 0.3 && bass < 1.2, "bass \(bass)")
        #expect(treble > 2.5 && treble < 4.5, "treble \(treble)")
    }

    @Test func correctionBypassesAtTheLoudnessOfTheEQ() {
        // A typical headphone correction: the automatic preamp takes off the 6 dB bass boost,
        // which makes the corrected sound clearly quieter than the original.
        var s = DSPSettings()
        s.bands = [
            EQBand(type: .lowShelf, frequency: 105, gain: 6.5, q: 0.7),
            EQBand(frequency: 160, gain: -2.5, q: 0.6),
            EQBand(frequency: 2600, gain: -2.9, q: 1.5),
            EQBand(frequency: 3200, gain: 2.7, q: 3.5),
            EQBand(frequency: 5400, gain: -3.6, q: 3.4),
            EQBand(type: .highShelf, frequency: 10_000, gain: -1.6, q: 0.7),
        ]
        let preamp = s.effectivePreampDB()
        let curve = ResponseCurve.loudnessChangeDB(bands: s.activeBands, sampleRate: sr)
        #expect(abs(s.eqLoudnessDB() - (preamp + curve)) < 1e-9)
        #expect(s.bypassGainDB() < -5 && s.bypassGainDB() > -8, "bypass \(s.bypassGainDB())")

        // Tone controls are part of the EQ too.
        s.tone.air = 6
        #expect(s.eqLoudnessDB() > preamp + curve)
    }

    @Test func leftAndRightCountEqually() {
        let left = EQBand(frequency: 1000, gain: 6, q: 1, channels: .left)
        let s = DSPSettings(preampDB: 0, autoPreamp: false, bands: [left])
        let leftOnly = ResponseCurve.loudnessChangeDB(bands: [left], sampleRate: sr, channel: .left)
        let expected = 10 * log10((pow(10, leftOnly / 10) + 1) / 2)
        #expect(abs(s.eqLoudnessDB() - expected) < 1e-9)
        #expect(s.eqLoudnessDB() > 0 && s.eqLoudnessDB() < leftOnly)
    }

    @Test func bypassNeverBoosts() {
        let s = DSPSettings(preampDB: 6, autoPreamp: false, bands: [EQBand(frequency: 1000, gain: 6, q: 1)])
        #expect(s.eqLoudnessDB() > 6)
        #expect(s.bypassGainDB() == 0)
        #expect(s.kernelParams(sampleRate: sr).bypass_gain == 1)
        #expect(DSPSettings.bypassGainDB(loudnessDB: .nan) == 0)
        #expect(DSPSettings.bypassGainDB(loudnessDB: -60) == DSPSettings.preampRange.lowerBound)
    }

    @Test func kernelParamsCarryTheBypassGain() {
        let s = DSPSettings(preampDB: -6, autoPreamp: false, bands: [])
        #expect(abs(s.kernelParams(sampleRate: sr).bypass_gain - Float(dbToLinear(-6))) < 1e-6)
        #expect(s.kernelParams(sampleRate: sr, levelMatchedBypass: false).bypass_gain == 1)
    }
}

extension KernelTests {
    @Test func bypassPlaysAtTheLoudnessOfTheEQ() {
        var s = DSPSettings()
        s.bands = [EQBand(type: .lowShelf, frequency: 105, gain: 6, q: 0.7), EQBand(frequency: 1000, gain: 6, q: 1)]
        s.effects.limiterEnabled = false
        s.eqEnabled = false
        let gain = Float(dbToLinear(s.bypassGainDB()))
        #expect(gain < 0.7)
        let matched = render(settings: s, input: dcInput(frames: 512, l: 0.5, r: -0.25), frames: 512)
        #expect(abs(matched.left[400] - 0.5 * gain) < 1e-4, "got \(matched.left[400]), want \(0.5 * gain)")
        #expect(abs(matched.right[400] + 0.25 * gain) < 1e-4)

        // Switched off in Settings: the original level, as before.
        let kernel = DSPKernel()
        kernel.configure(sampleRate: 48_000, layout: KernelLayout(sourceBufferIndices: [1], leftBuffer: 0, leftChannel: 0, rightBuffer: 0, rightChannel: 1))
        kernel.setSettings(s, sampleRate: 48_000, levelMatchedBypass: false)
        var left: [Float] = []
        for _ in 0..<2 { left = cycle(kernel, input: dcInput(frames: 512, l: 0.5, r: -0.25), frames: 512).left }
        #expect(abs(left[400] - 0.5) < 1e-5)
    }

    @Test func bypassGainIsNeverABoost() {
        var s = DSPSettings()
        s.eqEnabled = false
        s.effects.limiterEnabled = false
        let kernel = makeKernel(s)
        for bad: Float in [4, .nan, .infinity, -1] {
            var p = s.kernelParams(sampleRate: 48_000)
            p.bypass_gain = bad
            tl_kernel_set_params(kernel.pointer, &p)
            var left: [Float] = []
            for _ in 0..<2 { left = cycle(kernel, input: dcInput(frames: 512, l: 0.5, r: 0.5), frames: 512).left }
            #expect(abs(left[400] - 0.5) < 1e-5, "bypass gain \(bad) gave \(left[400])")
        }
    }

    /// Switching the EQ crossfades instead of cutting: no sample-to-sample jump larger than
    /// the steepest slope of the sine itself. A high-pass at the sine's frequency shifts its
    /// phase by 90 degrees, so a hard switch would jump by about a third of full scale.
    @Test func switchingTheEQDoesNotClick() {
        var s = DSPSettings()
        s.bands = [EQBand(type: .highPass, frequency: 1000, q: 0.71)]
        s.effects.limiterEnabled = false
        let kernel = makeKernel(s)
        let frames = 512
        let amplitude = 0.5
        var position = 0
        func sine() -> [Float] {
            let start = position
            position += frames
            return (0..<frames).flatMap { i -> [Float] in
                let v = Float(amplitude * sin(2 * Double.pi * 1000 * Double(start + i) / 48_000))
                return [v, v]
            }
        }
        func largestStep(_ x: [Float]) -> Float { zip(x.dropFirst(), x).map { abs($0 - $1) }.max() ?? 0 }
        let slope = Float(2 * Double.pi * 1000 / 48_000 * amplitude)

        var out: [Float] = []
        for _ in 0..<6 { out = cycle(kernel, input: sine(), frames: frames).left }
        for on in [false, true, false] {
            s.eqEnabled = on
            kernel.setSettings(s, sampleRate: 48_000)
            var stream = out
            for _ in 0..<2 { stream += cycle(kernel, input: sine(), frames: frames).left }
            #expect(largestStep(stream) < slope * 1.1, "EQ \(on ? "on" : "off"): step \(largestStep(stream)), sine slope \(slope)")
            out = Array(stream.suffix(frames))
        }
    }
}

@Suite struct CorrectionStrengthTests {
    let flat = FrequencyResponse(frequencies: [20, 1000, 20_000], levels: [0, 0, 0])
    let truth = [
        EQBand(type: .lowShelf, frequency: 105, gain: 5, q: 0.7),
        EQBand(type: .peak, frequency: 180, gain: -4, q: 1.0),
        EQBand(type: .peak, frequency: 2500, gain: 4, q: 1.4),
        EQBand(type: .peak, frequency: 4500, gain: -5, q: 3),
    ]

    /// A "headphone" whose response is the inverse of some known filters.
    func synthetic(_ filters: [EQBand]) -> FrequencyResponse {
        let grid = ResponseCurve.logFrequencies(count: 600)
        let r = ResponseCurve.responseDB(bands: filters, frequencies: grid, sampleRate: 48_000)
        return FrequencyResponse(frequencies: grid, levels: r.map { 75 - $0 })
    }

    func rms(_ a: [Double], _ b: [Double], frequencies f: [Double]) -> Double {
        let idx = f.indices.filter { f[$0] >= 20 && f[$0] <= 10_000 }
        return sqrt(idx.reduce(0) { $0 + pow(a[$1] - b[$1], 2) } / Double(idx.count))
    }

    @Test func fullStrengthIsTheDefault() {
        var options = EQFitter.Options()
        options.strength = 1
        let a = EQFitter.fit(measurement: synthetic(truth), target: flat)
        let b = EQFitter.fit(measurement: synthetic(truth), target: flat, options: options)
        #expect(a.bands.map { [$0.frequency, $0.gain, $0.q] } == b.bands.map { [$0.frequency, $0.gain, $0.q] })
        #expect(a.fitTarget == a.target)
    }

    @Test func partialStrengthFitsTheBlendedTarget() {
        let full = EQFitter.fit(measurement: synthetic(truth), target: flat)
        var options = EQFitter.Options()
        options.strength = 0.5
        let half = EQFitter.fit(measurement: synthetic(truth), target: flat, options: options)
        let f = half.frequencies

        // The blended target lies halfway between the measurement and the target...
        for i in f.indices {
            #expect(abs(half.fitTarget[i] - (half.measurement[i] + 0.5 * (half.target[i] - half.measurement[i]))) < 1e-9)
        }
        // ...and the EQ follows it as closely as the full correction follows the target.
        let accuracy = rms(half.predicted, half.fitTarget, frequencies: f)
        #expect(accuracy < 0.6, "fit error at half strength \(accuracy) dB")

        // Half the correction: the EQ curve is about half as deep everywhere.
        let probe = [40.0, 100, 180, 600, 2500, 4500, 8000]
        let eqFull = ResponseCurve.responseDB(bands: full.bands, frequencies: probe, sampleRate: 48_000)
        let eqHalf = ResponseCurve.responseDB(bands: half.bands, frequencies: probe, sampleRate: 48_000)
        for (a, b) in zip(eqFull, eqHalf) { #expect(abs(b - a / 2) < 0.8, "full \(eqFull) half \(eqHalf)") }

        // The deviation from the full target reflects the strength.
        #expect(abs(half.errorBeforeDB - full.errorBeforeDB) < 1e-9)
        #expect(half.errorAfterDB > 0.35 * half.errorBeforeDB && half.errorAfterDB < 0.65 * half.errorBeforeDB,
                "\(half.errorBeforeDB) -> \(half.errorAfterDB)")
        #expect(half.errorAfterDB > full.errorAfterDB)
    }

    @Test func zeroStrengthLeavesTheHeadphonesAlone() {
        var options = EQFitter.Options()
        options.strength = 0
        let r = EQFitter.fit(measurement: synthetic(truth), target: flat, options: options)
        #expect(r.bands.isEmpty)
        #expect(r.predicted == r.measurement)
        #expect(abs(r.errorAfterDB - r.errorBeforeDB) < 1e-9)
    }

    @Test func outOfRangeStrengthIsClamped() {
        var options = EQFitter.Options()
        options.strength = 3
        let over = EQFitter.fit(measurement: synthetic(truth), target: flat, options: options)
        #expect(over.fitTarget == over.target)
        options.strength = .nan
        let nan = EQFitter.fit(measurement: synthetic(truth), target: flat, options: options)
        #expect(nan.fitTarget == nan.target)
    }

    @Test func presetBandsScale() {
        let bell = EQBand(type: .peak, frequency: 1000, gain: 6, q: 1)
        #expect(bell.scaled(by: 0.5).gain == 3)
        #expect(bell.scaled(by: 0).gain == 0)
        #expect(bell.scaled(by: 1) == bell)
        #expect(bell.scaled(by: 2).gain == 6)
        #expect(bell.scaled(by: .nan).gain == 6)
        let shelf = EQBand(type: .lowShelf, frequency: 105, gain: -4, q: 0.7)
        #expect(shelf.scaled(by: 0.25).gain == -1)
        let highPass = EQBand(type: .highPass, frequency: 30, gain: 3, q: 0.7)
        #expect(highPass.scaled(by: 0.5) == highPass)
    }
}
