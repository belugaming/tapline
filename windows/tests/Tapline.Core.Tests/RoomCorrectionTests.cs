using System.Text.Json;
using Tapline.Core;

namespace Tapline.Core.Tests;

// Port of Packages/TaplineCore/Tests/TaplineCoreTests/RoomCorrectionTests.swift.
// KernelTests.bandsCanTargetOneChannel exercises the macOS C kernel and is not ported.

public class Adi2PresetTests
{
    /// <summary>The layout RME's ADI-2 Remote writes (factory values, as saved by the app).</summary>
    internal const string RmeSample = "\t<ADI-2DAC>\n" +
        "\t\t<General>\n" +
        "\t\t\t<val e=\"Version\" v=\"2\"/>\n" +
        "\t\t</General>\n" +
        "\t\t<EQPreset1Left>\n" +
        "\t\t\t<val e=\"EQEnable\" v=\"1.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand1Type\" v=\"1.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand1Gain\" v=\"0.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand1Freq\" v=\"100.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand1Q\" v=\"1.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand2Gain\" v=\"0.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand2Freq\" v=\"500.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand2Q\" v=\"1.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand3Gain\" v=\"0.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand3Freq\" v=\"1000.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand3Q\" v=\"1.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand4Gain\" v=\"0.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand4Freq\" v=\"5000.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand4Q\" v=\"1.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand5Type\" v=\"1.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand5Gain\" v=\"0.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand5Freq\" v=\"10000.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand5Q\" v=\"1.00,\"/>\n" +
        "\t\t\t<val e=\"BassTrebleEn\" v=\"1.00,\"/>\n" +
        "\t\t\t<val e=\"EQBassGain\" v=\"0.00,\"/>\n" +
        "\t\t\t<val e=\"EQBassFreq\" v=\"85.00,\"/>\n" +
        "\t\t\t<val e=\"EQBassQ\" v=\"0.90,\"/>\n" +
        "\t\t\t<val e=\"EQTrebleGain\" v=\"0.00,\"/>\n" +
        "\t\t\t<val e=\"EQTrebleFreq\" v=\"6500.00,\"/>\n" +
        "\t\t\t<val e=\"EQTrebleQ\" v=\"0.70,\"/>\n" +
        "\t\t\t<val e=\"DualEQ\" v=\"0.00,\"/>\n" +
        "\t\t\t<val e=\"EQ Preset Select\" v=\"1.00,\"/>\n" +
        "\t\t\t<val e=\"Load BT w. EQ\" v=\"1.00,\"/>\n" +
        "\t\t</EQPreset1Left>\n" +
        "\t\t<EQPreset1Right>\n" +
        "\t\t\t<val e=\"EQEnable\" v=\"1.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand1Type\" v=\"1.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand1Gain\" v=\"0.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand1Freq\" v=\"100.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand1Q\" v=\"1.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand2Gain\" v=\"0.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand2Freq\" v=\"500.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand2Q\" v=\"1.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand3Gain\" v=\"0.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand3Freq\" v=\"1000.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand3Q\" v=\"1.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand4Gain\" v=\"0.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand4Freq\" v=\"5000.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand4Q\" v=\"1.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand5Type\" v=\"1.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand5Gain\" v=\"0.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand5Freq\" v=\"10000.00,\"/>\n" +
        "\t\t\t<val e=\"EQBand5Q\" v=\"1.00,\"/>\n" +
        "\t\t</EQPreset1Right>\n" +
        "\t\t<PresetNames>\n" +
        "\t\t\t<val e=\"EQPresetName1\" v=\"Factory\"/>\n" +
        "\t\t</PresetNames>\n" +
        "\t</ADI-2DAC>\n";

