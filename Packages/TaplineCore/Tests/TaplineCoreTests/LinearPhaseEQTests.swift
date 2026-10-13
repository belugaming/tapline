import CoreAudio
import Foundation
import Testing
@testable import TaplineCore
import TaplineDSP

/// Magnitude of a symmetric FIR at `f`: h[c] + 2 sum h[c + m] cos(w m), with the cosines from
/// their recurrence.
private func firMagnitudeDB(_ fir: [Float], at f: Double, sampleRate: Double) -> Double {
    let c = fir.count / 2
    let w = 2 * Double.pi * f / sampleRate
    var a = Double(fir[c])
    var previous = 1.0, current = cos(w)
    let twice = 2 * current
    for m in 1..<c {
        a += 2 * Double(fir[c + m]) * current
        let next = twice * current - previous
        previous = current
        current = next
    }
    return 20 * log10(max(abs(a), 1e-9))
}

private func biquadDB(_ stages: [BiquadCoefficients], gain: Double = 1, at f: Double, sampleRate: Double) -> Double {
    stages.reduce(20 * log10(gain)) { $0 + $1.magnitudeDB(at: f, sampleRate: sampleRate) }
}

/// Largest difference in dB between the FIR and the biquads over `frequencies`, where the
/// biquads are above -40 dB (deep in a stop band, both are just "very quiet").
private func worstErrorDB(_ stages: [BiquadCoefficients], gain: Double = 1, sampleRate: Double, frequencies: [Double]) -> Double {
    let fir = LinearPhaseEQ.design(stages, gain: gain, sampleRate: sampleRate)
    return frequencies.reduce(0) { worst, f in
        let target = biquadDB(stages, gain: gain, at: f, sampleRate: sampleRate)
        guard target > -40 else { return worst }
        return max(worst, abs(firMagnitudeDB(fir, at: f, sampleRate: sampleRate) - target))
    }
}

@Suite struct LinearPhaseDesignTests {
    static let rate = 48_000.0
    /// 20 Hz ... 20 kHz, with the bottom octave sampled finely enough for a Q 18 bell.
    static let band = ResponseCurve.logFrequencies(count: 240) + (0..<160).map { 16 + Double($0) * 0.0625 }

    static func stages(_ bands: [EQBand], rate: Double = rate) -> [BiquadCoefficients] {
        bands.map { BiquadCoefficients(band: $0, sampleRate: rate) }
    }

    /// The FIR's length is chosen by sample rate: the same 1.25 s or more everywhere, as that is
    /// what resolves the bass, up to 262,144 taps.
    @Test func lengthAndLatencyFollowTheSampleRate() {
        let expected: [(Double, Int)] = [(22_050, 32_768), (44_100, 65_536), (48_000, 65_536), (88_200, 131_072),
                                         (96_000, 131_072), (192_000, 262_144), (384_000, 262_144)]
        for (rate, taps) in expected {
            #expect(LinearPhaseEQ.taps(sampleRate: rate) == taps, "\(rate)")
            #expect(LinearPhaseEQ.latency(sampleRate: rate) == taps / 2 + taps / Int(TL_LIN_PARTS), "\(rate)")
            #expect(Double(taps) >= min(1.25 * rate, Double(TL_LIN_MAX_TAPS)))
        }
        // 32,768 + 1,024 samples: 704 ms at 48 kHz, 766 ms at 44.1 kHz.
        #expect(LinearPhaseEQ.latency(sampleRate: 48_000) == 33_792)
        #expect(abs(LinearPhaseEQ.latencyMilliseconds(sampleRate: 48_000) - 704) < 0.01)
        #expect(abs(LinearPhaseEQ.latencyMilliseconds(sampleRate: 44_100) - 766.26) < 0.01)
        #expect(DSPKernel.linearPhaseLatency(sampleRate: 96_000) == 67_584)
    }

