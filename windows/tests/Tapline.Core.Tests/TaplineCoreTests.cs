using System.Globalization;
using System.Text;
using Tapline.Core;
using Xunit.Abstractions;

namespace Tapline.Core.Tests;

// Port of Packages/TaplineCore/Tests/TaplineCoreTests/TaplineCoreTests.swift.
// KernelTests and deviceStreamSourcePassesOtherChannelsThrough exercise the macOS C kernel and are not ported.

public class BiquadTests
{
    private const double Sr = 48_000.0;

    [Fact]
    public void PeakHitsGainAtCenter()
    {
        var c = BiquadCoefficients.Design(FilterType.Peak, 1000, 6, 1, Sr);
        Assert.True(Math.Abs(c.MagnitudeDb(1000, Sr) - 6) < 0.01);
        Assert.True(Math.Abs(c.MagnitudeDb(20, Sr)) < 0.1);
    }

    [Fact]
    public void ShelvesReachTheirPlateau()
    {
        var low = BiquadCoefficients.Design(FilterType.LowShelf, 105, 4.2, 0.7, Sr);
        Assert.True(Math.Abs(low.MagnitudeDb(20, Sr) - 4.2) < 0.3);
        Assert.True(Math.Abs(low.MagnitudeDb(10_000, Sr)) < 0.05);
        var high = BiquadCoefficients.Design(FilterType.HighShelf, 8000, -3, 0.7, Sr);
        Assert.True(Math.Abs(high.MagnitudeDb(20_000, Sr) + 3) < 0.3);
    }

    [Fact]
    public void FiltersAttenuate()
    {
        var hp = BiquadCoefficients.Design(FilterType.HighPass, 100, 0, 0.707, Sr);
        Assert.True(hp.MagnitudeDb(20, Sr) < -20);
        Assert.True(Math.Abs(hp.MagnitudeDb(100, Sr) + 3) < 0.1);
        var notch = BiquadCoefficients.Design(FilterType.Notch, 1000, 0, 10, Sr);
        Assert.True(notch.MagnitudeDb(1000, Sr) < -60);
    }

    [Fact]
    public void AutoPreampCancelsBoost()
    {
        var s = new DspSettings();
        s.Bands = [new EqBand(FilterType.Peak, 1000, 6, 1)];
        Assert.True(Math.Abs(s.EffectivePreampDb() + 6) < 0.05);
        s.Bands = [new EqBand(FilterType.Peak, 1000, -6, 1)];
        Assert.True(s.EffectivePreampDb() == 0);
    }
}

public class FormatTests
{
    private const string Sample = """
        Preamp: -6.4 dB
        Filter 1: ON LSC Fc 105 Hz Gain 4.2 dB Q 0.70
        Filter 2: ON PK Fc 132 Hz Gain -10.8 dB Q 1.01
        Filter 3: OFF HSC Fc 10000 Hz Gain -1.5 dB Q 0.70
        Filter 4: ON LP Fc 18000 Hz
        """;

    [Fact]
    public void ParsesAutoEq()
    {
        var parsed = ParametricEqFormat.Parse(Sample);
        Assert.Equal(-6.4, parsed.PreampDb);
        Assert.Equal(4, parsed.Bands.Count);
        Assert.Equal(FilterType.LowShelf, parsed.Bands[0].Type);
        Assert.Equal(105, parsed.Bands[0].Frequency);
        Assert.Equal(4.2, parsed.Bands[0].Gain);
        Assert.Equal(-10.8, parsed.Bands[1].Gain);
        Assert.False(parsed.Bands[2].IsEnabled);
        Assert.Equal(FilterType.LowPass, parsed.Bands[3].Type);
    }

    [Fact]
    public void RoundTrips()
    {
        var parsed = ParametricEqFormat.Parse(Sample);
        var text = ParametricEqFormat.Export(parsed.Bands, parsed.PreampDb ?? 0);
        var again = ParametricEqFormat.Parse(text);
        Assert.Equal(parsed.Bands.Select(b => b.Type), again.Bands.Select(b => b.Type));
        Assert.Equal(parsed.Bands.Select(b => b.IsEnabled), again.Bands.Select(b => b.IsEnabled));
    }

    [Fact]
    public void RejectsGarbage()
    {
        var e = Assert.Throws<ParametricEqParseException>(() => ParametricEqFormat.Parse("hello"));
        Assert.Equal(ParametricEqParseError.NoFilters, e.Error);
    }
}

public class AnalyzerTests
{
    [Fact]
    public void FullScaleSineReadsZeroDb()
    {
        var analyzer = new SpectrumAnalyzer(fftSize: 8192, displayPoints: 320) { Decay = 0 };
        const double sr = 48_000.0;
        const double f = 1000.0;
        var samples = Enumerable.Range(0, 8192).Select(i => (float)Math.Sin(2 * Math.PI * f * i / sr)).ToArray();
        var output = analyzer.Process(samples, sr);
        int idx = 0;
        for (int i = 1; i < analyzer.DisplayFrequencies.Count; i++)
            if (Math.Abs(analyzer.DisplayFrequencies[i] - f) < Math.Abs(analyzer.DisplayFrequencies[idx] - f)) idx = i;
        Assert.True(Math.Abs(output[idx]) < 1.5, $"got {output[idx]} dB");
    }
}