    /// <summary>Element names and keys, in order: what the app expects to find.</summary>
    private static List<string> Skeleton(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            int r = line.IndexOf("v=\"", StringComparison.Ordinal);
            return r >= 0 ? line[..r] : line;
        }).ToList();

    private static void AssertError(Adi2PresetError error, Action action, FilterType? filterType = null)
    {
        var e = Assert.Throws<Adi2PresetException>(action);
        Assert.Equal(error, e.Error);
        if (filterType is not null) Assert.Equal(filterType, e.FilterType);
    }

    [Fact]
    public void ExportHasTheLayoutOfRmesOwnFiles()
    {
        var file = Adi2Preset.Export([], "Factory");
        Assert.Equal(Skeleton(RmeSample), Skeleton(file));
        Assert.StartsWith("\t<ADI-2DAC>\n\t\t<General>\n\t\t\t<val e=\"Version\" v=\"2\"/>\n", file);
        Assert.EndsWith("\t</ADI-2DAC>\n", file);
    }

    [Fact]
    public void ExportsBellsRoundedIntoTheirBands()
    {
        EqBand[] left = [new(FilterType.Peak, 47.3, -7.74, 4.96), new(FilterType.Peak, 112, 2.2, 3), new(FilterType.Peak, 260, -3.1, 2.04)];
        var file = Adi2Preset.Export(left, "Room");
        Assert.Contains("<val e=\"EQBand1Type\" v=\"0.00,\"/>", file);
        Assert.Contains("<val e=\"EQBand1Gain\" v=\"-7.50,\"/>", file);
        Assert.Contains("<val e=\"EQBand1Freq\" v=\"47.00,\"/>", file);
        Assert.Contains("<val e=\"EQBand1Q\" v=\"5.00,\"/>", file);
        Assert.Contains("<val e=\"EQBand2Gain\" v=\"2.00,\"/>", file);
        Assert.Contains("<val e=\"EQBand3Freq\" v=\"260.00,\"/>", file);
        // Unused bands stay flat.
        Assert.Contains("<val e=\"EQBand4Gain\" v=\"0.00,\"/>", file);
        Assert.Contains("<val e=\"DualEQ\" v=\"0.00,\"/>", file);
        Assert.Contains("<val e=\"EQPresetName1\" v=\"Room\"/>", file);
    }

    [Fact]
    public void DualEqRoundTrips()
    {
        EqBand[] left = [new(FilterType.Peak, 45, -6, 5, channels: EqChannels.Left), new(FilterType.Peak, 300, -2, 2, channels: EqChannels.Left)];
        EqBand[] right = [new(FilterType.Peak, 52, -8.5, 6.2, channels: EqChannels.Right)];
        var file = Adi2Preset.Export(left, "Room L/R", right);
        Assert.Contains("<val e=\"DualEQ\" v=\"1.00,\"/>", file);
        var parsed = Adi2Preset.Parse(file);
        Assert.Equal("Room L/R", parsed.Name);
        Assert.Equal([45.0, 300], parsed.Bands.Where(b => b.Channels == EqChannels.Left).Select(b => b.Frequency));
        var r = parsed.Bands.First(b => b.Channels == EqChannels.Right);
        Assert.True(r.Frequency == 52 && r.Gain == -8.5 && Math.Abs(r.Q - 6.2) < 1e-9 && r.Type == FilterType.Peak);
    }

    [Fact]
    public void RefusesWhatTheHardwareCannotHold()
    {
        var six = Enumerable.Range(0, 6).Select(i => new EqBand(FilterType.Peak, 300 + i * 100.0, -2, 1)).ToList();
        AssertError(Adi2PresetError.DoesNotFit, () => Adi2Preset.Export(six, "x"));
        // Bands 4 and 5 start at 200 Hz: four bells in the bass do not fit.
        var bass = new[] { 40.0, 60, 90, 130 }.Select(f => new EqBand(FilterType.Peak, f, -3, 4)).ToList();
        AssertError(Adi2PresetError.DoesNotFit, () => Adi2Preset.Export(bass, "x"));
        // Bass covers 20-150 Hz only, and there is one of it.
        AssertError(Adi2PresetError.UnsupportedFilter,
                    () => Adi2Preset.Export([new EqBand(FilterType.LowShelf, 300, 3, 0.7)], "x"), FilterType.LowShelf);
        AssertError(Adi2PresetError.UnsupportedFilter,
                    () => Adi2Preset.Export([new EqBand(FilterType.LowShelf, 100, 3, 0.7), new EqBand(FilterType.LowShelf, 60, 2, 0.7)], "x"),
                    FilterType.LowShelf);
        AssertError(Adi2PresetError.UnsupportedFilter,
                    () => Adi2Preset.Export([new EqBand(FilterType.HighPass, 30, q: 0.7)], "x"), FilterType.HighPass);
    }

    [Fact]
    public void ShelvesBecomeBassAndTreble()
    {
        // An AutoEQ-style headphone preset.
        EqBand[] bands =
        [
            new(FilterType.LowShelf, 105, 5.6, 0.7), new(FilterType.Peak, 180, -3.2, 0.9),
            new(FilterType.Peak, 2800, 2.1, 2.2), new(FilterType.HighShelf, 10_000, -2.4, 0.7),
        ];
        var file = Adi2Preset.Export(bands, "Headphones");
        Assert.Contains("<val e=\"EQBassGain\" v=\"5.50,\"/>", file);
        Assert.Contains("<val e=\"EQBassFreq\" v=\"105.00,\"/>", file);
        Assert.Contains("<val e=\"EQTrebleGain\" v=\"-2.50,\"/>", file);
        Assert.Contains("<val e=\"Load BT w. EQ\" v=\"1.00,\"/>", file);
        var back = Adi2Preset.Parse(file).Bands;
        Assert.Equal([FilterType.LowShelf, FilterType.Peak, FilterType.Peak, FilterType.HighShelf], back.Select(b => b.Type));
    }

    [Fact]
    public void ReadsSharedPresetsWithBassAndTreble()
    {
        var text = RmeSample.Replace("<val e=\"EQBand1Type\" v=\"1.00,\"/>", "<val e=\"EQBand1Type\" v=\"0.00,\"/>");
        text = text.Replace("<val e=\"EQBand1Gain\" v=\"0.00,\"/>", "<val e=\"EQBand1Gain\" v=\"2.50,\"/>");
        text = text.Replace("<val e=\"EQBassGain\" v=\"0.00,\"/>", "<val e=\"EQBassGain\" v=\"3.00,\"/>");
        var parsed = Adi2Preset.Parse(text);
        Assert.Equal("Factory", parsed.Name);
        Assert.Equal(2, parsed.Bands.Count);
        Assert.True(parsed.Bands[0].Type == FilterType.LowShelf && parsed.Bands[0].Gain == 3 && parsed.Bands[0].Frequency == 85);
        Assert.True(parsed.Bands[1].Type == FilterType.Peak && parsed.Bands[1].Gain == 2.5 && parsed.Bands[1].Channels == EqChannels.Both);
        AssertError(Adi2PresetError.NotAPreset, () => Adi2Preset.Parse("Filter 1: ON PK Fc 100 Hz Gain 1 dB Q 1"));
    }

    [Fact]
    public void SkipsNonFiniteValues()
    {
        var text = RmeSample.Replace("<val e=\"EQBand1Type\" v=\"1.00,\"/>", "<val e=\"EQBand1Type\" v=\"0.00,\"/>");
        text = text.Replace("<val e=\"EQBand1Gain\" v=\"0.00,\"/>", "<val e=\"EQBand1Gain\" v=\"NaN,\"/>");
        text = text.Replace("<val e=\"EQBassGain\" v=\"0.00,\"/>", "<val e=\"EQBassGain\" v=\"Infinity,\"/>");
        var parsed = Adi2Preset.Parse(text);
        Assert.All(parsed.Bands, b => Assert.True(double.IsFinite(b.Frequency) && double.IsFinite(b.Gain) && double.IsFinite(b.Q)));
    }

    [Fact]
    public void FitStaysWithinTheHardware()
    {
        var grid = EqFitter.Grid;
        // Five resonances below 200 Hz and one above: more than bands 1-3 can take.
        var room = ResponseCurve.ResponseDb(
        [
            new EqBand(FilterType.Peak, 38, 7, 5), new EqBand(FilterType.Peak, 55, 9, 7), new EqBand(FilterType.Peak, 82, 6, 5),
            new EqBand(FilterType.Peak, 120, 5, 6), new EqBand(FilterType.Peak, 170, 6, 8), new EqBand(FilterType.Peak, 320, 4, 3),
        ], grid, 48_000);
        var measured = new FrequencyResponse([.. grid], room.Select(x => 80 + x).ToArray());
        var baseOptions = new EqFitter.Options
        {
            CorrectOnlyBelow = 500,
            MaxBoostDb = 3,
            MaxCutDb = 15,
            MaxQ = 10,
            LowSmoothingOctaves = 1.0 / 12,
        };
        var result = EqFitter.Fit(measured, RoomResponse.FlatTarget(), Adi2Preset.FitOptions(baseOptions));

        Assert.True(result.Bands.Count <= 5);
        Assert.True(result.Bands.Count(b => b.Frequency < 200) <= 3);
        Assert.All(result.Bands, b => Assert.True(b.Type == FilterType.Peak && Math.Abs(b.Gain) <= 12));
        Assert.True(result.Bands.All(b => Math.Abs(b.Gain * 2 - Math.Round(b.Gain * 2, MidpointRounding.AwayFromZero)) < 1e-9),
                    $"gain steps: {string.Join(", ", result.Bands.Select(b => b.Gain))}");
        Assert.True(result.Bands.All(b => Math.Abs(b.Q * 10 - Math.Round(b.Q * 10, MidpointRounding.AwayFromZero)) < 1e-6),
                    $"Q steps: {string.Join(", ", result.Bands.Select(b => b.Q))}");
        Assert.True(result.ErrorAfterDb < result.ErrorBeforeDb * 0.75, $"{result.ErrorBeforeDb} -> {result.ErrorAfterDb}");
        // What the fitter made, the exporter takes as is.
        var file = Adi2Preset.Export(result.Bands, "Room");
        var back = Adi2Preset.Parse(file).Bands;
        Assert.Equal(result.Bands.Select(b => b.Frequency).ToHashSet(), back.Select(b => b.Frequency).ToHashSet());
    }
}

