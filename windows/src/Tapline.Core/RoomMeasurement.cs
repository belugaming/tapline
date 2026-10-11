namespace Tapline.Core;

/// <summary>Exponential sine sweep measurement (Farina): play <see cref="Signal"/>, record the result,
/// deconvolve with <see cref="Inverse"/> to get the impulse response of speakers + room.</summary>
public sealed class SineSweep
{
    public double SampleRate { get; }
    public double Duration { get; }
    public double StartFrequency { get; }
    public double EndFrequency { get; }
    public float[] Signal { get; }
    /// <summary>Time-reversed sweep with a -6 dB/octave envelope, scaled so that
    /// <c>Signal ⊛ Inverse</c> has unity gain (0 dB at 1 kHz).</summary>
    public float[] Inverse { get; }

    /// <summary>Top of a sweep played at one sample rate and recorded at another. The played sweep and
    /// the reference that deconvolves the recording must cover the same range, or the impulse
    /// response smears and the measured response comes out wrong.</summary>
    public static double EndFrequencyFor(double playbackRate, double recordingRate, double limit = 20_000) =>
        Math.Min(limit, 0.45 * Math.Min(playbackRate, recordingRate));

    public SineSweep(double sampleRate, double duration = 8, double startFrequency = 20, double endFrequency = 20_000, float amplitude = 0.25f)
    {
        SampleRate = sampleRate;
        Duration = duration;
        StartFrequency = startFrequency;
        double f2 = Math.Min(endFrequency, sampleRate * 0.45);
        EndFrequency = f2;
        int n = (int)(duration * sampleRate);
        double rate = Math.Log(f2 / startFrequency);
        double k = 2 * Math.PI * startFrequency * duration / rate;

        var sweep = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / sampleRate;
            sweep[i] = (float)Math.Sin(k * (Math.Exp(t / duration * rate) - 1));
        }
        // 20 ms raised-cosine fades so the start and end do not click.
        int fade = Math.Min(n / 4, (int)(0.02 * sampleRate));
        for (int i = 0; i < fade; i++)
        {
            float g = (float)(0.5 - 0.5 * Math.Cos(Math.PI * i / fade));
            sweep[i] *= g;
            sweep[n - 1 - i] *= g;
        }

        var inverse = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / sampleRate;
            inverse[i] = sweep[n - 1 - i] * (float)Math.Exp(-t / duration * rate);
        }
        // Normalise in the frequency domain: sweep ⊛ inverse reads 0 dB at 1 kHz.
        var identity = Convolve(sweep, inverse);
        double level = RoomResponse.FromImpulse(identity, sampleRate, grid: [1000])?.Levels[0] ?? 0;
        float scale = (float)Math.Pow(10, -level / 20);
        for (int i = 0; i < n; i++) inverse[i] *= scale;

        for (int i = 0; i < n; i++) sweep[i] *= amplitude;
        Signal = sweep;
        for (int i = 0; i < n; i++) inverse[i] /= amplitude;
        Inverse = inverse;
    }

    /// <summary>Impulse response of whatever the sweep went through.</summary>
    public float[] ImpulseResponse(ReadOnlySpan<float> recording) => Convolve(recording, Inverse);

    /// <summary>Linear convolution via FFT.</summary>
    public static float[] Convolve(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int outCount = a.Length + b.Length - 1;
        if (outCount <= 0) return [];
        int n = Math.Max(2, Fft.NextPowerOfTwo(outCount));
        var (ar, ai) = Fft.RealForward(a, n);
        var (br, bi) = Fft.RealForward(b, n);
        for (int k = 0; k < ar.Length; k++)
        {
            double re = ar[k] * br[k] - ai[k] * bi[k];
            double im = ar[k] * bi[k] + ai[k] * br[k];
            ar[k] = re;
            ai[k] = im;
        }
        var y = Fft.RealInverse(ar, ai, n);
        var result = new float[outCount];
        for (int i = 0; i < outCount; i++) result[i] = (float)y[i];
        return result;
    }
}