public class FitterTests
{
    private static readonly FrequencyResponse Flat = new([20, 1000, 20_000], [0, 0, 0]);

    /// <summary>A "headphone" whose response is the inverse of some known filters.</summary>
    internal static FrequencyResponse Synthetic(IEnumerable<EqBand> filters)
    {
        var grid = ResponseCurve.LogFrequencies(600);
        var r = ResponseCurve.ResponseDb(filters, grid, 48_000);
        return new FrequencyResponse(grid, r.Select(x => 75 - x).ToArray());
    }

    [Fact]
    public void ParsesRewExport()
    {
        var text = """
            * Measurement data measured by REW V5.40
            * Format: Comma/Tab/Space delimited data
            * Freq(Hz) SPL(dB) Phase(degrees)
            20.000, 81.2, 10.0
            25.000 81.5 9.0
            """ + "\n" + string.Join("\n", Enumerable.Range(3, 18).Select(i =>
                string.Create(CultureInfo.InvariantCulture, $"{i * 1000.0}\t{70 + i * 0.1}\t0")));
        var fr = FrequencyResponse.Parse(text);
        Assert.NotNull(fr);
        Assert.Equal(20, fr.Frequencies[0]);
        Assert.Equal(81.2, fr.Levels[0]);
        Assert.Equal(20, fr.Frequencies.Length);
        Assert.True(Math.Abs(fr.LevelAt(22.36) - 81.35) < 0.05);
    }

    [Fact]
    public void RecoversKnownCorrection()
    {
        EqBand[] truth =
        [
            new(FilterType.LowShelf, 105, 5, 0.7),
            new(FilterType.Peak, 180, -4, 1.0),
            new(FilterType.Peak, 2500, 4, 1.4),
            new(FilterType.Peak, 4500, -5, 3),
        ];
        var result = EqFitter.Fit(Synthetic(truth), Flat);
        Assert.True(result.ErrorBeforeDb > 2.5);
        Assert.True(result.ErrorAfterDb < 0.6, $"after {result.ErrorAfterDb}");
        Assert.True(result.Bands.Count <= 10);
        Assert.All(result.Bands, b => Assert.True(Math.Abs(b.Gain) <= 18));
    }

    [Fact]
    public void KeepsBassUntouched()
    {
        EqBand[] truth = [new(FilterType.LowShelf, 100, -8, 0.7), new(FilterType.Peak, 3000, 5, 1)];
        var result = EqFitter.Fit(Synthetic(truth), Flat, new EqFitter.Options(keepBassBelow: 200));
        var eq = ResponseCurve.ResponseDb(result.Bands, [30, 60, 100], 48_000);
        Assert.True(eq.All(x => Math.Abs(x) < 1.0), $"bass moved: {string.Join(", ", eq)}");
        // Absolute level is arbitrary (preamp); compare the shape of the midrange correction.
        var eqMid = ResponseCurve.ResponseDb(result.Bands, [1000, 3000], 48_000);
        var truthMid = ResponseCurve.ResponseDb(truth, [1000, 3000], 48_000);
        Assert.True(Math.Abs((eqMid[1] - eqMid[0]) - (truthMid[1] - truthMid[0])) < 1.0,
                    $"eq {string.Join(", ", eqMid)} truth {string.Join(", ", truthMid)}");
    }

    [Fact]
    public void AppliesPreferences()
    {
        var result = EqFitter.Fit(Synthetic([]), Flat, new EqFitter.Options(bassAdjustDb: 6));
        var eq = ResponseCurve.ResponseDb(result.Bands, [30, 1000], 48_000);
        Assert.True(Math.Abs(eq[0] - 6) < 1.0, $"bass {eq[0]}");
        Assert.True(Math.Abs(eq[1]) < 0.7);
    }
}

public class TopLevelTests(ITestOutputHelper output)
{
    /// <summary>Manual check against real files:
    /// TAPLINE_FR_L=… TAPLINE_FR_R=… TAPLINE_TARGET=… dotnet test --filter RealData</summary>
    [Fact]
    public void RealData()
    {
        var l = Environment.GetEnvironmentVariable("TAPLINE_FR_L");
        var t = Environment.GetEnvironmentVariable("TAPLINE_TARGET");
        if (l is null || t is null) return;
        var left = FrequencyResponse.Parse(File.ReadAllText(l, Encoding.Latin1));
        Assert.NotNull(left);
        var rightPath = Environment.GetEnvironmentVariable("TAPLINE_FR_R");
        var right = rightPath is null ? null : FrequencyResponse.Parse(File.ReadAllText(rightPath, Encoding.Latin1));
        var measurement = FrequencyResponse.Average(right is null ? [left] : [left, right], EqFitter.Grid);
        Assert.NotNull(measurement);
        var target = FrequencyResponse.Parse(File.ReadAllText(t, Encoding.Latin1));
        Assert.NotNull(target);
        var start = DateTime.UtcNow;
        var r = EqFitter.Fit(measurement, target);
        output.WriteLine($"REAL fit in {(DateTime.UtcNow - start).TotalSeconds}s error {r.ErrorBeforeDb} -> {r.ErrorAfterDb}");
        output.WriteLine(ParametricEqFormat.Export(r.Bands, 0));
    }