public class LowFrequencyLimitTests
{
    [Fact]
    public void FindsTheRollOffAndDoesNotBoostBelowIt()
    {
        var grid = EqFitter.Grid;
        // Speakers rolling off at 38 Hz, with a mode at 47 Hz.
        var room = ResponseCurve.ResponseDb(
        [
            new EqBand(FilterType.HighPass, 38, q: 0.8), new EqBand(FilterType.HighPass, 38, q: 0.8),
            new EqBand(FilterType.Peak, 47, 7, 5),
        ], grid, 48_000);
        var measured = new FrequencyResponse([.. grid], room.Select(x => 80 + x).ToArray());
        double limit = RoomResponse.LowFrequencyLimit(measured);
        Assert.True(limit > 30 && limit < 45, $"limit {limit}");

        var o = new EqFitter.Options
        {
            CorrectOnlyBelow = 500,
            MaxBoostDb = 3,
            MaxQ = 10,
            LowSmoothingOctaves = 1.0 / 12,
            NoBoostBelow = limit,
        };
        var fit = EqFitter.Fit(measured, RoomResponse.FlatTarget(), o);
        var eq = ResponseCurve.ResponseDb(fit.Bands, [22, 47], 48_000);
        Assert.True(eq[0] < 0.5, $"boosted the roll-off: {eq[0]}");
        Assert.True(eq[1] < -4, $"mode not cut: {eq[1]}");
    }
}

