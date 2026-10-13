using System.Text.Json.Serialization;

namespace Tapline.Core;

/// <summary>Where the four responses came from in the imported file(s). Serialized as the Swift raw values.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<BinauralLayout>))]
public enum BinauralLayout
{
    /// <summary>HeSuVi's 14-channel layout, also Impulcifer's hesuvi.wav.</summary>
    Hesuvi14,
    /// <summary>HeSuVi's 7-channel layout: the left speakers only, mirrored for the right ones.</summary>
    Hesuvi7,
    /// <summary>Impulcifer's hrir.wav (16 channels).</summary>
    Impulcifer16,
    /// <summary>FL→left, FL→right, FR→left, FR→right.</summary>
    FourChannel,
    /// <summary>One stereo file per speaker.</summary>
    StereoPair,
    /// <summary>One stereo file of one speaker, mirrored for the other.</summary>
    MirroredStereo,
}

public enum BinauralImportError { UnsupportedChannelCount, Silent, SameSide, SampleRateMismatch, WrongFileCount }

public sealed class BinauralImportException(BinauralImportError error, int count = 0) : Exception(error.ToString())
{
    public BinauralImportError Error { get; } = error;
    /// <summary>The channel or file count for UnsupportedChannelCount and WrongFileCount.</summary>
    public int Count { get; } = count;
}

/// <summary>Impulse responses of a pair of speakers at ±30° as they arrive at the two ears:
/// what Virtual Speakers convolve the stereo signal with (port of BinauralIRs.swift).</summary>
public sealed class BinauralIrs
{
    /// <summary>The longest response used, in samples at the device rate (the macOS kernel's limit).</summary>
    public const int MaxLength = 65536;
    public const int FrontLeftToLeft = 0, FrontLeftToRight = 1, FrontRightToLeft = 2, FrontRightToRight = 3;

    public double SampleRate { get; }
    /// <summary>FL→left ear, FL→right ear, FR→left ear, FR→right ear, all the same length.</summary>
    public float[][] Responses { get; }
    public BinauralLayout Layout { get; }
    public int Length => Responses[0].Length;
    public double Duration => Length / SampleRate;

    public BinauralIrs(double sampleRate, IReadOnlyList<float[]> responses, BinauralLayout layout = BinauralLayout.FourChannel)
    {
        if (responses.Count != 4) throw new ArgumentException("four impulse responses", nameof(responses));
        var n = responses.Max(r => r.Length);
        SampleRate = sampleRate;
        Responses = responses.Select(r => r.Length == n ? (float[])r.Clone() : r.Concat(new float[n - r.Length]).ToArray()).ToArray();
        Layout = layout;
    }

    /// <summary>The front speaker pair of one WAV file: HeSuVi (14 or 7 channels), Impulcifer's
    /// hrir.wav (16), four channels in FL→L, FL→R, FR→L, FR→R order, or a stereo file of one
    /// speaker, which is mirrored for the other.</summary>
    public static BinauralIrs FromWav(WavFile wav)
    {
        var c = wav.Channels;
        var irs = c.Length switch
        {
            // HeSuVi: FL-L FL-R SL-L SL-R BL-L BL-R C-L FR-R FR-L SR-R SR-L BR-R BR-L C-R
            // (right-side speakers list the right ear first).
            14 => new BinauralIrs(wav.SampleRate, [c[0], c[1], c[8], c[7]], BinauralLayout.Hesuvi14),
            // The first seven of the above; the right speakers mirror the left ones.
            7 => new BinauralIrs(wav.SampleRate, [c[0], c[1], c[1], c[0]], BinauralLayout.Hesuvi7),
            // Impulcifer's hrir.wav: FL-L FL-R FR-L FR-R C-L C-R LFE-L LFE-R BL-L ...
            16 => new BinauralIrs(wav.SampleRate, [c[0], c[1], c[2], c[3]], BinauralLayout.Impulcifer16),
            4 => new BinauralIrs(wav.SampleRate, c, BinauralLayout.FourChannel),
            2 => Mirrored(wav),
            _ => throw new BinauralImportException(BinauralImportError.UnsupportedChannelCount, c.Length),
        };
        irs.Validate();
        return irs;
    }

    private static BinauralIrs Mirrored(WavFile wav)
    {
        var (_, left) = Speaker(wav.Channels);
        return new BinauralIrs(wav.SampleRate, [left[0], left[1], left[1], left[0]], BinauralLayout.MirroredStereo);
    }