public static class RoomResponse
{
    /// <summary>Magnitude response of an impulse response, windowed around its main peak.
    /// A long window (e.g. 500 ms) resolves room modes down to a few hertz.
    /// <paramref name="grid"/> defaults to <see cref="EqFitter.Grid"/>.</summary>
    public static FrequencyResponse? FromImpulse(ReadOnlySpan<float> ir, double sampleRate, double windowSeconds = 0.5,
                                                 IReadOnlyList<double>? grid = null)
    {
        grid ??= EqFitter.Grid;
        if (ir.Length == 0) return null;
        int peakIndex = 0;
        for (int i = 1; i < ir.Length; i++)
            if (Math.Abs(ir[i]) > Math.Abs(ir[peakIndex])) peakIndex = i;
        int pre = (int)(0.002 * sampleRate);
        int start = Math.Max(0, peakIndex - pre);
        int length = Math.Min(ir.Length - start, (int)(windowSeconds * sampleRate));
        if (length <= 64) return null;
        var segment = ir.Slice(start, length).ToArray();
        // Fade in over the pre-delay and fade out over the last quarter.
        int fadeIn = Math.Max(1, peakIndex - start);
        for (int i = 0; i < Math.Min(fadeIn, length); i++) segment[i] *= (float)(0.5 - 0.5 * Math.Cos(Math.PI * i / fadeIn));
        int fadeOut = length / 4;
        for (int i = 0; i < fadeOut; i++)
            segment[length - 1 - i] *= (float)(0.5 - 0.5 * Math.Cos(Math.PI * i / fadeOut));

        int log2n = Math.Max(16, (int)Math.Ceiling(Math.Log2(length)));
        int n = 1 << log2n;
        var (re, im) = Fft.RealForward(segment, n);
        var power = new double[n / 2];
        for (int b = 0; b < n / 2; b++) power[b] = re[b] * re[b] + im[b] * im[b];
        double binHz = sampleRate / n;
        // Power average over the bins each grid point covers (1/48 octave or one bin).
        var levels = new double[grid.Count];
        for (int g = 0; g < levels.Length; g++)
        {
            double f = grid[g];
            int lo = Math.Min(n / 2 - 1, Math.Max(1, (int)(f * Math.Pow(2, -1.0 / 96) / binHz)));
            int hi = Math.Min(n / 2 - 1, Math.Max(lo, (int)(f * Math.Pow(2, 1.0 / 96) / binHz)));
            double sum = 0;
            for (int b = lo; b <= hi; b++) sum += power[b];
            double p = sum / (hi - lo + 1);
            levels[g] = 10 * Math.Log10(Math.Max(p, 1e-20));
        }
        return new FrequencyResponse([.. grid], levels);
    }

    /// <summary>Power average of several measurements (e.g. a few listening positions).</summary>
    public static FrequencyResponse? Average(IReadOnlyList<FrequencyResponse> responses)
    {
        if (responses.Count == 0) return null;
        var grid = responses[0].Frequencies;
        var mean = new double[grid.Length];
        for (int i = 0; i < grid.Length; i++)
        {
            double p = 0;
            foreach (var r in responses) p += Math.Pow(10, r.LevelAt(grid[i]) / 10);
            p /= responses.Count;
            mean[i] = 10 * Math.Log10(Math.Max(p, 1e-20));
        }
        return new FrequencyResponse([.. grid], mean);
    }

    /// <summary>Subtracts a microphone calibration curve (e.g. a miniDSP UMIK-1 file).</summary>
    public static FrequencyResponse ApplyCalibration(FrequencyResponse response, FrequencyResponse calibration)
    {
        var levels = new double[response.Frequencies.Length];
        for (int i = 0; i < levels.Length; i++) levels[i] = response.Levels[i] - calibration.LevelAt(response.Frequencies[i]);
        return new FrequencyResponse([.. response.Frequencies], levels);
    }

    /// <summary>The speakers' low-frequency limit: the lowest frequency from which the
    /// third-octave smoothed response stays within 6 dB of the midrange for at
    /// least half an octave. Below it, boosting only wastes amplifier power.</summary>
    public static double LowFrequencyLimit(FrequencyResponse response)
    {
        var g = EqFitter.GridValues;
        var smooth = Smoothing.FractionalOctave(response.Resampled(g), g, _ => 1.0 / 3);
        var mids = Enumerable.Range(0, g.Length).Where(i => g[i] >= 200 && g[i] <= 2000).Select(i => smooth[i]).Order().ToArray();
        if (mids.Length == 0) return 20;
        double floor = mids[mids.Length / 2] - 6;
        for (int i = 0; i < g.Length; i++)
        {
            if (g[i] >= 200) continue;
            bool holds = true;
            for (int j = 0; j < g.Length; j++)
                if (g[j] >= g[i] && g[j] <= g[i] * 1.41 && !(smooth[j] >= floor)) { holds = false; break; }
            if (holds) return g[i];
        }
        return 200;
    }

    /// <summary>A straight target: 0 dB, optionally tilted (dB per octave around 1 kHz).</summary>
    public static FrequencyResponse FlatTarget(double tiltDbPerOctave = 0)
    {
        var grid = EqFitter.GridValues;
        return new FrequencyResponse([.. grid], grid.Select(f => tiltDbPerOctave * Math.Log2(f / 1000)).ToArray());
    }
}