    @Test(arguments: [44_100.0, 48_000, 96_000])
    func firIsExactlySymmetric(rate: Double) {
        let fir = LinearPhaseEQ.design(Self.stages([
            EQBand(type: .highPass, frequency: 30, q: 0.71),
            EQBand(type: .peak, frequency: 3000, gain: 5, q: 2),
            EQBand(type: .highShelf, frequency: 9000, gain: -4, q: 0.7),
        ], rate: rate), gain: 0.7, sampleRate: rate)
        let c = fir.count / 2
        #expect(fir.count == LinearPhaseEQ.taps(sampleRate: rate))
        #expect(fir[0] == 0)
        let symmetric = (1..<c).allSatisfy { fir[c + $0] == fir[c - $0] }
        #expect(symmetric, "linear phase: both halves are the same numbers")
        // The energy sits at the centre, so the delay is taps / 2.
        let peak = fir.indices.max { abs(fir[$0]) < abs(fir[$1]) }
        let finite = fir.allSatisfy { $0.isFinite }
        #expect(peak == c)
        #expect(finite)
    }

    /// A typical curve, with tone controls, a preamp and a band on one channel: within a
    /// hundredth of a dB of the biquads everywhere from 20 Hz to 20 kHz.
    @Test func firMatchesTheBiquadsAcrossTheBand() {
        var s = DSPSettings(preampDB: -3, autoPreamp: false, bands: [
            EQBand(type: .highPass, frequency: 24, q: 0.71),
            EQBand(type: .lowShelf, frequency: 105, gain: 4.2, q: 0.7),
            EQBand(type: .peak, frequency: 132, gain: -3.5, q: 1.01),
            EQBand(type: .peak, frequency: 350, gain: 2.1, q: 1.21),
            EQBand(type: .peak, frequency: 1875, gain: 3.4, q: 1.8, channels: .left),
            EQBand(type: .peak, frequency: 5985, gain: -4.8, q: 3.5),
            EQBand(type: .highShelf, frequency: 10_000, gain: 2.5, q: 0.7),
            EQBand(type: .notch, frequency: 7000, q: 4, isEnabled: false),
        ])
        s.tone.bass = 2
        s.tone.air = -1.5
        let spec = LinearPhaseEQ.Spec(settings: s, sampleRate: Self.rate)
        #expect(spec.left != spec.right)
        #expect(spec.left.count == 9 && spec.right.count == 8, "disabled bands stay out, tone controls are in")
        #expect(abs(spec.gain - dbToLinear(-3)) < 1e-12)
        for stages in [spec.left, spec.right] {
            let error = worstErrorDB(stages, gain: spec.gain, sampleRate: Self.rate, frequencies: Self.band)
            #expect(error < 0.01, "worst error \(error) dB")
        }
    }

    /// What 65,536 taps (1.37 s at 48 kHz) resolve, as the largest error from 16 Hz to 20 kHz.
    /// Measured: Q 4.3 (one octave) at 20 Hz +12 dB 0.09 dB, Q 10 at 40 Hz -12 dB 0.05 dB,
    /// Q 18 at 100 Hz 0.007 dB. The narrowest bells the app allows at its lowest frequency
    /// lose some of their depth, as they ring longer than the FIR lasts: Q 18 at 20 Hz is
    /// 1.1 dB short at ±6 dB and 5.3 dB at ±24 dB, a resonant Q 18 high-pass at 20 Hz 0.76 dB.
    /// An octave away from the band the error stays below 0.01 dB.
    @Test(arguments: [
        (EQBand(frequency: 20, gain: 6, q: 18), 1.2),
        (EQBand(frequency: 20, gain: -6, q: 18), 1.2),
        (EQBand(frequency: 20, gain: 24, q: 18), 5.5),
        (EQBand(frequency: 20, gain: -24, q: 18), 5.5),
        (EQBand(frequency: 20, gain: 12, q: 4.3), 0.15),
        (EQBand(frequency: 40, gain: -12, q: 10), 0.1),
        (EQBand(frequency: 100, gain: -12, q: 18), 0.02),
        (EQBand(type: .highPass, frequency: 20, q: 18), 0.8),
        (EQBand(frequency: 19_000, gain: 12, q: 18), 0.01),
    ])
    func documentsTheErrorAtTheBottom(band: EQBand, limit: Double) {
        let stages = Self.stages([band])
        let error = worstErrorDB(stages, sampleRate: Self.rate, frequencies: Self.band)
        #expect(error < limit, "\(band.type) \(band.frequency) Hz \(band.gain) dB Q \(band.q): \(error) dB")
        // An octave away from the band the FIR is exact.
        let away = Self.band.filter { $0 > band.frequency * 2 || $0 < band.frequency / 2 }
        #expect(worstErrorDB(stages, sampleRate: Self.rate, frequencies: away) < 0.01)
    }

