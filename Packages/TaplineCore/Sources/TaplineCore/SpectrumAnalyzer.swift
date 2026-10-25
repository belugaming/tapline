import Accelerate
import Foundation

/// FFT spectrum analyzer that turns a block of time-domain samples into
/// log-frequency display points in dBFS.
public final class SpectrumAnalyzer {
    public let fftSize: Int
    public let displayFrequencies: [Double]
    /// Display tilt in dB per octave around 1 kHz, so pink-ish music reads flat.
    public var tiltDBPerOctave: Double = 3.0 { didSet { sampleRate = 0 } }
    /// 0 = no smoothing, closer to 1 = slower falloff.
    public var decay: Float = 0.82

    private let log2n: vDSP_Length
    private let fft: FFTSetup
    private var window: [Float]
    private var real: [Float]
    private var imag: [Float]
    private var magnitudes: [Float]
    private var smoothed: [Float]
    /// The FFT bins each display point reads; nil for points at or above Nyquist.
    private var binRanges: [(lo: Int, hi: Int, frac: Float)?] = []
    private var tilt: [Float] = []
    private var sampleRate: Double = 0
    private let powerScale: Float

    public init(fftSize: Int = 8192, displayPoints: Int = 320) {
        precondition(fftSize.nonzeroBitCount == 1, "fftSize must be a power of two")
        self.fftSize = fftSize
        self.log2n = vDSP_Length(log2(Double(fftSize)))
        self.fft = vDSP_create_fftsetup(log2n, FFTRadix(kFFTRadix2))!
        self.window = [Float](repeating: 0, count: fftSize)
        vDSP_hann_window(&window, vDSP_Length(fftSize), Int32(vDSP_HANN_NORM))
        // A sine of amplitude A peaks at |X| = A/2 * sum(w); zrip returns 2X.
        // Scale |X_zrip|^2 so that a full-scale sine reads 0 dBFS.
        var windowSum: Float = 0
        vDSP_sve(window, 1, &windowSum, vDSP_Length(fftSize))
        self.powerScale = 1 / (windowSum * windowSum)
        self.real = [Float](repeating: 0, count: fftSize / 2)
        self.imag = [Float](repeating: 0, count: fftSize / 2)
        self.magnitudes = [Float](repeating: 0, count: fftSize / 2)
        self.displayFrequencies = ResponseCurve.logFrequencies(count: displayPoints)
        self.smoothed = [Float](repeating: -120, count: displayPoints)
    }

    deinit {
        vDSP_destroy_fftsetup(fft)
    }

    private func prepare(sampleRate: Double) {
        guard sampleRate != self.sampleRate else { return }
        self.sampleRate = sampleRate
        let binHz = sampleRate / Double(fftSize)
        let topBin = fftSize / 2 - 1
        let n = displayFrequencies.count
        binRanges = (0..<n).map { i in
            let f = displayFrequencies[i]
            // Below 40 kHz (AirPods in call mode run at 16 or 24 kHz) the top display points
            // lie above Nyquist and have no bins. Written so that a zero or NaN rate also
            // lands here, before Int() could trap on an infinite or NaN bin.
            guard binHz > 0, f / binHz < Double(topBin + 1) else { return nil }
            // Each display point covers the band halfway to its neighbours.
            let fLo = i == 0 ? f : sqrt(f * displayFrequencies[i - 1])
            let fHi = i == n - 1 ? f : sqrt(f * displayFrequencies[i + 1])
            let lo = max(1, Int(floor(fLo / binHz)))
            let hi = min(topBin, max(lo, Int(ceil(fHi / binHz))))
            return (lo, hi, Float(f / binHz - floor(f / binHz)))
        }
        tilt = displayFrequencies.map { Float(tiltDBPerOctave * log2($0 / 1000)) }
    }

    /// `samples` must contain at least `fftSize` values; the newest are used.
    public func process(_ samples: [Float], sampleRate: Double) -> [Float] {
        prepare(sampleRate: sampleRate)
        precondition(samples.count >= fftSize)
        let n = fftSize
        var windowed = [Float](repeating: 0, count: n)
        samples.withUnsafeBufferPointer { src in
            vDSP_vmul(src.baseAddress! + (samples.count - n), 1, window, 1, &windowed, 1, vDSP_Length(n))
        }

        real.withUnsafeMutableBufferPointer { rp in
            imag.withUnsafeMutableBufferPointer { ip in
                var split = DSPSplitComplex(realp: rp.baseAddress!, imagp: ip.baseAddress!)
                windowed.withUnsafeBufferPointer { wp in
                    wp.baseAddress!.withMemoryRebound(to: DSPComplex.self, capacity: n / 2) {
                        vDSP_ctoz($0, 2, &split, 1, vDSP_Length(n / 2))
                    }
                }
                vDSP_fft_zrip(fft, &split, 1, log2n, FFTDirection(FFT_FORWARD))
                vDSP_zvmags(&split, 1, &magnitudes, 1, vDSP_Length(n / 2))
            }
        }

        let scale = powerScale
        var out = [Float](repeating: -120, count: binRanges.count)
        for i in 0..<binRanges.count {
            // Nothing plays above Nyquist: leave the point at the floor.
            guard let r = binRanges[i] else {
                smoothed[i] = -120
                continue
            }
            var peak: Float = 0
            if r.hi > r.lo {
                for b in r.lo...r.hi where magnitudes[b] > peak { peak = magnitudes[b] }
            } else {
                // Low frequencies: interpolate between neighbouring bins.
                let a = magnitudes[r.lo], b = magnitudes[min(r.lo + 1, magnitudes.count - 1)]
                peak = a + (b - a) * r.frac
            }
            let db = 10 * log10(max(peak * scale, 1e-14)) + tilt[i]
            let previous = smoothed[i]
            let value = db > previous ? db : previous * decay + db * (1 - decay)
            smoothed[i] = value
            out[i] = value
        }
        return out
    }

    public func reset() {
        for i in smoothed.indices { smoothed[i] = -120 }
    }
}