/// <summary>A stereo measurement in one recording: a full sweep on the left speaker,
/// one on the right, then short chirps left, right, left. The chirps give the
/// difference in arrival time between the speakers; using the midpoint of the
/// two left chirps cancels the clock drift between two separate audio
/// interfaces (one playing, one recording).</summary>
public sealed class StereoSweepPlan
{
    public double SampleRate { get; }
    public SineSweep Sweep { get; }
    public SineSweep Chirp { get; }

    public const double PreRoll = 0.6;
    public const double Gap = 1.5;
    public const double ChirpSeconds = 0.25;
    public const double ChirpSpacing = 0.5;
    public const double Tail = 0.8;

    /// <param name="endFrequency">The top of the sweeps, the same for the played signal and the analysis
    /// (see <see cref="SineSweep.EndFrequencyFor"/>).</param>
    public StereoSweepPlan(double sampleRate, double sweepSeconds = 8, double endFrequency = 20_000)
    {
        SampleRate = sampleRate;
        Sweep = new SineSweep(sampleRate, sweepSeconds, endFrequency: endFrequency);
        // Above the subwoofer, where the direct sound gives a sharp peak.
        Chirp = new SineSweep(sampleRate, ChirpSeconds, startFrequency: 500, endFrequency: Math.Min(16_000, endFrequency));
    }

    // Start times in seconds.
    public double LeftStart => PreRoll;
    public double RightStart => LeftStart + Sweep.Duration + Gap;
    public IReadOnlyList<double> ChirpStarts
    {
        get
        {
            double first = RightStart + Sweep.Duration + Gap;
            return [first, first + 1 * ChirpSpacing, first + 2 * ChirpSpacing];
        }
    }
    public double Duration => ChirpStarts[2] + ChirpSeconds + Tail;

    /// <summary>Left and right output signals.</summary>
    public (float[] Left, float[] Right) Render()
    {
        int count = (int)Swift.Rounded(Duration * SampleRate);
        var left = new float[count];
        var right = new float[count];
        void Place(float[] signal, double seconds, float[] buffer)
        {
            int start = (int)Swift.Rounded(seconds * SampleRate);
            for (int i = 0; i < signal.Length && start + i < count; i++) buffer[start + i] = signal[i];
        }
        var chirps = ChirpStarts;
        Place(Sweep.Signal, LeftStart, left);
        Place(Sweep.Signal, RightStart, right);
        Place(Chirp.Signal, chirps[0], left);
        Place(Chirp.Signal, chirps[1], right);
        Place(Chirp.Signal, chirps[2], left);
        return (left, right);
    }

    public sealed class Analysis
    {
        public required FrequencyResponse Left { get; init; }
        public required FrequencyResponse Right { get; init; }
        /// <summary>Right arrival minus left arrival, seconds (positive: the right speaker is farther).</summary>
        public double? ArrivalDifference { get; init; }
        /// <summary>True when the speakers have opposite polarity, null when unclear.</summary>
        public bool? OppositePolarity { get; init; }
        public double SnrLeftDb { get; init; }
        public double SnrRightDb { get; init; }
        public double PeakDb { get; init; }
    }

