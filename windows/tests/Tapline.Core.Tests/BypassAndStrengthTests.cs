using Tapline.Core;

namespace Tapline.Core.Tests;

// Port of Packages/TaplineCore/Tests/TaplineCoreTests/BypassAndStrengthTests.swift.
// The kernel tests (crossfade, clamping in the realtime path) exercise the macOS C kernel and are not ported.

public class LoudnessTests
{
    private const double Sr = 48_000;

    [Fact]
    public void PreampAloneIsItsOwnLoudnessChange()
    {
        var s = new DspSettings(preampDb: -6, autoPreamp: false);
        Assert.Equal(-6, s.EqLoudnessDb(), 9);
        Assert.Equal(-6, s.BypassGainDb(), 9);
        s.PreampDb = 0;
        Assert.Equal(0, s.EqLoudnessDb(), 9);
        s.Bands = [new EqBand(FilterType.Peak, 1000, 0, 1)];
        Assert.True(Math.Abs(s.EqLoudnessDb()) < 1e-6);
    }

    [Fact]
    public void BoostsAreLouderAndCutsQuieter()
    {
        var boost = ResponseCurve.LoudnessChangeDb([new EqBand(FilterType.Peak, 1000, 6, 1)], Sr);
        var cut = ResponseCurve.LoudnessChangeDb([new EqBand(FilterType.Peak, 1000, -6, 1)], Sr);
        Assert.InRange(boost, 0.8, 2);
        Assert.InRange(cut, -1.5, -0.4);
    }

    [Fact]
    public void TrebleCountsMoreThanDeepBass()
    {
        // K-weighting: about -1 dB at 100 Hz, +4 dB above 2 kHz.
        var bass = ResponseCurve.LoudnessChangeDb([new EqBand(FilterType.LowShelf, 105, 6, 0.71)], Sr);
        var treble = ResponseCurve.LoudnessChangeDb([new EqBand(FilterType.HighShelf, 4000, 6, 0.71)], Sr);
        Assert.InRange(bass, 0.3, 1.2);
        Assert.InRange(treble, 2.5, 4.5);
    }

    [Fact]
    public void CorrectionBypassesAtTheLoudnessOfTheEq()
    {
        var s = new DspSettings(bands:
        [
            new EqBand(FilterType.LowShelf, 105, 6.5, 0.7),
            new EqBand(FilterType.Peak, 160, -2.5, 0.6),
            new EqBand(FilterType.Peak, 2600, -2.9, 1.5),
            new EqBand(FilterType.Peak, 3200, 2.7, 3.5),
            new EqBand(FilterType.Peak, 5400, -3.6, 3.4),
            new EqBand(FilterType.HighShelf, 10_000, -1.6, 0.7),
        ]);
        var preamp = s.EffectivePreampDb();
        var curve = ResponseCurve.LoudnessChangeDb(s.ActiveBands, Sr);
        Assert.Equal(preamp + curve, s.EqLoudnessDb(), 9);
        Assert.InRange(s.BypassGainDb(), -8, -5);

        // Tone controls are part of the EQ too.
        s.Tone = s.Tone with { Air = 6 };
        Assert.True(s.EqLoudnessDb() > preamp + curve);
    }

    [Fact]
    public void LeftAndRightCountEqually()
    {
        var left = new EqBand(FilterType.Peak, 1000, 6, 1, channels: EqChannels.Left);
        var s = new DspSettings(preampDb: 0, autoPreamp: false, bands: [left]);
        var leftOnly = ResponseCurve.LoudnessChangeDb([left], Sr, EqChannels.Left);
        var expected = 10 * Math.Log10((Math.Pow(10, leftOnly / 10) + 1) / 2);
        Assert.Equal(expected, s.EqLoudnessDb(), 9);
        Assert.InRange(s.EqLoudnessDb(), 0.01, leftOnly - 0.01);
    }

    [Fact]
    public void BypassNeverBoosts()
    {
        var s = new DspSettings(preampDb: 6, autoPreamp: false, bands: [new EqBand(FilterType.Peak, 1000, 6, 1)]);
        Assert.True(s.EqLoudnessDb() > 6);
        Assert.Equal(0, s.BypassGainDb());
        Assert.Equal(0, DspSettings.BypassGainFromLoudness(double.NaN));
        Assert.Equal(DspSettings.PreampMin, DspSettings.BypassGainFromLoudness(-60));
    }
}

public class CorrectionStrengthTests
{
    private static readonly FrequencyResponse Flat = new([20, 1000, 20_000], [0, 0, 0]);

