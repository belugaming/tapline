using Tapline.Core;

namespace Tapline.Core.Tests;

/// <summary>Values produced by the Swift TaplineCore for the same inputs (dumped from a scratch
/// test on macOS): the port must compute the same results, not merely pass the same thresholds.</summary>
public class SwiftReferenceTests
{
    private static string Describe(IEnumerable<EqBand> bands) =>
        string.Join("; ", bands.Select(b => $"{b.Type} {b.Frequency} {b.Gain} {b.Q}"));

    private static void AssertBands(IEnumerable<EqBand> actual, params (FilterType Type, double F, double Gain, double Q)[] expected)
    {
        var list = actual.ToList();
        Assert.True(list.Count == expected.Length, Describe(list));
        for (int i = 0; i < expected.Length; i++)
        {
            var (type, f, gain, q) = expected[i];
            Assert.True(list[i].Type == type && list[i].Frequency == f && Math.Abs(list[i].Gain - gain) < 1e-9 && Math.Abs(list[i].Q - q) < 1e-9,
                        $"band {i}: {Describe(list)}");
        }
    }

    [Fact]
    public void HeadphoneFitMatchesSwift()
    {
        EqBand[] truth =
        [
            new(FilterType.LowShelf, 105, 5, 0.7),
            new(FilterType.Peak, 180, -4, 1.0),
            new(FilterType.Peak, 2500, 4, 1.4),
            new(FilterType.Peak, 4500, -5, 3),
        ];
        var r = EqFitter.Fit(FitterTests.Synthetic(truth), new FrequencyResponse([20, 1000, 20_000], [0, 0, 0]));
        Assert.Equal(2.951998158079711, r.ErrorBeforeDb, 9);
        Assert.Equal(0.1249002071651134, r.ErrorAfterDb, 6);
        AssertBands(r.Bands,
            (FilterType.LowShelf, 103, 5.8, 0.7),
            (FilterType.Peak, 181, -3.3, 1.11),
            (FilterType.Peak, 667, 0.7, 0.49),
            (FilterType.Peak, 2538, 4.7, 1.17),
            (FilterType.Peak, 4507, -4.6, 2.66),
            (FilterType.Peak, 8041, -0.7, 3.45),
            (FilterType.HighShelf, 4744, 0.6, 1.2));
    }

    private static FrequencyResponse HardwareRoom()
    {
        var grid = EqFitter.Grid;
        var room = ResponseCurve.ResponseDb(
        [
            new EqBand(FilterType.Peak, 38, 7, 5), new EqBand(FilterType.Peak, 55, 9, 7), new EqBand(FilterType.Peak, 82, 6, 5),
            new EqBand(FilterType.Peak, 120, 5, 6), new EqBand(FilterType.Peak, 170, 6, 8), new EqBand(FilterType.Peak, 320, 4, 3),
        ], grid, 48_000);
        return new FrequencyResponse([.. grid], room.Select(x => 80 + x).ToArray());
    }

    [Fact]
    public void HardwareFitMatchesSwift()
    {
        var o = new EqFitter.Options { CorrectOnlyBelow = 500, MaxBoostDb = 3, MaxCutDb = 15, MaxQ = 10, LowSmoothingOctaves = 1.0 / 12 };
        var r = EqFitter.Fit(HardwareRoom(), RoomResponse.FlatTarget(), Adi2Preset.FitOptions(o));
        Assert.Equal(2.2473876629546035, r.ErrorBeforeDb, 9);
        Assert.Equal(1.0349586865638618, r.ErrorAfterDb, 6);
        AssertBands(r.Bands,
            (FilterType.Peak, 38, -5.5, 6.9),
            (FilterType.Peak, 55, -6.5, 8.4),
            (FilterType.Peak, 106, -3.0, 0.8),
            (FilterType.Peak, 226, 1.5, 5.0),
            (FilterType.Peak, 318, -2.5, 5.0));
        Assert.Equal(20.000000000000004, RoomResponse.LowFrequencyLimit(HardwareRoom()), 9);
    }

