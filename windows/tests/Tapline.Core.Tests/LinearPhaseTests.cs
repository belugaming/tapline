using System.Text.Json;
using Tapline.Core;

namespace Tapline.Core.Tests;

/// The linear-phase EQ's FIR (as LinearPhaseEQTests.swift): length, symmetry, how closely it follows
/// the biquads, and the files Equalizer APO convolves with.
public class LinearPhaseTests
{
    private const double Rate = 48_000;

    /// 20 Hz ... 20 kHz, with the bottom octave sampled finely enough for a Q 18 bell.
    private static readonly double[] Band = [.. ResponseCurve.LogFrequencies(160), .. Enumerable.Range(0, 160).Select(i => 16 + i * 0.0625)];

    /// Magnitude of a symmetric FIR at <paramref name="f"/>: h[c] + 2 sum h[c + m] cos(w m).
    private static double FirDb(float[] fir, double f)
    {
        var c = fir.Length / 2;
        var w = 2 * Math.PI * f / Rate;
        double a = fir[c], previous = 1, current = Math.Cos(w), twice = 2 * current;
        for (var m = 1; m < c; m++)
        {
            a += 2 * fir[c + m] * current;
            var next = twice * current - previous;
            previous = current;
            current = next;
        }
        return 20 * Math.Log10(Math.Max(Math.Abs(a), 1e-9));
    }

    /// Largest difference in dB between the FIR and the biquads where the biquads are above -40 dB.
    private static double WorstErrorDb(BiquadCoefficients[] stages, double gain, IEnumerable<double> frequencies)
    {
        var fir = LinearPhaseEq.Design(stages, gain, Rate);
        var worst = 0.0;
        foreach (var f in frequencies)
        {
            var target = 20 * Math.Log10(gain) + stages.Sum(s => s.MagnitudeDb(f, Rate));
            if (target > -40) worst = Math.Max(worst, Math.Abs(FirDb(fir, f) - target));
        }
        return worst;
    }

    [Theory]
    [InlineData(22_050, 32_768)]
    [InlineData(44_100, 65_536)]
    [InlineData(48_000, 65_536)]
    [InlineData(96_000, 131_072)]
    [InlineData(192_000, 262_144)]
    [InlineData(384_000, 262_144)]
    public void LengthFollowsTheSampleRate(double rate, int taps)
    {
        Assert.Equal(taps, LinearPhaseEq.Taps(rate));
        // Equalizer APO's convolver adds no latency of its own: the FIR's delay is all.
        Assert.Equal(taps / 2, LinearPhaseEq.Latency(rate));
    }

    [Fact]
    public void LatencyInMilliseconds()
    {
        Assert.Equal(682.67, LinearPhaseEq.LatencyMilliseconds(48_000), 2);
        Assert.Equal(743.04, LinearPhaseEq.LatencyMilliseconds(44_100), 2);
        Assert.Equal(682.67, LinearPhaseEq.LatencyMilliseconds(double.NaN), 2);
    }