public class StereoMeasurementTests
{
    /// <summary>Speaker + room: a delay (whole samples), a polarity, a colouration.</summary>
    private static float[] Speaker(float[] x, int delay, bool invert = false, EqBand[]? bands = null, double sampleRate = 48_000)
    {
        var coloured = RoomTests.Filter(x, bands ?? [], sampleRate);
        return [.. new float[delay], .. coloured.Select(v => invert ? -v : v)];
    }

    private static float[] Record(StereoSweepPlan plan, int leftDelay, int rightDelay, bool invertRight = false, double rightGainDb = 0, double drift = 0)
    {
        double sr = plan.SampleRate;
        var (l, r) = plan.Render();
        float gain = (float)Math.Pow(10, rightGainDb / 20);
        var lp = Speaker(l, leftDelay, bands: [new EqBand(FilterType.Peak, 60, 6, 5)], sampleRate: sr);
        var rp = Speaker(r, rightDelay, invertRight, [new EqBand(FilterType.Peak, 75, -5, 4)], sr).Select(v => v * gain).ToArray();
        int latency = (int)(0.12 * sr);
        var mic = new float[latency + Math.Max(lp.Length, rp.Length) + (int)(0.5 * sr)];
        for (int i = 0; i < lp.Length; i++) mic[latency + i] += lp[i];
        for (int i = 0; i < rp.Length; i++) mic[latency + i] += rp[i];
        ulong seed = 7;
        for (int i = 0; i < mic.Length; i++)
        {
            seed = unchecked(seed * 6364136223846793005UL + 1442695040888963407UL);
            mic[i] += ((float)(seed >> 40) / (float)(1 << 24) - 0.5f) * 1e-4f;
        }
        if (drift == 0) return mic;
        // The recording interface runs fast by `drift`: it samples the sound at t * (1 + drift).
        int count = (int)(mic.Length / (1 + drift)) - 2;
        return Enumerable.Range(0, count).Select(i =>
        {
            double x = i * (1 + drift);
            int j = (int)x;
            float frac = (float)(x - j);
            return mic[j] * (1 - frac) + mic[j + 1] * frac;
        }).ToArray();
    }