    @Test func specFollowsWhatTheStagesPlay() {
        var s = DSPSettings(bands: [EQBand(frequency: 1000, gain: 6, q: 1)])
        let a = LinearPhaseEQ.Spec(settings: s, sampleRate: 48_000)
        #expect(a.left == a.right)
        #expect(abs(a.gain - dbToLinear(s.effectivePreampDB())) < 1e-12, "the automatic preamp is part of the FIR")
        s.eqEnabled = false
        #expect(LinearPhaseEQ.Spec(settings: s, sampleRate: 48_000) == a, "bypass does not change the FIR")
        s.filterPhase = .linear
        #expect(LinearPhaseEQ.Spec(settings: s, sampleRate: 48_000) == a)
        s.bands[0].gain = 5.9
        #expect(LinearPhaseEQ.Spec(settings: s, sampleRate: 48_000) != a)
        #expect(LinearPhaseEQ.Spec(settings: s, sampleRate: 0).sampleRate == 48_000)
    }

    @Test func settingsDecodeAndRoundTrip() throws {
        let old = #"{"eqEnabled": true, "bands": []}"#
        #expect(try JSONDecoder().decode(DSPSettings.self, from: Data(old.utf8)).filterPhase == .minimum)
        let future = #"{"filterPhase": "hybrid"}"#
        #expect(try JSONDecoder().decode(DSPSettings.self, from: Data(future.utf8)).filterPhase == .minimum)
        var s = DSPSettings.flat
        s.filterPhase = .linear
        let data = try JSONEncoder().encode(s)
        #expect(String(decoding: data, as: UTF8.self).contains(#""filterPhase":"linear""#))
        #expect(try JSONDecoder().decode(DSPSettings.self, from: data) == s)
        #expect(s.kernelParams(sampleRate: 48_000).linear_phase == 1)
        #expect(DSPSettings.flat.kernelParams(sampleRate: 48_000).linear_phase == 0)
    }
}

@Suite struct LinearPhaseKernelTests {
    static let rate = 48_000.0
    static func lookahead(_ rate: Double) -> Int { Int((0.0015 * rate).rounded()) }
    static let fade = Int(0.030 * rate)

    static func settings(_ bands: [EQBand] = [], linear: Bool = true, preampDB: Double? = nil) -> DSPSettings {
        var s = DSPSettings(bands: bands)
        if let preampDB {
            s.autoPreamp = false
            s.preampDB = preampDB
        }
        s.effects.limiterEnabled = false
        s.filterPhase = linear ? .linear : .minimum
        return s
    }

    static func makeKernel(_ s: DSPSettings, rate: Double = rate, passthrough: Bool = false) -> DSPKernel {
        let kernel = DSPKernel()
        kernel.configure(sampleRate: rate, layout: KernelLayout(sourceBufferIndices: [0], leftBuffer: 0, leftChannel: 0, rightBuffer: 0, rightChannel: 1))
        kernel.setSettings(s, sampleRate: rate)
        if s.filterPhase == .linear { kernel.setLinearPhase(settings: s) }
        return kernel
    }

    static func process(_ kernel: DSPKernel, _ left: [Float], _ right: [Float], cycles: [Int] = [512]) -> (left: [Float], right: [Float]) {
        VirtualSpeakerKernelTests.process(kernel, left, right, cycles: cycles)
    }

    static func sine(_ count: Int, frequency: Double, amplitude: Double = 0.5, from start: Int = 0) -> [Float] {
        (0..<count).map { Float(amplitude * sin(2 * Double.pi * frequency * Double(start + $0) / rate)) }
    }

    static func maxStep(_ x: [Float], in range: Range<Int>) -> Float {
        range.dropFirst().reduce(0) { max($0, abs(x[$1] - x[$1 - 1])) }
    }