    [Theory]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(96_000)]
    public void FirIsExactlySymmetric(double rate)
    {
        BiquadCoefficients[] stages =
        [
            new(new EqBand(FilterType.HighPass, 30, q: 0.71), rate),
            new(new EqBand(FilterType.Peak, 3000, 5, 2), rate),
            new(new EqBand(FilterType.HighShelf, 9000, -4, 0.7), rate),
        ];
        var fir = LinearPhaseEq.Design(stages, 0.7, rate);
        var c = fir.Length / 2;
        Assert.Equal(LinearPhaseEq.Taps(rate), fir.Length);
        Assert.Equal(0f, fir[0]);
        for (var m = 1; m < c; m++) Assert.True(fir[c + m] == fir[c - m], $"tap {m}");
        var peak = Enumerable.Range(0, fir.Length).MaxBy(i => Math.Abs(fir[i]));
        Assert.Equal(c, peak);
        Assert.All(fir, v => Assert.True(float.IsFinite(v)));
    }

    /// A typical curve with tone controls, a preamp and a band on one channel: within a hundredth
    /// of a dB of the biquads from 20 Hz to 20 kHz.
    [Fact]
    public void FirMatchesTheBiquadsAcrossTheBand()
    {
        var s = new DspSettings(preampDb: -3, autoPreamp: false, bands:
        [
            new EqBand(FilterType.HighPass, 24, q: 0.71),
            new EqBand(FilterType.LowShelf, 105, 4.2, 0.7),
            new EqBand(FilterType.Peak, 132, -3.5, 1.01),
            new EqBand(FilterType.Peak, 350, 2.1, 1.21),
            new EqBand(FilterType.Peak, 1875, 3.4, 1.8, channels: EqChannels.Left),
            new EqBand(FilterType.Peak, 5985, -4.8, 3.5),
            new EqBand(FilterType.HighShelf, 10_000, 2.5, 0.7),
            new EqBand(FilterType.Notch, 7000, q: 4, isEnabled: false),
        ], tone: new ToneSettings(bass: 2, air: -1.5));
        var spec = LinearPhaseFiles.Spec.For(s, 2, Rate);
        Assert.Equal(9, spec.Left.Length);
        Assert.Equal(8, spec.Right.Length);
        Assert.Equal(8, spec.Both.Length);
        Assert.Equal(Math.Pow(10, -3 / 20.0), spec.Gain, 12);
        Assert.True(WorstErrorDb(spec.Left, spec.Gain, Band) < 0.01);
        Assert.True(WorstErrorDb(spec.Right, spec.Gain, Band) < 0.01);
    }

    /// What 65,536 taps resolve at the bottom (the numbers measured on macOS, same design): the
    /// narrowest bells at 20 Hz lose some depth, everything an octave away stays exact.
    [Theory]
    [InlineData(20, 6, 18, 1.2)]
    [InlineData(20, -24, 18, 5.5)]
    [InlineData(20, 12, 4.3, 0.15)]
    [InlineData(40, -12, 10, 0.1)]
    [InlineData(40, 12, 10, 0.2)]
    public void DocumentsTheErrorAtTheBottom(double frequency, double gain, double q, double limit)
    {
        BiquadCoefficients[] stages = [new(new EqBand(FilterType.Peak, frequency, gain, q), Rate)];
        var error = WorstErrorDb(stages, 1, Band);
        Assert.True(error < limit, $"{frequency} Hz {gain} dB Q {q}: {error} dB");
        Assert.True(WorstErrorDb(stages, 1, Band.Where(f => f > frequency * 2 || f < frequency / 2)) < 0.01);
    }

    [Fact]
    public void FilesAreNamedByTheirContent()
    {
        var s = DspSettings.Flat;
        s.Bands = [new EqBand(FilterType.Peak, 1000, 4, 1)];
        var (files, reference) = LinearPhaseFiles.Build(LinearPhaseFiles.Spec.For(s, 2, Rate));
        // Left and right are the same curve: one file.
        Assert.Single(files);
        Assert.Equal(reference.Left, reference.Right);
        Assert.Null(reference.Others);
        Assert.StartsWith(LinearPhaseFiles.Folder + @"\", reference.Left);
        Assert.EndsWith("-48000.wav", reference.Left);
        var wav = WavFile.Parse(files[0].Data);
        Assert.Equal(Rate, wav.SampleRate);
        var spec = LinearPhaseFiles.Spec.For(s, 2, Rate);
        Assert.Equal(LinearPhaseEq.Design(spec.Left, spec.Gain, Rate), wav.Channels[0]);

        // The same curve again: the same name. Another curve: another name.
        Assert.Equal(reference, LinearPhaseFiles.Build(LinearPhaseFiles.Spec.For(s.Clone(), 2, Rate)).Reference);
        s.Bands[0] = s.Bands[0] with { Gain = 4.1 };
        Assert.NotEqual(reference.Left, LinearPhaseFiles.Build(LinearPhaseFiles.Spec.For(s, 2, Rate)).Reference.Left);
        // A surround device gets a file for its other channels too (here the same curve).
        var surround = LinearPhaseFiles.Build(LinearPhaseFiles.Spec.For(s, 6, Rate)).Reference;
        Assert.Equal(surround.Left, surround.Others);
    }

    [Fact]
    public void SpecsCompareByTheirCurve()
    {
        var s = DspSettings.Flat;
        s.Bands = [new EqBand(FilterType.Peak, 1000, 4, 1)];
        var a = LinearPhaseFiles.Spec.For(s, 2, Rate);
        var bypassed = s.Clone();
        bypassed.EqEnabled = false;
        bypassed.FilterPhase = FilterPhase.Linear;
        Assert.Equal(a, LinearPhaseFiles.Spec.For(bypassed, 2, Rate));
        Assert.Equal(a.GetHashCode(), LinearPhaseFiles.Spec.For(bypassed, 2, Rate).GetHashCode());
        Assert.NotEqual(a, LinearPhaseFiles.Spec.For(s, 6, Rate));
        Assert.NotEqual(a, LinearPhaseFiles.Spec.For(s, 2, 44_100));
    }

    [Fact]
    public void SettingsKeepThePhase()
    {
        var s = DspSettings.Flat;
        s.FilterPhase = FilterPhase.Linear;
        var json = JsonSerializer.Serialize(s, TaplineJson.Options);
        Assert.Contains("\"filterPhase\":\"linear\"", json);
        Assert.Equal(s, JsonSerializer.Deserialize<DspSettings>(json, TaplineJson.Options));
        Assert.Equal(FilterPhase.Linear, s.Clone().FilterPhase);
        Assert.NotEqual(DspSettings.Flat, s);
        // Saved before linear phase, or by a newer version with another mode: minimum phase, and
        // the rest of the settings are kept.
        Assert.Equal(FilterPhase.Minimum, JsonSerializer.Deserialize<DspSettings>("""{"preampDB": -2}""", TaplineJson.Options)!.FilterPhase);
        var future = JsonSerializer.Deserialize<DspSettings>("""{"preampDB": -2, "filterPhase": "hybrid", "autoPreamp": false}""", TaplineJson.Options)!;
        Assert.Equal(FilterPhase.Minimum, future.FilterPhase);
        Assert.Equal(-2, future.PreampDb);
        Assert.False(future.AutoPreamp);
        Assert.Equal(FilterPhase.Minimum, JsonSerializer.Deserialize<DspSettings>("""{"filterPhase": {"mode": 3}, "preampDB": -1}""", TaplineJson.Options)!.FilterPhase);
    }
}
