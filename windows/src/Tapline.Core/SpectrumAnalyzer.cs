namespace Tapline.Core;

/// <summary>FFT spectrum analyzer that turns a block of time-domain samples into
/// log-frequency display points in dBFS.</summary>
public sealed class SpectrumAnalyzer
{
    public int FftSize { get; }
    public IReadOnlyList<double> DisplayFrequencies { get; }

    /// <summary>Display tilt in dB per octave around 1 kHz, so pink-ish music reads flat.</summary>
    public double TiltDbPerOctave
    {
        get => _tiltDbPerOctave;
        set
        {
            _tiltDbPerOctave = value;
            _sampleRate = 0;
        }
    }

    /// <summary>0 = no smoothing, closer to 1 = slower falloff.</summary>
    public float Decay { get; set; } = 0.82f;

    private double _tiltDbPerOctave = 3.0;
    private readonly double[] _displayFrequencies;
    private readonly double[] _window;
    private readonly float[] _magnitudes;
    private readonly float[] _smoothed;
    private (int Lo, int Hi, float Frac)[] _binRanges = [];
    private float[] _tilt = [];
    private double _sampleRate;
    private readonly double _powerScale;

    public SpectrumAnalyzer(int fftSize = 8192, int displayPoints = 320)
    {
        if (!Fft.IsPowerOfTwo(fftSize) || fftSize < 4) throw new ArgumentException("fftSize must be a power of two", nameof(fftSize));
        FftSize = fftSize;
        _window = Fft.HannWindow(fftSize, normalized: true);
        // A sine of amplitude A peaks at |X| = A/2 * sum(w).
        // Scale |X|^2 so that a full-scale sine reads 0 dBFS.
        double windowSum = _window.Sum();
        _powerScale = 4 / (windowSum * windowSum);
        _magnitudes = new float[fftSize / 2];
        _displayFrequencies = ResponseCurve.LogFrequencies(displayPoints);
        DisplayFrequencies = Array.AsReadOnly(_displayFrequencies);
        _smoothed = new float[displayPoints];
        Array.Fill(_smoothed, -120f);
    }

    private void Prepare(double sampleRate)
    {
        if (sampleRate == _sampleRate) return;
        _sampleRate = sampleRate;
        double binHz = sampleRate / FftSize;
        int n = _displayFrequencies.Length;
        _binRanges = new (int, int, float)[n];
        for (int i = 0; i < n; i++)
        {
            double f = _displayFrequencies[i];
            // Each display point covers the band halfway to its neighbours.
            double fLo = i == 0 ? f : Math.Sqrt(f * _displayFrequencies[i - 1]);
            double fHi = i == n - 1 ? f : Math.Sqrt(f * _displayFrequencies[i + 1]);
            int lo = Math.Max(1, (int)Math.Floor(fLo / binHz));
            int hi = Math.Min(FftSize / 2 - 1, Math.Max(lo, (int)Math.Ceiling(fHi / binHz)));
            _binRanges[i] = (lo, hi, (float)(f / binHz - Math.Floor(f / binHz)));
        }
        _tilt = _displayFrequencies.Select(f => (float)(_tiltDbPerOctave * Math.Log2(f / 1000))).ToArray();
    }

    /// <summary><paramref name="samples"/> must contain at least <see cref="FftSize"/> values; the newest are used.</summary>
    public float[] Process(ReadOnlySpan<float> samples, double sampleRate)
    {
        Prepare(sampleRate);
        if (samples.Length < FftSize) throw new ArgumentException($"At least {FftSize} samples are needed.", nameof(samples));
        int n = FftSize;
        var newest = samples[(samples.Length - n)..];
        var windowed = new double[n];
        for (int i = 0; i < n; i++) windowed[i] = newest[i] * _window[i];

        var (re, im) = Fft.RealForward(windowed, n);
        for (int b = 0; b < n / 2; b++) _magnitudes[b] = (float)(re[b] * re[b] + im[b] * im[b]);

        var output = new float[_binRanges.Length];
        for (int i = 0; i < _binRanges.Length; i++)
        {
            var r = _binRanges[i];
            float peak = 0;
            if (r.Hi > r.Lo)
            {
                for (int b = r.Lo; b <= r.Hi; b++)
                    if (_magnitudes[b] > peak) peak = _magnitudes[b];
            }
            else
            {
                // Low frequencies: interpolate between neighbouring bins.
                float a = _magnitudes[r.Lo], b = _magnitudes[Math.Min(r.Lo + 1, _magnitudes.Length - 1)];
                peak = a + (b - a) * r.Frac;
            }
            float db = (float)(10 * Math.Log10(Math.Max(peak * _powerScale, 1e-14))) + _tilt[i];
            float previous = _smoothed[i];
            float value = db > previous ? db : previous * Decay + db * (1 - Decay);
            _smoothed[i] = value;
            output[i] = value;
        }
        return output;
    }

    public void Reset() => Array.Fill(_smoothed, -120f);
}