    /// <summary>Two stereo files, one per speaker, in either order: the louder ear tells which is which.</summary>
    public static BinauralIrs FromStereoPair(WavFile a, WavFile b)
    {
        if (a.Channels.Length != 2) throw new BinauralImportException(BinauralImportError.UnsupportedChannelCount, a.Channels.Length);
        if (b.Channels.Length != 2) throw new BinauralImportException(BinauralImportError.UnsupportedChannelCount, b.Channels.Length);
        if (a.SampleRate != b.SampleRate) throw new BinauralImportException(BinauralImportError.SampleRateMismatch);
        var first = Speaker(a.Channels);
        var second = Speaker(b.Channels);
        if (first.IsLeft == second.IsLeft) throw new BinauralImportException(BinauralImportError.SameSide);
        var left = first.IsLeft ? a.Channels : b.Channels;
        var right = first.IsLeft ? b.Channels : a.Channels;
        var irs = new BinauralIrs(a.SampleRate, [left[0], left[1], right[0], right[1]], BinauralLayout.StereoPair);
        irs.Validate();
        return irs;
    }

    /// <summary>One file (any layout above) or two stereo files.</summary>
    public static BinauralIrs FromFiles(IReadOnlyList<WavFile> files) => files.Count switch
    {
        1 => FromWav(files[0]),
        2 => FromStereoPair(files[0], files[1]),
        _ => throw new BinauralImportException(BinauralImportError.WrongFileCount, files.Count),
    };

    /// <summary>A stereo response of one speaker: the ear on its side is the louder one. Returns the
    /// responses as a left speaker would have them (mirrored when it is a right speaker).</summary>
    private static (bool IsLeft, float[][] Left) Speaker(float[][] c)
    {
        var isLeft = Energy(c[0]) >= Energy(c[1]);
        return (isLeft, isLeft ? c : new[] { c[1], c[0] });
    }

    private void Validate()
    {
        static float Peak(float[] x) => x.Length == 0 ? 0 : x.Max(MathF.Abs);
        if (Peak(Responses[FrontLeftToLeft]) <= 1e-6f || Peak(Responses[FrontRightToRight]) <= 1e-6f)
            throw new BinauralImportException(BinauralImportError.Silent);
    }

    internal static double Energy(float[] x)
    {
        var e = 0.0;
        foreach (var v in x) e += (double)v * v;
        return e;
    }

    /// <summary>The four responses as a 4-channel WAV, the layout Tapline stores imported sets in.</summary>
    public WavFile ToWav() => new(SampleRate, Responses.Select(r => (float[])r.Clone()).ToArray());

    // MARK: - Preparing for playback