    /// <summary>Analyses a mono microphone recording at <paramref name="inputRate"/> that started before playback.
    /// <paramref name="outputRate"/> is the rate the plan was played at, when it differs from the microphone's.</summary>
    public static Analysis? Analyze(ReadOnlySpan<float> recording, double inputRate, double? outputRate = null, double sweepSeconds = 8, double windowSeconds = 0.5)
    {
        var end = SineSweep.EndFrequencyFor(outputRate ?? inputRate, inputRate);
        var plan = new StereoSweepPlan(inputRate, sweepSeconds, end);
        double fs = inputRate;
        int n = plan.Sweep.Signal.Length;
        var chirpStarts = plan.ChirpStarts;
        if (recording.Length <= (int)(chirpStarts[0] * fs)) return null;

        // Main sweeps: a sweep starting at t peaks at t * fs + n - 1 after deconvolution.
        var ir = plan.Sweep.ImpulseResponse(recording[..Math.Min(recording.Length, Math.Max(0, (int)((chirpStarts[0] - 0.2) * fs)))]);
        int leftExpected = (int)(plan.LeftStart * fs) + n - 1;
        if (PeakIndex(ir, leftExpected - (int)(0.05 * fs), leftExpected + (int)(1.0 * fs)) is not { } leftPeak) return null;
        int latency = leftPeak - leftExpected;
        int rightExpected = (int)(plan.RightStart * fs) + n - 1 + latency;
        if (PeakIndex(ir, rightExpected - (int)(0.04 * fs), rightExpected + (int)(0.04 * fs)) is not { } rightPeak) return null;

        FrequencyResponse? ResponseAround(int peak)
        {
            int lo = Math.Max(0, peak - (int)(0.05 * fs)), hi = Math.Min(ir.Length, peak + (int)((windowSeconds + 0.05) * fs));
            if (hi <= lo) return null;
            return RoomResponse.FromImpulse(ir.AsSpan(lo, hi - lo), fs, windowSeconds);
        }
        if (ResponseAround(leftPeak) is not { } left || ResponseAround(rightPeak) is not { } right) return null;

        // Chirps: arrival times with sub-sample precision.
        double? arrival = null;
        bool? polarity = null;
        int nc = plan.Chirp.Signal.Length;
        int chirpBase = (int)((chirpStarts[0] - 0.1) * fs);
        if (chirpBase < recording.Length)
        {
            int end = Math.Min(recording.Length, chirpBase + (int)((3 * ChirpSpacing + 0.5) * fs) + nc);
            var cir = plan.Chirp.ImpulseResponse(recording[chirpBase..end]);
            var peaks = new List<(double Position, float? Sign)>();
            foreach (double start in chirpStarts)
            {
                int expected = (int)(start * fs) - chirpBase + nc - 1 + latency;
                // Wide enough for a subwoofer peak having set the latency, or speakers a metre apart.
                if (PeakIndex(cir, expected - (int)(0.025 * fs), expected + (int)(0.025 * fs)) is not { } p) continue;
                peaks.Add((InterpolatedPeak(cir, p), ClearSign(cir, p, (int)(0.0005 * fs))));
            }
            if (peaks.Count == 3)
            {
                double mid = (peaks[0].Position + peaks[2].Position) / 2;
                arrival = (peaks[1].Position - mid) / fs;
                if (peaks[0].Sign is { } a && peaks[1].Sign is { } b && peaks[2].Sign is { } c && a == c)
                    polarity = a != b;
            }
        }

        static double Rms(ReadOnlySpan<float> rec, double fs, int latency, double lo, double hi)
        {
            int a = Math.Max(0, (int)(lo * fs) + latency), b = Math.Min(rec.Length, (int)(hi * fs) + latency);
            if (b <= a) return 1e-9;
            double sum = 0;
            for (int i = a; i < b; i++) sum += (double)rec[i] * rec[i];
            return Math.Sqrt(sum / (b - a));
        }
        double noise = Rms(recording, fs, latency, 0, PreRoll * 0.8);
        double snrL = 20 * Math.Log10(Math.Max(Rms(recording, fs, latency, plan.LeftStart, plan.LeftStart + sweepSeconds), 1e-9) / Math.Max(noise, 1e-9));
        double snrR = 20 * Math.Log10(Math.Max(Rms(recording, fs, latency, plan.RightStart, plan.RightStart + sweepSeconds), 1e-9) / Math.Max(noise, 1e-9));
        float peakSample = 0;
        foreach (float v in recording) peakSample = Math.Max(peakSample, Math.Abs(v));

        return new Analysis
        {
            Left = left,
            Right = right,
            ArrivalDifference = arrival,
            OppositePolarity = polarity,
            SnrLeftDb = snrL,
            SnrRightDb = snrR,
            PeakDb = 20 * Math.Log10(Math.Max(peakSample, 1e-9)),
        };
    }

    internal static int? PeakIndex(float[] x, int rangeLo, int rangeHi)
    {
        int lo = Math.Max(0, rangeLo), hi = Math.Min(x.Length, rangeHi);
        if (hi <= lo) return null;
        int best = lo;
        for (int i = lo; i < hi; i++)
            if (Math.Abs(x[i]) > Math.Abs(x[best])) best = i;
        return best;
    }

    /// <summary>Parabolic interpolation of |x| around index <paramref name="i"/>.</summary>
    internal static double InterpolatedPeak(float[] x, int i)
    {
        if (i <= 0 || i >= x.Length - 1) return i;
        double a = Math.Abs(x[i - 1]), b = Math.Abs(x[i]), c = Math.Abs(x[i + 1]);
        double d = a - 2 * b + c;
        if (Math.Abs(d) <= 1e-12) return i;
        return i + 0.5 * (a - c) / d;
    }

    /// <summary>+1 / -1 when the peak clearly dominates the opposite polarity nearby, else null.</summary>
    internal static float? ClearSign(float[] x, int i, int halfWidth)
    {
        int lo = Math.Max(0, i - halfWidth), hi = Math.Min(x.Length, i + halfWidth + 1);
        float main = x[i];
        float opposite = 0;
        for (int j = lo; j < hi; j++)
            if ((x[j] > 0) != (main > 0)) opposite = Math.Max(opposite, Math.Abs(x[j]));
        return Math.Abs(main) > 1.5f * opposite ? (main > 0 ? 1 : -1) : null;
    }
}