    private static readonly EqBand[] Truth =
    [
        new(FilterType.LowShelf, 105, 5, 0.7),
        new(FilterType.Peak, 180, -4, 1.0),
        new(FilterType.Peak, 2500, 4, 1.4),
        new(FilterType.Peak, 4500, -5, 3),
    ];

    private static double Rms(double[] a, double[] b, double[] f)
    {
        var idx = Enumerable.Range(0, f.Length).Where(i => f[i] >= 20 && f[i] <= 10_000).ToList();
        return Math.Sqrt(idx.Sum(i => (a[i] - b[i]) * (a[i] - b[i])) / idx.Count);
    }

    [Fact]
    public void FullStrengthIsTheDefault()
    {
        var a = EqFitter.Fit(FitterTests.Synthetic(Truth), Flat);
        var b = EqFitter.Fit(FitterTests.Synthetic(Truth), Flat, new EqFitter.Options { Strength = 1 });
        Assert.Equal(a.Bands.Select(x => (x.Type, x.Frequency, x.Gain, x.Q)), b.Bands.Select(x => (x.Type, x.Frequency, x.Gain, x.Q)));
        Assert.Equal(a.Target, a.FitTarget);
    }

    [Fact]
    public void PartialStrengthFitsTheBlendedTarget()
    {
        var full = EqFitter.Fit(FitterTests.Synthetic(Truth), Flat);
        var half = EqFitter.Fit(FitterTests.Synthetic(Truth), Flat, new EqFitter.Options { Strength = 0.5 });
        var f = half.Frequencies;

        // The blended target lies halfway between the measurement and the target...
        for (int i = 0; i < f.Length; i++)
            Assert.True(Math.Abs(half.FitTarget[i] - (half.Measurement[i] + 0.5 * (half.Target[i] - half.Measurement[i]))) < 1e-9);
        // ...and the EQ follows it as closely as the full correction follows the target.
        Assert.True(Rms(half.Predicted, half.FitTarget, f) < 0.6, $"fit error at half strength {Rms(half.Predicted, half.FitTarget, f)} dB");

        // Half the correction: the EQ curve is about half as deep everywhere.
        double[] probe = [40, 100, 180, 600, 2500, 4500, 8000];
        var eqFull = ResponseCurve.ResponseDb(full.Bands, probe, 48_000);
        var eqHalf = ResponseCurve.ResponseDb(half.Bands, probe, 48_000);
        for (int i = 0; i < probe.Length; i++)
            Assert.True(Math.Abs(eqHalf[i] - eqFull[i] / 2) < 0.8, $"{probe[i]} Hz: full {eqFull[i]}, half {eqHalf[i]}");

        // The deviation from the full target reflects the strength.
        Assert.Equal(full.ErrorBeforeDb, half.ErrorBeforeDb, 9);
        Assert.InRange(half.ErrorAfterDb, 0.35 * half.ErrorBeforeDb, 0.65 * half.ErrorBeforeDb);
        Assert.True(half.ErrorAfterDb > full.ErrorAfterDb);
    }

    [Fact]
    public void ZeroStrengthLeavesTheHeadphonesAlone()
    {
        var r = EqFitter.Fit(FitterTests.Synthetic(Truth), Flat, new EqFitter.Options { Strength = 0 });
        Assert.Empty(r.Bands);
        Assert.Equal(r.Measurement, r.Predicted);
        Assert.Equal(r.ErrorBeforeDb, r.ErrorAfterDb, 9);
    }

    [Fact]
    public void OutOfRangeStrengthIsClamped()
    {
        var over = EqFitter.Fit(FitterTests.Synthetic(Truth), Flat, new EqFitter.Options { Strength = 3 });
        Assert.Equal(over.Target, over.FitTarget);
        var nan = EqFitter.Fit(FitterTests.Synthetic(Truth), Flat, new EqFitter.Options { Strength = double.NaN });
        Assert.Equal(nan.Target, nan.FitTarget);
    }

    [Fact]
    public void PresetBandsScale()
    {
        var bell = new EqBand(FilterType.Peak, 1000, 6, 1);
        Assert.Equal(3, bell.Scaled(0.5).Gain);
        Assert.Equal(0, bell.Scaled(0).Gain);
        Assert.Equal(bell, bell.Scaled(1));
        Assert.Equal(6, bell.Scaled(2).Gain);
        Assert.Equal(6, bell.Scaled(double.NaN).Gain);
        Assert.Equal(-1, new EqBand(FilterType.LowShelf, 105, -4, 0.7).Scaled(0.25).Gain);
        var highPass = new EqBand(FilterType.HighPass, 30, 3, 0.7);
        Assert.Equal(highPass, highPass.Scaled(0.5));
    }
}