    [Fact]
    public void FindsDistanceBalanceAndBothResponses()
    {
        const double sr = 48_000.0;
        var plan = new StereoSweepPlan(sr, sweepSeconds: 3);
        // Right speaker 7 samples (5.0 cm) farther and 1.5 dB louder.
        var mic = Record(plan, leftDelay: 96, rightDelay: 103, rightGainDb: 1.5);
        var a = StereoSweepPlan.Analyze(mic, sr, sweepSeconds: 3);
        Assert.NotNull(a);
        Assert.NotNull(a.ArrivalDifference);
        double dt = a.ArrivalDifference.Value;
        Assert.True(Math.Abs(dt * sr - 7) < 0.15, $"arrival difference {dt * sr} samples");
        Assert.False(a.OppositePolarity);
        double refL = a.Left.LevelAt(1000), refR = a.Right.LevelAt(1000);
        Assert.True(Math.Abs(a.Left.LevelAt(60) - refL - 6) < 1, $"left mode {a.Left.LevelAt(60) - refL}");
        Assert.True(Math.Abs(a.Right.LevelAt(75) - refR + 5) < 1, $"right dip {a.Right.LevelAt(75) - refR}");
        Assert.True(Math.Abs(refR - refL - 1.5) < 0.2);
        Assert.True(a.SnrLeftDb > 40 && a.SnrRightDb > 40);
    }

    [Fact]
    public void NoticesOppositePolarity()
    {
        const double sr = 48_000.0;
        var plan = new StereoSweepPlan(sr, sweepSeconds: 3);
        var mic = Record(plan, leftDelay: 96, rightDelay: 96, invertRight: true);
        var a = StereoSweepPlan.Analyze(mic, sr, sweepSeconds: 3);
        Assert.NotNull(a);
        Assert.True(a.OppositePolarity);
        Assert.True(Math.Abs((a.ArrivalDifference ?? 1) * sr) < 0.15);
    }