    /// An impulse comes out as the FIR itself, sample for sample, the reported latency late.
    @Test func impulseResponseIsTheFIR() {
        let s = Self.settings([EQBand(type: .lowShelf, frequency: 100, gain: 5, q: 0.7),
                               EQBand(frequency: 2500, gain: -6, q: 3, channels: .right)])
        let kernel = Self.makeKernel(s)
        let spec = LinearPhaseEQ.Spec(settings: s, sampleRate: Self.rate)
        let left = LinearPhaseEQ.design(spec.left, gain: spec.gain, sampleRate: Self.rate)
        let right = LinearPhaseEQ.design(spec.right, gain: spec.gain, sampleRate: Self.rate)
        let latency = LinearPhaseEQ.latency(sampleRate: Self.rate), look = Self.lookahead(Self.rate)
        let at = latency + Self.fade + 1000   // the FIR has faded in
        let count = at + latency + left.count
        var x = [Float](repeating: 0, count: count)
        x[at] = 1
        let out = Self.process(kernel, x, x, cycles: [512, 333, 64])
        let start = at + latency - left.count / 2 + look
        var worst: Float = 0
        for k in 0..<left.count {
            worst = max(worst, abs(out.left[start + k] - left[k]), abs(out.right[start + k] - right[k]))
        }
        #expect(worst < 1e-5, "largest difference from the FIR \(worst)")
        // The peak, the centre of symmetry, comes exactly `latency` samples late.
        let peak = out.left.indices.max { abs(out.left[$0]) < abs(out.left[$1]) }
        #expect(peak == at + latency + look)
    }

    /// Noise through odd IO sizes is the direct convolution with the FIR (8 kHz keeps the
    /// reference short: 16,384 taps).
    @Test func noiseMatchesDirectConvolution() {
        let rate = 8000.0
        let s = Self.settings([EQBand(frequency: 300, gain: 6, q: 2), EQBand(type: .highShelf, frequency: 2000, gain: -4, q: 0.7)])
        let kernel = Self.makeKernel(s, rate: rate)
        let spec = LinearPhaseEQ.Spec(settings: s, sampleRate: rate)
        let fir = LinearPhaseEQ.design(spec.left, gain: spec.gain, sampleRate: rate)
        #expect(fir.count == 16_384)
        let latency = LinearPhaseEQ.latency(sampleRate: rate), look = Self.lookahead(rate), block = latency - fir.count / 2
        var state: UInt64 = 42
        let count = latency + Int(0.03 * rate) + fir.count + 3000
        let x: [Float] = (0..<count).map { _ in
            state = state &* 6364136223846793005 &+ 1442695040888963407
            return Float(Int64(bitPattern: state >> 11) & 0xFFFF) / Float(0xFFFF) * 0.6 - 0.3
        }
        let out = Self.process(kernel, x, x, cycles: [100, 333, 512, 7, 1024])
        var worst = 0.0
        for t in (count - 2000)..<count {
            var acc = 0.0
            for k in 0..<fir.count where t - look - block - k >= 0 {
                acc += Double(fir[k]) * Double(x[t - look - block - k])
            }
            worst = max(worst, abs(Double(out.left[t]) - acc))
        }
        #expect(worst < 1e-4, "largest difference from direct convolution \(worst)")
    }