    /// <summary>Drops the silence before the first and after the last sound (keeping the delays
    /// between the four), then cuts what is left to <paramref name="maxLength"/> samples with a short fade-out.</summary>
    public BinauralIrs Trimmed(int maxLength = MaxLength)
    {
        var peak = Responses.Max(r => r.Length == 0 ? 0 : r.Max(MathF.Abs));
        if (peak <= 0 || Length == 0) return this;
        // Before the onset (-60 dB) keep 1 ms; after the end keep everything above -100 dB.
        var onset = Responses.Select(r => Array.FindIndex(r, v => MathF.Abs(v) > peak * 1e-3f)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var last = Responses.Select(r => Array.FindLastIndex(r, v => MathF.Abs(v) > peak * 1e-5f)).Where(i => i >= 0).DefaultIfEmpty(Length - 1).Max();
        var start = Math.Max(0, onset - (int)(SampleRate / 1000));
        var end = last + 1;
        var cut = false;
        if (end - start > maxLength)
        {
            end = start + maxLength;
            cut = true;
        }
        var output = Responses.Select(r => r[start..end]).ToArray();
        if (cut)
        {
            var fade = Math.Min((int)(0.005 * SampleRate), maxLength / 8);
            foreach (var r in output)
                for (var i = 0; i < fade; i++)
                    r[end - start - fade + i] *= (float)(0.5 + 0.5 * Math.Cos(Math.PI * (i + 1) / fade));
        }
        return new BinauralIrs(SampleRate, output, Layout);
    }

    /// <summary>Ready for playback at <paramref name="rate"/>: trimmed, cut (with a fade) to what fits
    /// at that rate, and resampled.</summary>
    public BinauralIrs Prepared(double rate)
    {
        var fits = rate > 0 ? (int)(MaxLength * SampleRate / rate) : MaxLength;
        return Trimmed(Math.Min(MaxLength, fits)).Resampled(rate);
    }

    /// <summary>Band-limited resampling (Kaiser-windowed sinc). Scales by the rate ratio so the
    /// filters keep their gain. Stops after <paramref name="maxLength"/> output samples.</summary>
    public BinauralIrs Resampled(double rate, int maxLength = MaxLength)
    {
        if (rate <= 0 || SampleRate <= 0 || Math.Abs(rate - SampleRate) < 1e-9) return this;
        var ratio = rate / SampleRate;
        var count = (int)Math.Min(maxLength, Math.Ceiling(Length * ratio));
        var resampler = new Resampler(ratio);
        return new BinauralIrs(rate, Responses.Select(r => resampler.Run(r, count)).ToArray(), Layout);
    }

    /// <summary>Gain that keeps the level about the same when the effect is switched on: the
    /// geometric mean of the power gains for a centre (mono) source and for wide, uncorrelated
    /// stereo, with equal weight per octave from 100 Hz to 8 kHz.</summary>
    public double LoudnessGain
    {
        get
        {
            if (Length == 0) return 1;
            var n = Math.Max(4096, Fft.NextPowerOfTwo(Length));
            var spectra = Responses.Select(r => Fft.RealForward(r, n)).ToArray();
            double mono = 0, wide = 0, weights = 0;
            for (var k = 1; k < n / 2; k++)
            {
                var f = k * SampleRate / n;
                if (f < 100 || f > 8000) continue;
                var w = 1 / f;
                double P(double re, double im) => re * re + im * im;
                var (ll, rl, lr, rr) = (spectra[FrontLeftToLeft], spectra[FrontRightToLeft], spectra[FrontLeftToRight], spectra[FrontRightToRight]);
                var leftEar = P(ll.Re[k] + rl.Re[k], ll.Im[k] + rl.Im[k]);
                var rightEar = P(lr.Re[k] + rr.Re[k], lr.Im[k] + rr.Im[k]);
                mono += w * (leftEar + rightEar) / 2;
                wide += w * (P(ll.Re[k], ll.Im[k]) + P(rl.Re[k], rl.Im[k]) + P(lr.Re[k], lr.Im[k]) + P(rr.Re[k], rr.Im[k])) / 2;
                weights += w;
            }
            if (weights <= 0 || mono <= 0 || wide <= 0) return 1;
            var gain = Math.Pow(mono / weights * (wide / weights), -0.25);
            return double.IsFinite(gain) ? Math.Clamp(gain, 0.05, 20) : 1;
        }
    }

    /// <summary>Kaiser-windowed sinc interpolation from a tabulated kernel.</summary>
    private sealed class Resampler
    {
        private const double StepsPerSample = 512;
        private readonly double ratio;
        private readonly double halfWidth;
        private readonly double[] table;

        public Resampler(double ratio)
        {
            this.ratio = ratio;
            // Cut off at 96% of the lower Nyquist frequency.
            var cutoff = Math.Min(1, ratio) * 0.96;
            halfWidth = 24 / cutoff;
            const double beta = 8;
            var norm = BesselI0(beta);
            // An impulse response gets `ratio` times as many samples: scale them down so the filter keeps its gain.
            var scale = 1 / ratio;
            table = new double[(int)(halfWidth * StepsPerSample) + 2];
            for (var i = 0; i < table.Length; i++)
            {
                var t = i / StepsPerSample;
                if (t >= halfWidth) continue;
                var x = cutoff * t;
                var sinc = x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                var u = t / halfWidth;
                table[i] = cutoff * sinc * BesselI0(beta * Math.Sqrt(1 - u * u)) / norm * scale;
            }
        }

        private double Kernel(double t)
        {
            var p = Math.Abs(t) * StepsPerSample;
            var i = (int)p;
            if (i + 1 >= table.Length) return 0;
            var f = p - i;
            return table[i] + f * (table[i + 1] - table[i]);
        }

        public float[] Run(float[] x, int count)
        {
            var output = new float[count];
            for (var n = 0; n < count; n++)
            {
                var t = n / ratio;
                var lo = Math.Max(0, (int)Math.Ceiling(t - halfWidth));
                var hi = Math.Min(x.Length - 1, (int)Math.Floor(t + halfWidth));
                var acc = 0.0;
                for (var k = lo; k <= hi; k++) acc += x[k] * Kernel(t - k);
                output[n] = (float)acc;
            }
            return output;
        }

        private static double BesselI0(double x)
        {
            double sum = 1, term = 1, q = x * x / 4;
            for (var k = 1; k < 64; k++)
            {
                term *= q / ((double)k * k);
                sum += term;
                if (term < sum * 1e-17) break;
            }
            return sum;
        }
    }
}