    [Fact]
    public void TrebleIsOnlyBroadlyCorrected()
    {
        // A narrow 9 kHz coupler resonance must not produce a deep narrow cut.
        var grid = ResponseCurve.LogFrequencies(600);
        var resonance = ResponseCurve.ResponseDb([new EqBand(FilterType.Peak, 9000, 10, 4)], grid, 48_000);
        var measurement = new FrequencyResponse(grid, resonance.Select(x => 70 + x).ToArray());
        var result = EqFitter.Fit(measurement, new FrequencyResponse([20, 20_000], [0, 0]));
        var eq = ResponseCurve.ResponseDb(result.Bands, [9000], 48_000)[0];
        Assert.True(eq > -4, $"deep treble cut: {eq} dB, bands {string.Join("; ", result.Bands.Select(b => $"{b.Type} {(int)b.Frequency} {b.Gain} {b.Q}"))}");
    }
}

public class RoomTests
{
    /// <summary>Runs <paramref name="x"/> through biquads (transposed direct form II), like a room would colour it.</summary>
    internal static float[] Filter(float[] x, IEnumerable<EqBand> bands, double sampleRate)
    {
        var y = x.Select(v => (double)v).ToArray();
        foreach (var band in bands)
        {
            var c = new BiquadCoefficients(band, sampleRate);
            double z1 = 0, z2 = 0;
            for (int i = 0; i < y.Length; i++)
            {
                double input = y[i];
                double output = c.B0 * input + z1;
                z1 = c.B1 * input - c.A1 * output + z2;
                z2 = c.B2 * input - c.A2 * output;
                y[i] = output;
            }
        }
        return y.Select(v => (float)v).ToArray();
    }

    [Fact]
    public void SweepMeasuresARoom()
    {
        const double sr = 48_000.0;
        var sweep = new SineSweep(sr, duration: 3);
        EqBand[] room = [new(FilterType.Peak, 80, 6, 4), new(FilterType.Peak, 200, -4, 3)];
        // 30 ms of latency before the sound reaches the microphone, plus a tail.
        float[] recording = [.. new float[(int)(0.03 * sr)], .. Filter(sweep.Signal, room, sr), .. new float[(int)sr]];
        var ir = sweep.ImpulseResponse(recording);
        var fr = RoomResponse.FromImpulse(ir, sr);
        Assert.NotNull(fr);
        double reference = fr.LevelAt(1000);
        Assert.True(Math.Abs(fr.LevelAt(80) - reference - 6) < 1.0, $"80 Hz: {fr.LevelAt(80) - reference}");
        Assert.True(Math.Abs(fr.LevelAt(200) - reference + 4) < 1.0, $"200 Hz: {fr.LevelAt(200) - reference}");
        Assert.True(Math.Abs(fr.LevelAt(3000) - reference) < 0.5);
        Assert.True(Math.Abs(reference) < 1.0, $"unity gain expected, got {reference} dB");
    }

    [Fact]
    public void RoomCorrectionCutsTheModeAndLeavesTheTreble()
    {
        var grid = EqFitter.Grid;
        var room = ResponseCurve.ResponseDb([new EqBand(FilterType.Peak, 63, 9, 6), new EqBand(FilterType.HighShelf, 6000, -3, 0.7)],
                                            grid, 48_000);
        var measurement = new FrequencyResponse([.. grid], room.Select(x => 80 + x).ToArray());
        var options = new EqFitter.Options
        {
            CorrectOnlyBelow = 500,
            MaxBoostDb = 3,
            MaxQ = 10,
            LowSmoothingOctaves = 1.0 / 12,
        };
        var result = EqFitter.Fit(measurement, RoomResponse.FlatTarget(), options);
        var eq = ResponseCurve.ResponseDb(result.Bands, [63, 1000, 8000], 48_000);
        Assert.True(eq[0] < -6, $"mode not cut: {eq[0]}");
        Assert.True(Math.Abs(eq[1]) < 1, $"mids touched: {eq[1]}");
        Assert.True(Math.Abs(eq[2]) < 1, $"treble touched: {eq[2]}");
        Assert.All(result.Bands, b => Assert.True(b.Frequency <= 750));
    }
}