    /// Bypassed, the linear-phase path plays the input delayed by the same latency at the
    /// bypass gain. A flat EQ with a -6 dB preamp bypasses at -6 dB (level matched), so the two
    /// are the same signal: switching back and forth changes nothing, which a wrong delay
    /// (comb filtering, a jump in time) would show at once.
    @Test func bypassIsTimeAlignedWithTheFIR() {
        var s = Self.settings(preampDB: -6)
        let kernel = Self.makeKernel(s)
        let latency = LinearPhaseEQ.latency(sampleRate: Self.rate), look = Self.lookahead(Self.rate)
        let gain = Float(dbToLinear(-6))
        let count = 4 * latency
        let x = Self.sine(count, frequency: 997)
        var out: [Float] = []
        var pos = 0
        for (i, n) in [latency + 4000, 3000, 2000, 5000, 1000, count].enumerated() {
            let end = min(count, pos + n)
            s.eqEnabled = i % 2 == 0
            kernel.setSettings(s, sampleRate: Self.rate)
            out += Self.process(kernel, Array(x[pos..<end]), Array(x[pos..<end]), cycles: [480]).left
            pos = end
        }
        let from = latency + Self.fade + look + 10
        var worst: Float = 0
        for t in from..<count { worst = max(worst, abs(out[t] - gain * x[t - latency - look])) }
        #expect(worst < 1e-4, "bypass and EQ differ by \(worst)")

        // Bypassed with a real curve: exactly the delayed input at the bypass gain.
        s = Self.settings([EQBand(frequency: 1000, gain: 6, q: 1)])
        s.eqEnabled = false
        let bypassed = Self.makeKernel(s)
        let g = Float(dbToLinear(s.bypassGainDB()))
        #expect(g < 0.95)
        let y = Self.process(bypassed, x, x).left
        for t in stride(from: from, to: count, by: 97) { #expect(abs(y[t] - g * x[t - latency - look]) < 1e-5) }
    }

    /// Switching the bypass with a curve that shifts no phase in linear phase: no step larger
    /// than the sine's own, where minimum phase needs its one-buffer crossfade to get there.
    @Test func switchingTheBypassDoesNotClick() {
        var s = Self.settings([EQBand(type: .highPass, frequency: 1000, q: 0.71)])
        let kernel = Self.makeKernel(s)
        let latency = LinearPhaseEQ.latency(sampleRate: Self.rate)
        let count = latency * 2 + 20_000
        let x = Self.sine(count, frequency: 1000)
        var out: [Float] = []
        var pos = 0
        for (i, n) in [latency + 6000, 3000, 3000, 3000, count].enumerated() {
            let end = min(count, pos + n)
            s.eqEnabled = i % 2 == 0
            kernel.setSettings(s, sampleRate: Self.rate)
            out += Self.process(kernel, Array(x[pos..<end]), Array(x[pos..<end]), cycles: [512]).left
            pos = end
        }
        let natural = Self.maxStep(x, in: 0..<count)
        #expect(Self.maxStep(out, in: 0..<count) < natural * 1.1)
    }

    /// Minimum -> linear: the stages fade out, a pause while the FIR fills, the FIR fades in.
    /// Linear -> minimum: a crossfade. No click either way, and nothing in between plays twice.
    @Test func switchingThePhaseFadesWithoutClicks() {
        var s = Self.settings([EQBand(frequency: 800, gain: 4, q: 1)], linear: false)
        let kernel = Self.makeKernel(s)
        let latency = LinearPhaseEQ.latency(sampleRate: Self.rate), look = Self.lookahead(Self.rate)
        let count = 3 * latency + 20_000
        let x = Self.sine(count, frequency: 440)
        let switchAt = 10_000, backAt = 10_000 + 2 * latency
        var out = Self.process(kernel, Array(x[..<switchAt]), Array(x[..<switchAt])).left
        s.filterPhase = .linear
        kernel.setSettings(s, sampleRate: Self.rate)
        kernel.setLinearPhase(settings: s)
        out += Self.process(kernel, Array(x[switchAt..<backAt]), Array(x[switchAt..<backAt])).left
        s.filterPhase = .minimum
        kernel.setSettings(s, sampleRate: Self.rate)
        out += Self.process(kernel, Array(x[backAt...]), Array(x[backAt...])).left

        let natural = Self.maxStep(x, in: 0..<count) * Float(dbToLinear(4))
        #expect(Self.maxStep(out, in: 0..<count) < natural * 1.2, "no click")
        // The pause: silent from the end of the fade-out until the FIR's output starts.
        let quiet = (switchAt + Self.fade + look + 600)..<(switchAt + latency + look - 600)
        #expect(quiet.allSatisfy { abs(out[$0]) < 1e-3 })
        // Linear phase: the sine at the bell's gain (+4 dB at 440 Hz is about +1.6 dB), late.
        let gain = Float(BiquadCoefficients(band: s.bands[0], sampleRate: Self.rate).magnitude(at: 440, sampleRate: Self.rate)
                         * dbToLinear(s.effectivePreampDB()))
        let steady = (switchAt + latency + Self.fade + look + 100)..<(backAt + look)
        let worst = steady.reduce(Float(0)) { max($0, abs(out[$1] - gain * x[$1 - latency - look])) }
        #expect(worst < 2e-3, "linear phase plays the delayed sine: \(worst)")
        // Back to minimum phase after the crossfade: no latency (only the limiter's look-ahead).
        let after = (backAt + Self.fade + look + 2000)..<count
        let minimum = Self.makeKernel(s)
        let reference = Self.process(minimum, x, x).left
        #expect(after.allSatisfy { abs(out[$0] - reference[$0]) < 1e-3 })
    }

    /// Dragging a band: FIRs arrive faster than they crossfade (50 ms each). The output never
    /// jumps, and the last one plays.
    @Test func newFIRsCrossfadeWhileDragging() {
        var s = Self.settings([EQBand(frequency: 1000, gain: 0, q: 1)], preampDB: 0)
        let kernel = Self.makeKernel(s)
        let latency = LinearPhaseEQ.latency(sampleRate: Self.rate), look = Self.lookahead(Self.rate)
        let count = latency + 40_000
        let x = Self.sine(count, frequency: 1000)
        var out = Self.process(kernel, Array(x[..<(latency + 4000)]), Array(x[..<(latency + 4000)])).left
        var pos = out.count
        for step in 1...12 {
            s.bands[0].gain = Double(step)
            kernel.setSettings(s, sampleRate: Self.rate)
            kernel.setLinearPhase(settings: s)
            out += Self.process(kernel, Array(x[pos..<(pos + 800)]), Array(x[pos..<(pos + 800)])).left
            pos += 800
            kernel.collectReleasedSets()
        }
        out += Self.process(kernel, Array(x[pos...]), Array(x[pos...])).left
        let natural = Self.maxStep(x, in: 0..<count) * Float(dbToLinear(12))
        #expect(Self.maxStep(out, in: 0..<count) < natural * 1.1)
        let gain = Float(dbToLinear(12))
        let last = (count - 3000)..<count
        #expect(last.allSatisfy { abs(out[$0] - gain * x[$0 - latency - look]) < 2e-3 })
    }

    /// Until its FIR is there (it is designed off the main thread), linear phase plays the
    /// stages; a reconfigure for another rate drops the FIR and goes back to them.
    @Test func playsTheStagesUntilThereIsAFIR() {
        let s = Self.settings([EQBand(frequency: 1000, gain: 6, q: 1)])
        let kernel = DSPKernel()
        kernel.configure(sampleRate: Self.rate, layout: KernelLayout(sourceBufferIndices: [0], leftBuffer: 0, leftChannel: 0, rightBuffer: 0, rightChannel: 1))
        kernel.setSettings(s, sampleRate: Self.rate)
        let x = Self.sine(6000, frequency: 1000)
        let minimum = Self.process(Self.makeKernel(Self.settings(s.bands, linear: false)), x, x).left
        let waiting = Self.process(kernel, x, x).left
        #expect(zip(waiting, minimum).allSatisfy { $0 == $1 })

        kernel.setLinearPhase(settings: s)
        #expect(kernel.linearPhaseSpec != nil)
        kernel.configure(sampleRate: 44_100, layout: KernelLayout(sourceBufferIndices: [0], leftBuffer: 0, leftChannel: 0, rightBuffer: 0, rightChannel: 1))
        #expect(kernel.linearPhaseSpec == nil, "the app has to send a FIR for the new rate")
        // One made for 48 kHz is refused.
        kernel.setLinearPhase(LinearPhaseEQ.prepare(LinearPhaseEQ.Spec(settings: s, sampleRate: Self.rate)))
        #expect(kernel.linearPhaseSpec == nil)
        kernel.setSettings(s, sampleRate: 44_100)
        let reference = Self.process(Self.makeKernel(Self.settings(s.bands, linear: false), rate: 44_100), x, x).left
        // (After the first buffers, which ramp the preamp from different values.)
        let out = Self.process(kernel, x, x).left
        #expect((2000..<6000).allSatisfy { abs(out[$0] - reference[$0]) < 1e-5 })
    }

    /// Removing the FIR (switching to minimum phase frees it) before it faded in, or while it
    /// plays: back to the stages, without a click.
    @Test func removingTheFIRReturnsToTheStages() {
        let s = Self.settings([EQBand(frequency: 500, gain: 5, q: 1)])
        let latency = LinearPhaseEQ.latency(sampleRate: Self.rate)
        let x = Self.sine(latency + 30_000, frequency: 300)
        let reference = Self.process(Self.makeKernel(Self.settings(s.bands, linear: false)), x, x).left
        for removeAt in [2000, latency + 8000] {
            let kernel = Self.makeKernel(s)
            var out = Self.process(kernel, Array(x[..<removeAt]), Array(x[..<removeAt])).left
            kernel.setLinearPhase(nil)
            #expect(kernel.linearPhaseSpec == nil)
            out += Self.process(kernel, Array(x[removeAt...]), Array(x[removeAt...])).left
            kernel.collectReleasedSets()
            let natural = Self.maxStep(reference, in: 0..<reference.count)
            #expect(Self.maxStep(out, in: 0..<out.count) < natural * 1.2, "removed at \(removeAt)")
            let after = (removeAt + Self.fade + 3000)..<x.count
            #expect(after.allSatisfy { abs(out[$0] - reference[$0]) < 1e-3 }, "removed at \(removeAt)")
        }
    }

    /// The pre-EQ spectrum is taken from the delayed input, so it lines up with the output.
    @Test func inputSpectrumLinesUpWithTheOutput() {
        let kernel = Self.makeKernel(Self.settings([EQBand(frequency: 1000, gain: 3, q: 1)]))
        let latency = LinearPhaseEQ.latency(sampleRate: Self.rate)
        let count = latency + 10_000
        let x = (0..<count).map { Float($0 % 1000) / 1000 * 0.5 }   // a ramp: every value tells its time
        _ = Self.process(kernel, x, x)
        var pre = [Float](repeating: 0, count: 256)
        kernel.copyAnalyzer(post: false, into: &pre)
        for i in 0..<256 { #expect(pre[i] == x[count - 256 + i - latency]) }
    }

    /// The other channels of a multichannel stream are delayed with the processed pair.
    @Test func passthroughChannelsKeepTheirTiming() {
        let s = Self.settings([], preampDB: 0)
        let kernel = DSPKernel()
        kernel.configure(sampleRate: Self.rate, layout: KernelLayout(sourceBufferIndices: [0], sourceLeftChannel: 0, sourceRightChannel: 1,
                                                                  leftBuffer: 0, leftChannel: 0, rightBuffer: 0, rightChannel: 1, passthrough: true))
        kernel.setSettings(s, sampleRate: Self.rate)
        kernel.setLinearPhase(settings: s)
        let latency = LinearPhaseEQ.latency(sampleRate: Self.rate), look = Self.lookahead(Self.rate)
        let frames = 512, cycles = (latency + Self.fade) / frames + 8
        let channels = 6
        let inList = AudioBufferList.allocate(maximumBuffers: 1)
        let outList = AudioBufferList.allocate(maximumBuffers: 1)
        defer { free(inList.unsafeMutablePointer); free(outList.unsafeMutablePointer) }
        func value(_ t: Int, _ c: Int) -> Float { Float((t * 7 + c * 13) % 101) / 101 - 0.5 }
        var output: [[Float]] = Array(repeating: [], count: channels)
        for cycle in 0..<cycles {
            var src = [Float](repeating: 0, count: frames * channels)
            for i in 0..<frames { for c in 0..<channels { src[i * channels + c] = value(cycle * frames + i, c) } }
            var out = [Float](repeating: 0, count: frames * channels)
            src.withUnsafeMutableBytes { sr in
                out.withUnsafeMutableBytes { o in
                    inList[0] = AudioBuffer(mNumberChannels: UInt32(channels), mDataByteSize: UInt32(sr.count), mData: sr.baseAddress)
                    outList[0] = AudioBuffer(mNumberChannels: UInt32(channels), mDataByteSize: UInt32(o.count), mData: o.baseAddress)
                    var ts = AudioTimeStamp()
                    _ = tl_ioproc(0, &ts, inList.unsafePointer, &ts, outList.unsafeMutablePointer, &ts, kernel.clientData)
                }
            }
            for c in 0..<channels { output[c] += (0..<frames).map { out[$0 * channels + c] } }
        }
        let end = cycles * frames
        for t in (end - 300)..<end {
            #expect(abs(output[0][t] - value(t - latency - look, 0)) < 1e-5)
            for c in 2..<channels { #expect(abs(output[c][t] - value(t - latency, c)) < 1e-6, "channel \(c + 1)") }
        }
    }
}