    [Fact]
    public void SpeakersAndMicrophoneMayRunAtDifferentRates()
    {
        // Speakers on a 44.1 kHz interface, a 48 kHz microphone: the sweep can only reach
        // 0.45 x 44.1 kHz, and the analysis at 48 kHz has to deconvolve with that same sweep.
        const double output = 44_100.0, sr = 48_000.0;
        double end = SineSweep.EndFrequencyFor(output, sr);
        Assert.Equal(0.45 * output, end);
        Assert.Equal(end, SineSweep.EndFrequencyFor(sr, output));
        var played = new StereoSweepPlan(output, sweepSeconds: 3, endFrequency: end);
        // What the microphone hears: the same continuous signal, sampled at its own rate.
        var heard = new StereoSweepPlan(sr, sweepSeconds: 3, endFrequency: played.Sweep.EndFrequency);
        var mic = Record(heard, leftDelay: 96, rightDelay: 103, rightGainDb: 1.5);
        var a = StereoSweepPlan.Analyze(mic, sr, outputRate: output, sweepSeconds: 3);
        Assert.NotNull(a);
        double refL = a.Left.LevelAt(1000), refR = a.Right.LevelAt(1000);
        Assert.True(Math.Abs(a.Left.LevelAt(60) - refL - 6) < 1, $"left mode {a.Left.LevelAt(60) - refL}");
        Assert.True(Math.Abs(a.Right.LevelAt(75) - refR + 5) < 1, $"right dip {a.Right.LevelAt(75) - refR}");
        Assert.True(Math.Abs(a.Left.LevelAt(10_000) - refL) < 1, $"left treble {a.Left.LevelAt(10_000) - refL}");
        Assert.True(Math.Abs(refR - refL - 1.5) < 0.2);
        Assert.NotNull(a.ArrivalDifference);
        Assert.True(Math.Abs(a.ArrivalDifference.Value * sr - 7) < 0.15, $"arrival difference {a.ArrivalDifference.Value * sr} samples");
    }

    [Fact]
    public void CancelsClockDriftBetweenInterfaces()
    {
        const double sr = 48_000.0;
        var plan = new StereoSweepPlan(sr, sweepSeconds: 3);
        // 300 ppm, far worse than real crystals: uncorrected it would read 0.15 ms (5 cm) off.
        var mic = Record(plan, leftDelay: 96, rightDelay: 96, drift: 300e-6);
        var a = StereoSweepPlan.Analyze(mic, sr, sweepSeconds: 3);
        Assert.NotNull(a);
        double cm = (a.ArrivalDifference ?? 1) * 34_300;
        Assert.True(Math.Abs(cm) < 0.5, $"{cm} cm");
    }
}

public class RoomAdvisorTests
{
    private static readonly IReadOnlyList<double> Grid = EqFitter.Grid;

    private static FrequencyResponse Response(IEnumerable<EqBand> bands, double level = 80) =>
        new([.. Grid], ResponseCurve.ResponseDb(bands, Grid, 48_000).Select(x => level + x).ToArray());

    [Fact]
    public void SpotsWhatToFixAtTheSpeakers()
    {
        EqBand[] room =
        [
            new(FilterType.LowShelf, 90, 5, 0.7),  // subwoofer too loud
            new(FilterType.Peak, 47, 8, 8),        // axial mode
            new(FilterType.Peak, 105, -16, 7),     // cancellation near the crossover
            new(FilterType.Peak, 165, 6, 1.6),     // desk bounce
        ];
        var input = new RoomAdvisor.Input(Response(room), RoomResponse.FlatTarget(), positions: 1, worstSnrDb: 48, calibrated: false,
                                          left: Response(room), right: Response(room, level: 81.5),
                                          arrivalDifference: 0.0003, oppositePolarity: false);
        var advice = RoomAdvisor.Advise(input);
        var kinds = advice.Select(a => a.Kind).ToList();
        string all = string.Join("; ", kinds);

        Assert.True(kinds.Any(k => k is RoomAdviceKind.Balance { Louder: EqChannels.Right } b && Math.Abs(b.Db - 1.5) < 0.2), all);
        Assert.True(kinds.Any(k => k is RoomAdviceKind.Distance { Farther: EqChannels.Right } d && Math.Abs(d.Centimeters - 10.3) < 0.2), all);
        Assert.True(kinds.Any(k => k is RoomAdviceKind.Subwoofer s && s.Db > 3), all);
        Assert.True(kinds.Any(k => k is RoomAdviceKind.RoomMode m && Math.Abs(m.Frequency - 47) < 3 && Math.Abs(m.DimensionMeters - 3.65) < 0.25), all);
        Assert.True(kinds.Any(k => k is RoomAdviceKind.Cancellation { NearCrossover: true } c && Math.Abs(c.Frequency - 105) < 6), all);
        Assert.True(kinds.Any(k => k is RoomAdviceKind.Desktop d && Math.Abs(d.Frequency - 165) < 25), all);
        Assert.True(kinds.Any(k => k is RoomAdviceKind.FewPositions), all);
        Assert.DoesNotContain(new RoomAdviceKind.Polarity(), kinds);
    }