    [Fact]
    public void SpectrumMatchesSwift()
    {
        var analyzer = new SpectrumAnalyzer(8192, 320) { Decay = 0 };
        var samples = Enumerable.Range(0, 8192)
            .Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 1234.5 * i / 48_000) + 0.01 * Math.Sin(i * 0.37))).ToArray();
        var output = analyzer.Process(samples, 48_000);
        // Above vDSP's single-precision noise floor the two must agree closely.
        (int Index, double Db)[] expected = [(185, -96.090706), (188, -69.88818), (190, -5.6937065), (192, -54.11328), (195, -93.35874), (229, -36.397884)];
        foreach (var (i, db) in expected) Assert.True(Math.Abs(output[i] - db) < 0.05, $"{i}: {output[i]} vs {db}");
        var output2 = analyzer.Process(samples.Select(x => x * 0.5f).ToArray(), 44_100);
        Assert.True(Math.Abs(output2[190] - -90.12644) < 0.05, $"{output2[190]}");
    }

    [Fact]
    public void SweepMatchesSwift()
    {
        var sweep = new SineSweep(48_000, duration: 1);
        Assert.Equal(48_000, sweep.Signal.Length);
        Assert.True(Math.Abs(sweep.Signal[1000] - 0.080027975) < 1e-6, $"{sweep.Signal[1000]}");
        Assert.True(Math.Abs(sweep.Inverse[1000] - -0.00016883628) < 1e-8, $"{sweep.Inverse[1000]}");
        var fr = RoomResponse.FromImpulse(sweep.ImpulseResponse(sweep.Signal), 48_000);
        Assert.NotNull(fr);
        (double F, double Db)[] expected = [(30, 0.8399603218642588), (100, -0.3378074756348905), (1000, 0), (5000, 0), (15000, 0)];
        foreach (var (f, db) in expected) Assert.True(Math.Abs(fr.LevelAt(f) - db) < 1e-3, $"{f}: {fr.LevelAt(f)} vs {db}");
    }

    [Fact]
    public void AdviceMatchesSwift()
    {
        var grid = EqFitter.Grid;
        var levels = ResponseCurve.ResponseDb(
        [
            new EqBand(FilterType.LowShelf, 90, 5, 0.7), new EqBand(FilterType.Peak, 47, 8, 8),
            new EqBand(FilterType.Peak, 105, -16, 7), new EqBand(FilterType.Peak, 165, 6, 1.6),
        ], grid, 48_000).Select(x => 80 + x).ToArray();
        var advice = RoomAdvisor.Advise(new RoomAdvisor.Input(new FrequencyResponse([.. grid], levels), RoomResponse.FlatTarget(),
                                                              positions: 1, worstSnrDb: 48, calibrated: false));
        RoomAdvice[] expected =
        [
            new(new RoomAdviceKind.Subwoofer(5.136671044078858), RoomAdviceSeverity.Suggestion),
            new(new RoomAdviceKind.BassTilt(1.8155711245342248, -2.0), RoomAdviceSeverity.Suggestion),
            new(new RoomAdviceKind.Desktop(180.5691579035172, 2.9978302718223393), RoomAdviceSeverity.Suggestion),
            new(new RoomAdviceKind.Cancellation(105.11304748156492, 13.858316374074201, 81.57883541055084, true), RoomAdviceSeverity.Suggestion),
            new(new RoomAdviceKind.RoomMode(46.68487601624958, 6.434583274476916, 3.6735665730440425), RoomAdviceSeverity.Info),
            new(new RoomAdviceKind.FewPositions(1), RoomAdviceSeverity.Info),
        ];
        Assert.Equal(expected.Length, advice.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Severity, advice[i].Severity);
            Assert.Equal(expected[i].Kind.GetType(), advice[i].Kind.GetType());
            Assert.Equal(expected[i].Id, advice[i].Id);
        }
        Assert.Equal(5.136671044078858, ((RoomAdviceKind.Subwoofer)advice[0].Kind).Db, 9);
        Assert.Equal(2.9978302718223393, ((RoomAdviceKind.Desktop)advice[2].Kind).Db, 9);
        Assert.Equal(13.858316374074201, ((RoomAdviceKind.Cancellation)advice[3].Kind).DepthDb, 9);
        Assert.Equal(6.434583274476916, ((RoomAdviceKind.RoomMode)advice[4].Kind).Db, 9);
    }
}
