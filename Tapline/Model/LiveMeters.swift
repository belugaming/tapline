import Foundation
import Observation
import TaplineCore

/// High-rate visual data (meters, spectrum). Kept separate from AppModel so
/// only the views that draw it are invalidated 30-60 times a second.
@MainActor
@Observable
final class LiveMeters {
    var peakL: Float = 0
    var peakR: Float = 0
    var rmsL: Float = 0
    var rmsR: Float = 0
    var holdL: Float = 0
    var holdR: Float = 0
    var gainReductionDB: Float = 0
    var clipping = false
    var appPeaks: [String: Float] = [:]
    var spectrumPost: [Float] = []
    var spectrumPre: [Float] = []
    var spectrumFrequencies: [Double] = []
    var ioCycles: UInt64 = 0

    @ObservationIgnored private var lastClip = Date.distantPast
    @ObservationIgnored private var holdTimeL = Date.distantPast
    @ObservationIgnored private var holdTimeR = Date.distantPast

    /// Applies meter ballistics (fast attack, ~300 ms fall, 1.5 s peak hold).
    func ingest(_ m: KernelMeters, sourceKeys: [String], dt: Double) {
        let fall = Float(exp(-dt / 0.3))
        peakL = max(m.peakL, peakL * fall)
        peakR = max(m.peakR, peakR * fall)
        rmsL = max(m.rmsL, rmsL * fall)
        rmsR = max(m.rmsR, rmsR * fall)
        let now = Date()
        if m.peakL >= holdL || now.timeIntervalSince(holdTimeL) > 1.5 { holdL = m.peakL; holdTimeL = now }
        if m.peakR >= holdR || now.timeIntervalSince(holdTimeR) > 1.5 { holdR = m.peakR; holdTimeR = now }
        gainReductionDB = max(m.gainReductionDB, gainReductionDB * fall)
        if m.clippedSamples > 0 { lastClip = now }
        clipping = now.timeIntervalSince(lastClip) < 2
        ioCycles = m.ioCycles
        var peaks: [String: Float] = [:]
        for (i, key) in sourceKeys.enumerated() where i < m.sourcePeaks.count {
            peaks[key] = max(m.sourcePeaks[i], (appPeaks[key] ?? 0) * fall)
        }
        appPeaks = peaks
    }

    /// Synthetic data for screenshots (TAPLINE_SNAPSHOT_DIR).
    func fillDemo(frequencies: [Double], settings: DSPSettings) {
        spectrumFrequencies = frequencies
        let eq = ResponseCurve.responseDB(bands: settings.activeBands, frequencies: frequencies, sampleRate: 48_000)
        var seed: UInt64 = 0x9E37_79B9_7F4A_7C15
        func noise() -> Double {
            seed = seed &* 6364136223846793005 &+ 1442695040888963407
            return Double(seed >> 33) / Double(1 << 31) - 0.5
        }
        var pre: [Float] = []
        var post: [Float] = []
        var smooth = 0.0
        for (i, f) in frequencies.enumerated() {
            let octave = log2(f / 1000)
            let music = -34 - 1.2 * octave * octave + 7 * exp(-pow(log2(f / 70), 2)) + 4 * exp(-pow(log2(f / 2500), 2) * 2)
            let rolloff = f > 14_000 ? -(f - 14_000) / 400 : 0
            smooth = 0.6 * smooth + 0.4 * noise() * 9
            let level = music + rolloff + smooth
            pre.append(Float(level))
            post.append(Float(level + eq[i] + settings.effectivePreampDB()))
        }
        spectrumPre = pre
        spectrumPost = post
        peakL = 0.62; peakR = 0.58; rmsL = 0.31; rmsR = 0.29; holdL = 0.71; holdR = 0.66
        gainReductionDB = 0.8
    }

    func reset() {
        peakL = 0; peakR = 0; rmsL = 0; rmsR = 0; holdL = 0; holdR = 0
        gainReductionDB = 0
        clipping = false
        appPeaks = [:]
        spectrumPost = []
        spectrumPre = []
    }
}
