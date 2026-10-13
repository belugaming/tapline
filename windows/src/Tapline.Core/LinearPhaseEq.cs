using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tapline.Core;

/// <summary>How the EQ handles phase.</summary>
public enum FilterPhase
{
    /// <summary>Biquads, as in an analog EQ: no latency, and each band shifts the phase around it.</summary>
    Minimum,
    /// <summary>A symmetric FIR with the same magnitude: no phase shift, but the sound comes later.</summary>
    Linear,
}

/// <summary>Reads "minimum" and "linear"; anything else (a mode of a newer version) as Minimum,
/// so the rest of the device's settings stay readable, as on macOS.</summary>
public sealed class FilterPhaseConverter : JsonConverter<FilterPhase>
{
    public override FilterPhase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
            return FilterPhase.Minimum;
        }
        return reader.TokenType == JsonTokenType.String && reader.GetString() == "linear" ? FilterPhase.Linear : FilterPhase.Minimum;
    }

    public override void Write(Utf8JsonWriter writer, FilterPhase value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value == FilterPhase.Linear ? "linear" : "minimum");
}

/// <summary>The linear-phase EQ (port of LinearPhaseEQ.swift): a symmetric FIR with the magnitude of
/// the EQ's biquads (bands, tone controls and preamp), which Equalizer APO plays with its
/// <c>Convolution:</c> filter.</summary>
///
/// <remarks>Design: the magnitude of the biquad cascade is sampled at 4 x taps points, transformed into
/// its zero-phase impulse response, cut to <c>taps</c> samples around the centre with a Tukey window
/// (flat over the middle half, cosine tapers at the ends) and delayed by taps / 2. The length,
/// 1.25 s or more (65,536 taps at 44.1 and 48 kHz), is what resolves narrow bass bands: a one-octave
/// bell at 20 Hz stays within 0.1 dB of the biquads, and bells up to Q 10 and ±12 dB from 40 Hz
/// within 0.15 dB; Q 18 at 20 Hz is about 1 dB short at ±6 dB and 5 dB short at ±24 dB.</remarks>
public static class LinearPhaseEq
{
    public const int MinTaps = 4096, MaxTaps = 262_144;
    /// <summary>Share of the FIR, at each end, that the window tapers.</summary>
    private const double Taper = 0.5;
    /// <summary>Magnitude samples per FIR tap.</summary>
    private const int Oversampling = 4;

    /// <summary>Length of the FIR at <paramref name="sampleRate"/>: the smallest power of two covering
    /// 1.25 s, within <see cref="MinTaps"/> ... <see cref="MaxTaps"/> (as tl_linear_phase_taps on macOS).</summary>
    public static int Taps(double sampleRate)
    {
        if (!(sampleRate >= 8000) || !double.IsFinite(sampleRate)) sampleRate = 48_000;
        var taps = MinTaps;
        while (taps < 1.25 * sampleRate && taps < MaxTaps) taps <<= 1;
        return taps;
    }

    /// <summary>Samples of delay: the FIR's own, taps / 2. Equalizer APO's convolver adds none.</summary>
    public static int Latency(double sampleRate) => Taps(sampleRate) / 2;

    public static double LatencyMilliseconds(double sampleRate)
    {
        var rate = sampleRate >= 8000 && double.IsFinite(sampleRate) ? sampleRate : 48_000;
        return Latency(rate) / rate * 1000;
    }

    /// <summary>The biquads that play on <paramref name="channel"/> (Left or Right; null: the bands for
    /// every channel only), in the order of the kernel: the bands, clamped, then the tone controls.</summary>
    public static BiquadCoefficients[] PlayedStages(DspSettings s, EqChannels? channel, double sampleRate) =>
    [
        .. s.Bands.Take(DspSettings.MaxBands)
            .Where(b => b.IsEnabled && (channel is { } c ? b.Channels.Includes(c) : b.Channels == EqChannels.Both))
            .Select(b => new BiquadCoefficients(b.Clamped(), sampleRate)),
        .. s.Tone.Bands.Where(b => b.IsEnabled).Select(b => new BiquadCoefficients(b, sampleRate)),
    ];

    /// <summary>Magnitude of <paramref name="stages"/> times <paramref name="gain"/> at the bin
    /// frequencies k * sampleRate / count, k = 0 ... count / 2.</summary>
    internal static double[] Magnitude(IReadOnlyList<BiquadCoefficients> stages, double gain, int count)
    {
        var bins = count / 2 + 1;
        var cw = new double[bins];
        var c2w = new double[bins];
        for (var k = 0; k < bins; k++)
        {
            cw[k] = Math.Cos(2 * Math.PI * k / count);
            c2w[k] = 2 * cw[k] * cw[k] - 1;
        }
        var power = new double[bins];
        Array.Fill(power, gain * gain);
        foreach (var s in stages)
        {
            double n0 = s.B0 * s.B0 + s.B1 * s.B1 + s.B2 * s.B2, n1 = 2 * (s.B0 * s.B1 + s.B1 * s.B2), n2 = 2 * s.B0 * s.B2;
            double d0 = 1 + s.A1 * s.A1 + s.A2 * s.A2, d1 = 2 * (s.A1 + s.A1 * s.A2), d2 = 2 * s.A2;
            for (var k = 0; k < bins; k++)
            {
                var num = n0 + n1 * cw[k] + n2 * c2w[k];
                var den = d0 + d1 * cw[k] + d2 * c2w[k];
                power[k] *= Math.Max(num, 0) / Math.Max(den, 1e-30);
            }
        }
        for (var k = 0; k < bins; k++) power[k] = Math.Sqrt(power[k]);
        return power;
    }