    [Fact]
    public void ANarrowModeIsNotADeskBounce()
    {
        var room = Response([new EqBand(FilterType.Peak, 176, 6, 6)]);
        var advice = RoomAdvisor.Advise(new RoomAdvisor.Input(room, RoomResponse.FlatTarget(), positions: 3, worstSnrDb: 50, calibrated: true));
        string all = string.Join("; ", advice.Select(a => a.Kind));
        Assert.False(advice.Any(a => a.Kind is RoomAdviceKind.Desktop), all);
        Assert.True(advice.Any(a => a.Kind is RoomAdviceKind.RoomMode m && Math.Abs(m.Frequency - 176) < 8), all);
    }

    [Fact]
    public void StaysQuietForAGoodRoom()
    {
        var flat = Response([]);
        var input = new RoomAdvisor.Input(flat, RoomResponse.FlatTarget(), positions: 4, worstSnrDb: 50, calibrated: true,
                                          left: flat, right: flat, arrivalDifference: 0.00002, oppositePolarity: false);
        Assert.Empty(RoomAdvisor.Advise(input));
    }

    [Fact]
    public void WarnsAboutPolarityFirst()
    {
        var flat = Response([]);
        var input = new RoomAdvisor.Input(flat, RoomResponse.FlatTarget(), positions: 1, worstSnrDb: 50, calibrated: true, oppositePolarity: true);
        Assert.Equal(new RoomAdviceKind.Polarity(), RoomAdvisor.Advise(input).FirstOrDefault()?.Kind);
    }
}

public class ChannelTests
{
    [Fact]
    public void OldPresetsDecodeAsBothChannels()
    {
        const string json = """{"id":"6F1C0A8E-34B4-4C59-9E58-1C2B6B7D9A10","type":"peak","frequency":100,"gain":-3,"q":2,"isEnabled":true}""";
        var band = JsonSerializer.Deserialize<EqBand>(json, TaplineJson.Options);
        Assert.Equal(EqChannels.Both, band.Channels);
        var again = JsonSerializer.Deserialize<EqBand>(
            JsonSerializer.Serialize(new EqBand(FilterType.Peak, 50, channels: EqChannels.Right), TaplineJson.Options), TaplineJson.Options);
        Assert.Equal(EqChannels.Right, again.Channels);
    }

    [Fact]
    public void TextFormatKeepsChannels()
    {
        EqBand[] bands =
        [
            new(FilterType.Peak, 100, -2, 1), new(FilterType.Peak, 45, -6, 5, channels: EqChannels.Left),
            new(FilterType.Peak, 52, -4, 4, channels: EqChannels.Right),
        ];
        var text = ParametricEqFormat.Export(bands, 0);
        Assert.Contains("Channel: L\nFilter 2:", text);
        var parsed = ParametricEqFormat.Parse(text).Bands;
        Assert.Equal([EqChannels.Both, EqChannels.Left, EqChannels.Right], parsed.Select(b => b.Channels));
        var centre = ParametricEqFormat.Parse("Channel: C\nFilter 1: ON PK Fc 100 Hz Gain -3 dB Q 1\nChannel: all\nFilter 2: ON PK Fc 200 Hz Gain -3 dB Q 1");
        Assert.Equal([200.0], centre.Bands.Select(b => b.Frequency));
    }

    [Fact]
    public void PreampLooksAtEachChannel()
    {
        var s = new DspSettings();
        s.Bands = [new EqBand(FilterType.Peak, 100, 4, 2, channels: EqChannels.Left), new EqBand(FilterType.Peak, 100, 5, 2, channels: EqChannels.Right)];
        Assert.True(Math.Abs(s.EffectivePreampDb() + 5) < 0.05, $"{s.EffectivePreampDb()}");
    }
}