    /// <summary>The FIR of one channel: <see cref="Taps"/> samples, symmetric about taps / 2 (both
    /// halves are the same numbers), tap 0 zero.</summary>
    public static float[] Design(IReadOnlyList<BiquadCoefficients> stages, double gain, double sampleRate)
    {
        int taps = Taps(sampleRate), half = taps / 2, count = taps * Oversampling;
        var target = Magnitude(stages, gain, count);
        // Zero-phase impulse response: the inverse transform of the real, even spectrum.
        var response = Fft.RealInverse(target, new double[target.Length], count);
        var fir = new float[taps];
        var flat = half * (1 - Taper);
        for (var n = 0; n < half; n++)
        {
            var w = n <= flat ? 1 : 0.5 + 0.5 * Math.Cos(Math.PI * (n - flat) / (half - flat));
            var v = (float)(response[n] * w);
            fir[half + n] = v;
            fir[half - n] = v;
        }
        fir[0] = 0;
        return fir;
    }
}

/// <summary>The FIR files linear phase needs in Equalizer APO's config folder: one mono 32-bit float
/// WAV per channel group (left, right, the other channels of a multichannel device), at the
/// device's sample rate, named by their content so that a new curve is a new file name (and
/// Equalizer APO reloads).</summary>
public static class LinearPhaseFiles
{
    /// <summary>Subfolder of Equalizer APO's config folder, as written in the configuration.</summary>
    public const string Folder = @"Tapline\LinearPhase";

    public sealed record FirFile(string Name, byte[] Data);

    /// <summary>What the files are made from. Equal specs make equal files.</summary>
    public sealed record Spec(double SampleRate, bool Stereo, bool Others, double Gain,
                              BiquadCoefficients[] Left, BiquadCoefficients[] Right, BiquadCoefficients[] Both)
    {
        /// <summary>The EQ of <paramref name="s"/> on a device with <paramref name="channels"/> channels,
        /// whether or not it is bypassed. A mono device plays the bands of the left channel, as with biquads.</summary>
        public static Spec For(DspSettings s, int channels, double sampleRate)
        {
            var rate = sampleRate >= 8000 && double.IsFinite(sampleRate) ? sampleRate : 48_000;
            return new Spec(rate, channels >= 2, channels > 2, Db.ToLinear(s.EffectivePreampDb(rate)),
                LinearPhaseEq.PlayedStages(s, EqChannels.Left, rate), LinearPhaseEq.PlayedStages(s, EqChannels.Right, rate),
                LinearPhaseEq.PlayedStages(s, null, rate));
        }

        public bool Equals(Spec? other) =>
            other is not null && SampleRate.Equals(other.SampleRate) && Stereo == other.Stereo && Others == other.Others
            && Gain.Equals(other.Gain) && Left.SequenceEqual(other.Left) && Right.SequenceEqual(other.Right) && Both.SequenceEqual(other.Both);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(SampleRate);
            hash.Add(Stereo);
            hash.Add(Others);
            hash.Add(Gain);
            foreach (var list in new[] { Left, Right, Both })
            {
                hash.Add(list.Length);
                foreach (var b in list) hash.Add(b);
            }
            return hash.ToHashCode();
        }
    }

    /// <summary>The files for <paramref name="spec"/> (identical FIRs share one) and what the
    /// configuration refers to. Takes a few tens of milliseconds per FIR: call it off the UI thread.</summary>
    public static (IReadOnlyList<FirFile> Files, ApoConfig.FirFiles Reference) Build(Spec spec)
    {
        var files = new List<FirFile>();
        string Write(BiquadCoefficients[] stages)
        {
            var data = new WavFile(spec.SampleRate, [LinearPhaseEq.Design(stages, spec.Gain, spec.SampleRate)]).Encode();
            var name = $"{Convert.ToHexString(SHA256.HashData(data))[..16].ToLowerInvariant()}-{(int)Math.Round(spec.SampleRate)}.wav";
            if (!files.Any(f => f.Name == name)) files.Add(new FirFile(name, data));
            return Folder + @"\" + name;
        }
        var left = Write(spec.Left);
        var right = spec.Stereo ? (spec.Right.SequenceEqual(spec.Left) ? left : Write(spec.Right)) : left;
        var others = spec.Others ? (spec.Both.SequenceEqual(spec.Left) ? left : Write(spec.Both)) : null;
        return (files, new ApoConfig.FirFiles(left, right, others, LinearPhaseEq.Latency(spec.SampleRate)));
    }
}
