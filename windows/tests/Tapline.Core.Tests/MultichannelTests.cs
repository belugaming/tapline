using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tapline.Core;

namespace Tapline.Core.Tests;

// Port of Packages/TaplineCore/Tests/TaplineCoreTests/MultichannelTests.swift. MultichannelKernelTests
// exercises the macOS C kernel; its processing rules are checked here on the Equalizer APO
// configuration instead (MultichannelApoConfigTests).

// MARK: - Layouts

public class SpeakerLayoutTests
{
    private const SpeakerPosition L = SpeakerPosition.FrontLeft, R = SpeakerPosition.FrontRight, C = SpeakerPosition.Center, Lfe = SpeakerPosition.Lfe,
        Ls = SpeakerPosition.SurroundLeft, Rs = SpeakerPosition.SurroundRight, Lrs = SpeakerPosition.RearLeft, Rrs = SpeakerPosition.RearRight,
        Ltf = SpeakerPosition.TopFrontLeft, Rtf = SpeakerPosition.TopFrontRight, Ltr = SpeakerPosition.TopRearLeft, Rtr = SpeakerPosition.TopRearRight,
        Unknown = SpeakerPosition.Unknown;

    [Fact]
    public void NamesCountEarLevelLfeAndHeightSpeakers()
    {
        Assert.Equal("5.1", SpeakerLayout.Surround51.Name);
        Assert.Equal("7.1", SpeakerLayout.Surround71.Name);
        Assert.Equal("5.1.4", SpeakerLayout.Atmos514.Name);
        Assert.Equal("7.1.4", SpeakerLayout.Atmos714.Name);
        Assert.Equal("9.1.6", SpeakerLayout.Atmos916.Name);
        Assert.Equal("2.0", new SpeakerLayout([L, R]).Name);
        Assert.Equal(SpeakerLayout.Atmos714, SpeakerLayout.Standard(12));
        Assert.Null(SpeakerLayout.Standard(4));
    }

    [Fact]
    public void WindowsSpeakerMasksBecomePositionsInBitOrder()
    {
        // KSAUDIO_SPEAKER_7POINT1_SURROUND plus the four top speakers: Windows' 7.1.4 order.
        var atmos = SpeakerLayout.FromChannelMask(0x2D63F, 12);
        Assert.Equal([L, R, C, Lfe, Lrs, Rrs, Ls, Rs, Ltf, Rtf, Ltr, Rtr], atmos.Positions);
        Assert.Equal("7.1.4", atmos.Name);
        Assert.True(atmos.IsSurround);
        Assert.Equal(0, atmos.ReferenceChannel);
        Assert.Equal(Enumerable.Range(0, 12), atmos.SpeakerChannels);
        // It holds the same speakers as macOS's 7.1.4 in another order: not the same layout.
        Assert.NotEqual(SpeakerLayout.Atmos714, atmos);

        Assert.Equal([L, R, C, Lfe, Lrs, Rrs, Ls, Rs], SpeakerLayout.FromChannelMask(0x63F, 8).Positions);
        Assert.Equal([L, R, C, Lfe, Ls, Rs], SpeakerLayout.FromChannelMask(0x60F, 6).Positions);
        Assert.Equal(SpeakerLayout.Surround51, SpeakerLayout.FromChannelMask(0x60F, 6));
        // 5.1 (back): the back pair is the surrounds when there are no side speakers.
        Assert.Equal([L, R, C, Lfe, Ls, Rs], SpeakerLayout.FromChannelMask(0x3F, 6).Positions);
        Assert.Equal([L, R, C, Lfe, Ls, Rs, Ltf, Rtf, Ltr, Rtr], SpeakerLayout.FromChannelMask(0x2D60F, 10).Positions);
        Assert.Equal("5.1.4", SpeakerLayout.FromChannelMask(0x2D60F, 10).Name);
        // The less common bits.
        Assert.Equal([SpeakerPosition.FrontLeftCenter, SpeakerPosition.FrontRightCenter, SpeakerPosition.RearCenter, SpeakerPosition.TopMiddleCenter,
                      SpeakerPosition.TopFrontCenter, SpeakerPosition.TopRearCenter],
                     SpeakerLayout.FromChannelMask(0x40 | 0x80 | 0x100 | 0x800 | 0x2000 | 0x10000, 6).Positions);
        // Bits beyond the speaker bits (SPEAKER_RESERVED, SPEAKER_ALL) name no speaker.
        Assert.Equal([L, R], SpeakerLayout.FromChannelMask(0x8000_0003, 2).Positions);
    }

    [Fact]
    public void OutputsWithoutAUsefulMaskFallBackSensibly()
    {
        // No mask: Windows' default for the channel count.
        Assert.Equal([L, R, C, Lfe, Lrs, Rrs, Ls, Rs], SpeakerLayout.FromChannelMask(0, 8).Positions);
        Assert.Equal(SpeakerLayout.FromChannelMask(0x2D63F, 12), SpeakerLayout.FromChannelMask(0, 12));
        Assert.Equal(SpeakerLayout.FromChannelMask(0x2D60F, 10), SpeakerLayout.FromChannelMask(0, 10));
        Assert.Equal([L, R], SpeakerLayout.FromChannelMask(0, 2).Positions);
        // A mask that names too few channels gives way to the default too.
        Assert.Equal(SpeakerLayout.FromChannelMask(0x63F, 8), SpeakerLayout.FromChannelMask(0x3F, 8));
        // Without a default, the channels beyond the mask are unknown.
        var sixteen = SpeakerLayout.FromChannelMask(0, 16);
        Assert.Equal(16, sixteen.ChannelCount);
        Assert.All(sixteen.Positions, p => Assert.Equal(Unknown, p));
        Assert.False(sixteen.IsSurround);
        Assert.Empty(sixteen.SpeakerChannels);
        Assert.Null(sixteen.ReferenceChannel);
        var fourteen = SpeakerLayout.FromChannelMask(0x63F, 14);
        Assert.Equal([L, R, C, Lfe, Lrs, Rrs, Ls, Rs, Unknown, Unknown, Unknown, Unknown, Unknown, Unknown], fourteen.Positions);
        Assert.Equal("7.1", fourteen.Name);
        Assert.Equal(Enumerable.Range(0, 8), fourteen.SpeakerChannels);
        // A mask naming more channels than the output has: its first ones.
        Assert.Equal([L, R], SpeakerLayout.FromChannelMask(0x63F, 2).Positions);
        Assert.Empty(SpeakerLayout.FromChannelMask(0x63F, 0).Positions);
    }

    [Fact]
    public void OnlySurroundLayoutsGetPerSpeakerProcessing()
    {
        Assert.False(SpeakerLayout.FromChannelMask(0x3, 2).IsSurround);
        // An interface with ten outputs whose driver names only the front pair.
        var interfaceLayout = new SpeakerLayout([L, R, .. Enumerable.Repeat(Unknown, 8)]);
        Assert.False(interfaceLayout.IsSurround);
        Assert.False(ApoConfig.ProcessesPerSpeaker(10, interfaceLayout));
        Assert.True(ApoConfig.ProcessesPerSpeaker(12, SpeakerLayout.FromChannelMask(0x2D63F, 12)));
        // A mono output with a centre speaker, and layouts of another channel count, play as before.
        Assert.False(ApoConfig.ProcessesPerSpeaker(1, SpeakerLayout.FromChannelMask(0x4, 1)));
        Assert.False(ApoConfig.ProcessesPerSpeaker(6, SpeakerLayout.Surround71));
        Assert.False(ApoConfig.ProcessesPerSpeaker(6, null));
    }

    [Fact]
    public void TheReferenceIsTheFrontLeftElseTheFirstMainSpeaker()
    {
        Assert.Equal(1, new SpeakerLayout([Unknown, L, R, C]).ReferenceChannel);
        Assert.Equal(1, new SpeakerLayout([Lfe, C, Ls]).ReferenceChannel);
        Assert.Equal(0, new SpeakerLayout([Lfe]).ReferenceChannel);
        Assert.Equal(3, new SpeakerLayout([Unknown, L, R, Lfe]).ChannelOf(Lfe));
        Assert.Null(new SpeakerLayout([L, R]).ChannelOf(C));
    }

    [Fact]
    public void PositionsGroup()
    {
        Assert.True(SpeakerPosition.Lfe.IsLfe() && SpeakerPosition.Lfe2.IsLfe());
        Assert.True(SpeakerPosition.TopRearLeft.IsHeight());
        Assert.Equal(SpeakerGroup.Surround, SpeakerPosition.RearLeft.Group());
        Assert.True(SpeakerPosition.FrontLeft.IsFrontPair() && !SpeakerPosition.Center.IsFrontPair());
        Assert.All(Enum.GetValues<SpeakerPosition>(), p => Assert.False(string.IsNullOrEmpty(p.Abbreviation())));
        Assert.Equal("Rrs", SpeakerPosition.RearRight.Abbreviation());
        Assert.Equal(-30, SpeakerPosition.FrontLeft.Azimuth());
    }

    [Fact]
    public void LayoutsUseTheSwiftJson()
    {
        var json = JsonSerializer.Serialize(SpeakerLayout.Atmos916, TaplineJson.Options);
        Assert.StartsWith("""{"positions":["frontLeft","frontRight","center","lfe","surroundLeft","surroundRight","rearLeft","rearRight","wideLeft",""", json);
        Assert.Equal("\"lfe2\"", JsonSerializer.Serialize(SpeakerPosition.Lfe2));
        Assert.Equal("\"topMiddleCenter\"", JsonSerializer.Serialize(SpeakerPosition.TopMiddleCenter));
        var back = JsonSerializer.Deserialize<SpeakerLayout>(json, TaplineJson.Options);
        Assert.Equal(SpeakerLayout.Atmos916, back);
        Assert.Equal(SpeakerLayout.Atmos916.GetHashCode(), back!.GetHashCode());
        Assert.Empty(JsonSerializer.Deserialize<SpeakerLayout>("{}", TaplineJson.Options)!.Positions);
    }
}

// MARK: - Measurement

public class SpeakerMeasurementTests
{
    private const double Rate = 48_000;

    /// <summary>Runs <paramref name="x"/> through biquads, like a speaker and room would colour it.</summary>
    private static float[] Filter(float[] x, IEnumerable<EqBand> bands)
    {
        var y = x.Select(v => (double)v).ToArray();
        foreach (var band in bands)
        {
            var c = new BiquadCoefficients(band, Rate);
            double z1 = 0, z2 = 0;
            for (var i = 0; i < y.Length; i++)
            {
                var input = y[i];
                var output = c.B0 * input + z1;
                z1 = c.B1 * input - c.A1 * output + z2;
                z2 = c.B2 * input - c.A2 * output;
                y[i] = output;
            }
        }
        return y.Select(v => (float)v).ToArray();
    }

    private sealed record Room(int[] Delays, double[] GainsDb, HashSet<int> Inverted, EqBand[][] Colour);

    /// <summary>Plays every speaker's signal into one microphone: 25 ms of system latency plus each
    /// speaker's distance, gain, polarity and colouring, and a little noise.</summary>
    private static float[] Record(SpeakerSweepPlan plan, Room room)
    {
        var signals = plan.Render();
        var latency = (int)(0.025 * Rate);
        var output = new float[signals[0].Length + latency + (int)(0.2 * Rate)];
        for (var i = 0; i < signals.Length; i++)
        {
            var played = Filter(signals[i], room.Colour[i]);
            var g = (float)Db.ToLinear(room.GainsDb[i]) * (room.Inverted.Contains(i) ? -1 : 1);
            var start = latency + room.Delays[i];
            for (var j = 0; j < played.Length; j++)
                if (start + j >= 0 && start + j < output.Length) output[start + j] += played[j] * g;
        }
        ulong seed = 7;
        for (var i = 0; i < output.Length; i++)
        {
            seed = unchecked(seed * 6364136223846793005UL + 1442695040888963407UL);
            output[i] += ((float)((long)(seed >> 11) & 0xFFFF) / 0xFFFF - 0.5f) * 2e-4f;
        }
        return output;
    }

    [Fact]
    public void MeasuresLevelsDistancesAndPolarityOfEachSpeaker()
    {
        var layout = SpeakerLayout.Surround51;
        var speakers = Enumerable.Range(0, layout.ChannelCount).Select(c => new SpeakerSweepPlan.Speaker(c, layout.Positions[c].IsLfe())).ToList();
        var plan = new SpeakerSweepPlan(Rate, speakers, sweepSeconds: 2);
        EqBand[] lowPass = [new(FilterType.LowPass, 100, q: 0.71), new(FilterType.LowPass, 100, q: 0.71)];
        var room = new Room([0, 29, -40, 96, 120, 55], [0, -0.5, 2, 4, -3, -1.5], [4],
                            [[], [new EqBand(FilterType.Peak, 80, 6, 4)], [], lowPass, [], []]);
        var recording = Record(plan, room);
        var analysis = SpeakerSweepPlan.Analyze(recording, Rate, null, speakers, sweepSeconds: 2);
        Assert.NotNull(analysis);
        Assert.Equal(6, analysis.Speakers.Count);
        var result = analysis.Speakers.ToDictionary(s => s.Channel);

        // Levels in each speaker's own band, relative to the front left.
        var levels = Enumerable.Range(0, 6).Select(c => SpeakerAlignment.Level(result[c].Response, layout.Positions[c])).ToArray();
        foreach (var c in new[] { 1, 2, 4, 5 })
            Assert.True(Math.Abs(levels[c] - levels[0] - room.GainsDb[c]) < 0.5, $"channel {c}: {levels[c] - levels[0]} dB");
        // Arrivals relative to the front left, to a few microseconds for full-range speakers.
        foreach (var c in new[] { 1, 2, 4, 5 })
        {
            Assert.True(result[c].Arrival.HasValue, $"channel {c}");
            Assert.True(Math.Abs(result[c].Arrival!.Value * Rate - room.Delays[c]) < 0.5, $"channel {c}: {result[c].Arrival * Rate} samples");
        }
        Assert.True(result[0].Arrival is { } reference && Math.Abs(reference) < 1e-5);
        // The subwoofer: later by its distance plus its low-pass filter's delay.
        Assert.True(result[3].Arrival.HasValue);
        var sub = result[3].Arrival!.Value * Rate;
        Assert.True(sub > room.Delays[3] && sub < room.Delays[3] + 0.008 * Rate, $"LFE {sub} samples");
        Assert.Null(result[3].OppositePolarity);
        // Polarity against the front left.
        Assert.True(result[4].OppositePolarity);
        foreach (var c in new[] { 0, 1, 2, 5 }) Assert.True(result[c].OppositePolarity == false, $"channel {c}");
        Assert.All(result.Values, s => Assert.True(s.SnrDb > 40, $"channel {s.Channel}: {s.SnrDb} dB"));
        // The colouring shows in the response.
        var fr = result[1].Response;
        Assert.True(fr.LevelAt(80) - fr.LevelAt(1000) > 4);
        Assert.True(analysis.PeakDb < 0 && double.IsFinite(analysis.PeakDb));
    }

    [Fact]
    public void ThePlanPlaysEachSpeakerThenTheTimingChirps()
    {
        var speakers = new List<SpeakerSweepPlan.Speaker> { new(0), new(1), new(3, IsLfe: true) };
        var plan = new SpeakerSweepPlan(Rate, speakers, sweepSeconds: 1);
        Assert.Equal([0, 0, 1, 2, 0], plan.ChirpSlots.Select(s => s.Speaker));
        Assert.Equal(SpeakerSweepPlan.PreRoll + 3 * (1 + SpeakerSweepPlan.Gap), plan.ChirpSlots[0].Start, 9);
        // The LFE's low chirp takes twice the room.
        Assert.Equal(plan.ChirpSlots[3].Start + SpeakerSweepPlan.LowChirpSpacing, plan.ChirpSlots[4].Start, 9);
        Assert.Null(plan.Playing(0.2));
        Assert.Equal<(int, bool)?>((0, false), plan.Playing(1.0));
        Assert.Equal<(int, bool)?>((1, false), plan.Playing(SpeakerSweepPlan.PreRoll + 1 + SpeakerSweepPlan.Gap + 0.5));
        Assert.Equal<(int, bool)?>((2, true), plan.Playing(plan.ChirpSlots[3].Start + 0.3));
        Assert.Null(plan.Playing(plan.Duration - 0.1));

        var signals = plan.Render();
        Assert.Equal(3, signals.Length);
        Assert.All(signals, s => Assert.Equal((int)Math.Round(plan.Duration * Rate, MidpointRounding.AwayFromZero), s.Length));
        // Each speaker is silent while another plays.
        var second = (int)(plan.SweepStart(1) * Rate);
        Assert.Equal(0, signals[0][second + 1000]);
        Assert.NotEqual(0, signals[1][second + 1000]);
    }

    [Fact]
    public void AlignmentTrimsToTheQuietestAndWaitsForTheFarthest()
    {
        var speakers = new SpeakerAlignment.Speaker[]
        {
            new(SpeakerPosition.FrontLeft, 80, 0),
            new(SpeakerPosition.FrontRight, 81.5, 0.0004),
            new(SpeakerPosition.Center, 83, -0.001),
            new(SpeakerPosition.Lfe, 92, 0.006),
            new(SpeakerPosition.SurroundLeft, 79, null),
        };
        var r = SpeakerAlignment.Align(speakers);
        Assert.Equal([-1.0, -2.5, -4.0, 0.0, 0.0], r.Select(x => x.TrimDb));
        Assert.True(Math.Abs(r[0].DelayMs - 6) < 1e-9 && Math.Abs(r[2].DelayMs - 7) < 1e-9 && r[3].DelayMs == 0 && r[4].DelayMs == 0);
        // A measurement gone wrong never asks for more than the limits.
        var wild = SpeakerAlignment.Align([new(SpeakerPosition.FrontLeft, 60, 0), new(SpeakerPosition.FrontRight, 90, 0.2)]);
        Assert.True(wild[0].DelayMs == SpeakerAlignment.MaxDelayMs && wild[1].TrimDb == -SpeakerAlignment.MaxTrimDb);
        // Nothing measured: nothing to do, and no NaN.
        var empty = SpeakerAlignment.Align([new(SpeakerPosition.Lfe, double.NaN, double.NaN)]);
        Assert.Equal(new SpeakerAlignment.Result(0, 0), empty[0]);
    }

    [Fact]
    public void AdviceNamesTheSpeakerToCheck()
    {
        var speakers = new SpeakerAdvice.Speaker[]
        {
            new(0, SpeakerPosition.FrontLeft, 80, 0, false),
            new(1, SpeakerPosition.FrontRight, 80, 0.0001, true),
            new(2, SpeakerPosition.Center, 72, 0.0002, false),
            new(3, SpeakerPosition.Lfe, 88, 0.01, null),
            new(4, SpeakerPosition.SurroundLeft, 80, null, null),
            new(5, SpeakerPosition.SurroundRight, 80, 0.008, false),
        };
        var advice = SpeakerAdvice.Advise(speakers, positions: 5, worstSnrDb: 40);
        var kinds = advice.Select(a => a.Kind).ToList();
        Assert.Contains(new SpeakerAdviceKind.Polarity(1), kinds);
        Assert.Contains(new SpeakerAdviceKind.Silent(4), kinds);
        Assert.Contains(new SpeakerAdviceKind.Level(2, -8), kinds);
        Assert.Contains(new SpeakerAdviceKind.Subwoofer(3, 8), kinds);
        Assert.Contains(kinds, k => k is SpeakerAdviceKind.Distance { Channel: 5, Meters: > 2.5 });
        Assert.Equal(RoomAdviceSeverity.Warning, advice[0].Severity);
        Assert.DoesNotContain(kinds, k => k is SpeakerAdviceKind.Noisy or SpeakerAdviceKind.FewPositions);
        Assert.Equal(advice.Count, advice.Select(a => a.Id).Distinct().Count());

        var few = SpeakerAdvice.Advise(speakers, positions: 1, worstSnrDb: 20);
        Assert.Contains(new SpeakerAdviceKind.Noisy(20), few.Select(a => a.Kind));
        Assert.Contains(new SpeakerAdviceKind.FewPositions(1), few.Select(a => a.Kind));
        // Most severe first, in their order otherwise.
        Assert.Equal(few.OrderByDescending(a => a.Severity).Select(a => a.Id), few.Select(a => a.Id));
    }

    [Fact]
    public void TheLfeFitNeverBoostsAbove120Hz()
    {
        var grid = EqFitter.Grid;
        // A subwoofer: a mode at 45 Hz, a dip at 70 Hz, rolling off above 100 Hz.
        var sub = ResponseCurve.ResponseDb([new EqBand(FilterType.Peak, 45, 8, 5), new EqBand(FilterType.Peak, 70, -5, 4),
                                            new EqBand(FilterType.LowPass, 100, q: 0.71), new EqBand(FilterType.LowPass, 100, q: 0.71),
                                            new EqBand(FilterType.HighPass, 22, q: 0.71)], grid, 48_000);
        var options = new EqFitter.Options(maxFilters: 6)
        {
            CorrectOnlyBelow = SpeakerSetup.LfeBoostCeiling,
            NoBoostAbove = SpeakerSetup.LfeBoostCeiling,
            LevelMatchRange = (30, 80),
            MaxBoostDb = 3,
            MaxQ = 10,
        };
        var result = EqFitter.Fit(new FrequencyResponse([.. grid], sub.Select(v => 90 + v).ToArray()), RoomResponse.FlatTarget(), options);
        var eq = ResponseCurve.ResponseDb(result.Bands, [45.0, 150, 300, 1000], 48_000);
        Assert.True(eq[0] < -5, $"mode not cut: {eq[0]}");
        Assert.True(eq[1] < 0.5 && eq[2] < 0.5 && Math.Abs(eq[3]) < 0.5, $"boost above 120 Hz: {string.Join(", ", eq)}");
        Assert.DoesNotContain(result.Bands, b => b.Gain > 0 && b.Frequency > 120 && b.Type != FilterType.LowShelf);
    }

    [Fact]
    public void FitterOptionsCompareTheNewLimits()
    {
        var a = new EqFitter.Options { NoBoostAbove = 120 };
        Assert.NotEqual(a, new EqFitter.Options());
        Assert.NotEqual(a, a with { LevelMatchRange = (30, 80) });
        Assert.Equal(a, new EqFitter.Options { NoBoostAbove = 120 });
        Assert.Equal(a.GetHashCode(), new EqFitter.Options { NoBoostAbove = 120 }.GetHashCode());
        Assert.Equal((200.0, 1200.0), new EqFitter.Options().LevelMatchRange);
    }
}

// MARK: - Settings and text

public class SpeakerSettingsTests
{
    private static SpeakerSetup Sample() => new(
        eqOnAllSpeakers: false,
        corrections: [new SpeakerCorrection(2, SpeakerPosition.Center, [new EqBand(FilterType.Peak, 63, -6, 4)], trimDb: -1.5, delayMs: 2.25)],
        layout: SpeakerLayout.Surround51);

    [Fact]
    public void OlderSettingsDecodeWithoutSpeakers()
    {
        var s = JsonSerializer.Deserialize<DspSettings>("""{"eqEnabled":true,"preampDB":0,"autoPreamp":true,"bands":[]}""", TaplineJson.Options)!;
        Assert.Equal(new SpeakerSetup(), s.Speakers);
        Assert.True(s.Speakers.EqOnAllSpeakers && s.Speakers.CorrectionEnabled && s.Speakers.Corrections.Count == 0 && s.Speakers.Layout == null);
        var t = s.Clone();
        t.Speakers = Sample();
        var back = JsonSerializer.Deserialize<DspSettings>(JsonSerializer.Serialize(t, TaplineJson.Options), TaplineJson.Options)!;
        Assert.Equal(t, back);
        Assert.Equal(t.GetHashCode(), back.GetHashCode());
        Assert.NotEqual(s, t);

        // Partial and null values keep the defaults.
        var partial = JsonSerializer.Deserialize<DspSettings>("""{"speakers": {"eqOnAllSpeakers": false, "corrections": null}}""", TaplineJson.Options)!;
        Assert.False(partial.Speakers.EqOnAllSpeakers);
        Assert.True(partial.Speakers.CorrectionEnabled);
        Assert.Empty(partial.Speakers.Corrections);
        Assert.Equal(new SpeakerSetup(), JsonSerializer.Deserialize<DspSettings>("""{"speakers": null}""", TaplineJson.Options)!.Speakers);
    }

    [Fact]
    public void JsonUsesTheSwiftKeys()
    {
        var s = new DspSettings { Speakers = Sample() };
        var node = JsonNode.Parse(JsonSerializer.Serialize(s, TaplineJson.Options))!["speakers"]!.AsObject();
        Assert.Equal(["eqOnAllSpeakers", "correctionEnabled", "corrections", "layout"], node.Select(p => p.Key));
        var correction = node["corrections"]![0]!.AsObject();
        Assert.Equal(["channel", "position", "bands", "trimDB", "delayMS"], correction.Select(p => p.Key));
        Assert.Equal("center", (string?)correction["position"]);
        Assert.Equal(-1.5, (double)correction["trimDB"]!);
        Assert.Equal("surroundRight", (string?)node["layout"]!["positions"]![5]);
        // No layout: left out, as Swift's encoder does.
        Assert.DoesNotContain("layout", JsonSerializer.Serialize(new SpeakerSetup(), TaplineJson.Options));

        // What Tapline for macOS writes.
        const string swift = """
            {"speakers":{"eqOnAllSpeakers":false,"correctionEnabled":true,"layout":{"positions":["frontLeft","frontRight","center","lfe","surroundLeft","surroundRight"]},
             "corrections":[{"channel":2,"position":"center","trimDB":-1.5,"delayMS":2.25,
               "bands":[{"id":"6F9619FF-8B86-D011-B42D-00C04FC964FF","type":"peak","frequency":63,"gain":-6,"q":4,"isEnabled":true,"channels":"both"}]}]}}
            """;
        var decoded = JsonSerializer.Deserialize<DspSettings>(swift, TaplineJson.Options)!.Speakers;
        var expected = Sample();
        expected.Corrections[0].Bands[0] = expected.Corrections[0].Bands[0] with { Id = Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF") };
        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void ClonesAreIndependent()
    {
        var s = new DspSettings { Speakers = Sample() };
        var copy = s.Clone();
        Assert.Equal(s, copy);
        copy.Speakers.Corrections[0].TrimDb = -3;
        copy.Speakers.Corrections[0].Bands.Add(new EqBand(FilterType.Peak, 100, -2, 2));
        copy.Speakers.EqOnAllSpeakers = true;
        Assert.Equal(-1.5, s.Speakers.Corrections[0].TrimDb);
        Assert.Single(s.Speakers.Corrections[0].Bands);
        Assert.False(s.Speakers.EqOnAllSpeakers);
        Assert.NotEqual(s, copy);
    }

    [Fact]
    public void CorrectionsOnlyPlayOnTheirLayoutAndWhenEnabled()
    {
        var setup = Sample();
        Assert.Single(setup.ActiveCorrections(SpeakerLayout.Surround51));
        Assert.Single(setup.ActiveCorrections(SpeakerLayout.FromChannelMask(0x60F, 6)));
        Assert.Empty(setup.ActiveCorrections(SpeakerLayout.Surround71));
        Assert.Empty(setup.ActiveCorrections(null));
        setup.CorrectionEnabled = false;
        Assert.Empty(setup.ActiveCorrections(SpeakerLayout.Surround51));
        Assert.Single(setup.Corrections);
    }

    [Fact]
    public void HeadroomIsSharedByEverySpeaker()
    {
        SpeakerCorrection[] corrections =
        [
            new(0, SpeakerPosition.FrontLeft, [new EqBand(FilterType.Peak, 50, 3, 2)]),
            new(2, SpeakerPosition.Center, [new EqBand(FilterType.Peak, 80, -6, 2)]),
        ];
        Assert.True(Math.Abs(SpeakerSetup.HeadroomDb(corrections) + 3) < 0.05);
        Assert.True(Math.Abs(SpeakerSetup.HeadroomDb([new SpeakerCorrection(1, SpeakerPosition.FrontRight, [new EqBand(FilterType.Peak, 80, -6, 2)])])) < 1e-9);
        // A band set to one side still plays on its speaker.
        Assert.True(SpeakerSetup.HeadroomDb([new SpeakerCorrection(1, SpeakerPosition.FrontRight, [new EqBand(FilterType.Peak, 80, 4, 2, channels: EqChannels.Left)])]) < -3.9);
    }

    [Fact]
    public void LfeChannelsLeaveOutBoostsAbove120Hz()
    {
        Assert.True(SpeakerSetup.SkipsOnLfe(new EqBand(FilterType.Peak, 1000, 6, 1)));
        Assert.True(SpeakerSetup.SkipsOnLfe(new EqBand(FilterType.HighShelf, 10_000, 4, 0.7)));
        Assert.False(SpeakerSetup.SkipsOnLfe(new EqBand(FilterType.LowShelf, 105, 6, 0.7)));
        Assert.False(SpeakerSetup.SkipsOnLfe(new EqBand(FilterType.Peak, 300, -6, 2)));
        Assert.False(SpeakerSetup.SkipsOnLfe(new EqBand(FilterType.Peak, 100, 6, 2)));
        Assert.False(SpeakerSetup.SkipsOnLfe(new EqBand(FilterType.LowPass, 5000, 6, 0.7)));
    }

    [Fact]
    public void FiltersAsTextHaveASectionPerSpeaker()
    {
        var text = ParametricEqFormat.ExportSpeakers(
        [
            new SpeakerCorrection(2, SpeakerPosition.Center, [new EqBand(FilterType.Peak, 63, -6, 4)], trimDb: -1.5, delayMs: 1.25),
            new SpeakerCorrection(0, SpeakerPosition.FrontLeft, [new EqBand(FilterType.Peak, 47, -4.5, 5), new EqBand(FilterType.Peak, 200, 2, 2, isEnabled: false)]),
        ], "5.1", -2);
        Assert.Equal("""
            # Room correction for 5.1 speakers, one section per speaker.

            # L (channel 1)
            Channel: 1
            Preamp: -2.0 dB
            Filter 1: ON PK Fc 47 Hz Gain -4.5 dB Q 5.00

            # C (channel 3)
            Channel: 3
            Preamp: -3.5 dB
            Delay: 1.25 ms
            Filter 1: ON PK Fc 63 Hz Gain -6.0 dB Q 4.00

            Channel: all

            """.Replace("\r\n", "\n"), text);
    }
}

// MARK: - Equalizer APO configuration

/// Interprets the configuration of a multichannel output the way Equalizer APO does (as
/// ApoConfigTests does for stereo), as the transfer from every input channel to every output channel,
/// and checks it against the macOS kernel's multichannel processing.
public class MultichannelApoConfigTests
{
    private const double SampleRate = 48_000;
    private static readonly SpeakerLayout Atmos = SpeakerLayout.FromChannelMask(0x2D63F, 12);
    // Channels of Windows' 7.1.4: L R C LFE Lrs Rrs Ls Rs Ltf Rtf Ltr Rtr.
    private const int Left = 0, Right = 1, Center = 2, Sub = 3, RearLeft = 4, SideLeft = 6, TopRearRight = 11;

    private static readonly Regex TypeRegex = new(@"^\s*ON\s+([A-Za-z]+)");
    private static readonly Regex FreqRegex = new(@"\s+Fc\s*([-+0-9.eE]+)\s*H\s*z");
    private static readonly Regex GainRegex = new(@"\s+Gain\s*([-+0-9.eE]+)\s*dB");
    private static readonly Regex QRegex = new(@"\s+Q\s*([-+0-9.eE]+)");

    private static double Parse(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>[output channel][input channel] at <paramref name="f"/>.</summary>
    private static Complex[][] Interpret(string config, double f, int channels = 12)
    {
        var all = Enumerable.Range(1, channels).Select(i => "C" + i).ToList();
        var state = new Dictionary<string, Complex[]>();
        for (var i = 0; i < channels; i++)
        {
            var v = new Complex[channels];
            v[i] = 1;
            state[all[i]] = v;
        }
        var selected = new List<string>(all);

        string Resolve(string word, List<string> names)
        {
            if (!char.IsAsciiDigit(word[0])) return word;
            var index = int.Parse(word, CultureInfo.InvariantCulture) - 1;
            Assert.InRange(index, 0, names.Count - 1);
            return names[index];
        }
        void Scale(Complex factor)
        {
            foreach (var c in selected) state[c] = state[c].Select(v => v * factor).ToArray();
        }

        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            Assert.True(colon > 0, line);
            var command = line[..colon].Trim();
            var parameters = line[(colon + 1)..];
            switch (command)
            {
                case "Device":
                    break;
                case "Channel":
                    selected = parameters.Trim() == "all"
                        ? [.. all]
                        : [.. parameters.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => Resolve(w, all))];
                    Assert.NotEmpty(selected);
                    Assert.All(selected, s => Assert.True(state.ContainsKey(s), s));
                    break;
                case "Preamp":
                    Scale(Math.Pow(10, Parse(Regex.Match(parameters, @"[-+0-9.]+").Value) / 20));
                    break;
                case "Delay":
                    Assert.Contains("ms", parameters);
                    Scale(Complex.Exp(new Complex(0, -2 * Math.PI * f * Parse(Regex.Match(parameters, @"[-+0-9.]+").Value) / 1000)));
                    break;
                case "Copy":
                {
                    var next = new Dictionary<string, Complex[]>(state);
                    var targets = new List<string>();
                    foreach (var assignment in parameters.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = assignment.Split('=');
                        Assert.Equal(2, parts.Length);
                        var target = Resolve(parts[0], selected);
                        var sum = new Complex[channels];
                        foreach (var summand in parts[1].Split('+'))
                        {
                            var factors = summand.Split('*');
                            Assert.Contains('.', factors[0]);
                            if (factors.Length == 1) continue;
                            Assert.Equal(2, factors.Length);
                            var source = Resolve(factors[1], selected);
                            Assert.True(state.ContainsKey(source), source);
                            for (var i = 0; i < channels; i++) sum[i] += Parse(factors[0]) * state[source][i];
                        }
                        next[target] = sum;
                        targets.Add(target);
                        if (!all.Contains(target)) all.Add(target);
                    }
                    state = next;
                    selected = targets;
                    break;
                }
                default:
                {
                    Assert.StartsWith("Filter", command);
                    var m = TypeRegex.Match(parameters);
                    Assert.True(m.Success, line);
                    var rest = parameters[m.Length..];
                    var type = m.Groups[1].Value switch
                    {
                        "PK" => FilterType.Peak, "LSC" => FilterType.LowShelf, "HSC" => FilterType.HighShelf,
                        "LPQ" => FilterType.LowPass, "HPQ" => FilterType.HighPass, "NO" => FilterType.Notch, "BP" => FilterType.BandPass,
                        var other => throw new Xunit.Sdk.XunitException("unexpected filter type " + other),
                    };
                    var fc = Parse(FreqRegex.Match(rest).Groups[1].Value);
                    var gain = GainRegex.Match(rest);
                    var q = QRegex.Match(rest);
                    Assert.True(q.Success, line);
                    var c0 = BiquadCoefficients.Design(type, fc, gain.Success ? Parse(gain.Groups[1].Value) : 0, Parse(q.Groups[1].Value), SampleRate);
                    var z1 = Complex.Exp(new Complex(0, -2 * Math.PI * f / SampleRate));
                    Scale((c0.B0 + c0.B1 * z1 + c0.B2 * z1 * z1) / (1 + c0.A1 * z1 + c0.A2 * z1 * z1));
                    break;
                }
            }
        }
        return [.. Enumerable.Range(1, channels).Select(i => state["C" + i])];
    }

    private static string Build(DspSettings s, SpeakerLayout? layout, int channels = 12, bool levelMatchedBypass = true,
                                ApoConfig.SpeakerFiles? files = null, DeviceSimulation? simulation = null) =>
        ApoConfig.Build(true, [new ApoConfig.Device("{00000000-1111-2222-3333-444444444444}", "Receiver", channels, SampleRate, s, files, simulation, layout)],
                        levelMatchedBypass);

    private static double Db(Complex c) => 20 * Math.Log10(Math.Max(c.Magnitude, 1e-15));

    private static double Response(IEnumerable<EqBand> bands, double f) => ResponseCurve.ResponseDb(bands, [f], SampleRate)[0];

    /// <summary>Every output channel hears only its own input.</summary>
    private static void AssertNoCrosstalk(Complex[][] t, params int[] except)
    {
        for (var o = 0; o < t.Length; o++)
            for (var i = 0; i < t[o].Length; i++)
                if (o != i && !(except.Contains(o) && except.Contains(i))) Assert.True(t[o][i].Magnitude < 1e-12, $"{i + 1} → {o + 1}");
    }

    [Fact]
    public void TheEqPlaysOnEverySpeakerWithoutItsBoostsAbove120HzOnTheLfe()
    {
        var s = DspSettings.Flat;
        s.Bands =
        [
            new EqBand(FilterType.Peak, 1000, 6, 1),
            new EqBand(FilterType.LowShelf, 105, 4, 0.7),
            new EqBand(FilterType.Peak, 300, -6, 2),
            new EqBand(FilterType.HighShelf, 8000, -3, 0.7),
            new EqBand(FilterType.Peak, 2000, 3, 1, channels: EqChannels.Right),
            new EqBand(FilterType.Peak, 60, -4, 3, channels: EqChannels.Left),
        ];
        s.Tone = new ToneSettings(air: 2);
        var config = Build(s, Atmos);
        Assert.Contains("# 7.1.4 speakers: 1 L, 2 R, 3 C, 4 LFE, 5 Lrs, 6 Rrs, 7 Ls, 8 Rs, 9 Ltf, 10 Rtf, 11 Ltr, 12 Rtr", config);
        Assert.Contains("# Not on the LFE", config);
        var preamp = s.EffectivePreampDb(SampleRate);
        Assert.True(preamp < -5);
        var active = s.ActiveBands;
        var shared = active.Where(b => b.Channels == EqChannels.Both).ToList();
        var lfe = shared.Where(b => !SpeakerSetup.SkipsOnLfe(b)).ToList();
        foreach (var f in new[] { 40.0, 105, 300, 1000, 2000, 8000, 12_000 })
        {
            var t = Interpret(config, f);
            AssertNoCrosstalk(t);
            Assert.InRange(Db(t[Left][Left]) - (ResponseCurve.ResponseDb(active, [f], SampleRate, EqChannels.Left)[0] + preamp), -0.01, 0.01);
            Assert.InRange(Db(t[Right][Right]) - (ResponseCurve.ResponseDb(active, [f], SampleRate, EqChannels.Right)[0] + preamp), -0.01, 0.01);
            foreach (var c in new[] { Center, RearLeft, SideLeft, TopRearRight })
                Assert.InRange(Db(t[c][c]) - (Response(shared, f) + preamp), -0.01, 0.01);
            Assert.InRange(Db(t[Sub][Sub]) - (Response(lfe, f) + preamp), -0.01, 0.01);
        }
        // The cuts and the low shelf still play on the LFE channel; the boosts at 1 kHz and in the air do not.
        Assert.True(Db(Interpret(config, 1000)[Sub][Sub]) - preamp < 0.5);
        Assert.True(Db(Interpret(config, 1000)[Center][Center]) - preamp > 5);
    }

    [Fact]
    public void TheEqCanPlayOnTheFrontPairOnly()
    {
        var s = DspSettings.Flat;
        s.Bands = [new EqBand(FilterType.Peak, 1000, 6, 1)];
        s.AutoPreamp = false;
        s.PreampDb = -2;
        s.Speakers.EqOnAllSpeakers = false;
        var config = Build(s, Atmos);
        Assert.Contains("Channel: 1 2", config);
        var t = Interpret(config, 1000);
        AssertNoCrosstalk(t);
        Assert.InRange(Db(t[Left][Left]) - 4, -0.01, 0.01);
        Assert.InRange(Db(t[Right][Right]) - 4, -0.01, 0.01);
        for (var c = 2; c < 12; c++) Assert.True((t[c][c] - 1).Magnitude < 1e-12, $"channel {c + 1}");
    }

    [Fact]
    public void TheBypassIsLevelMatchedOnTheSameSpeakersAsTheEq()
    {
        var s = DspSettings.Flat;
        s.EqEnabled = false;
        s.Bands = [new EqBand(FilterType.LowShelf, 105, 6, 0.7), new EqBand(FilterType.Peak, 1000, 6, 1)];
        var expected = s.BypassGainDb(SampleRate);
        Assert.InRange(expected, -6, -2);
        var everywhere = Interpret(Build(s, Atmos), 1000);
        Assert.DoesNotContain("Filter", Build(s, Atmos));
        for (var c = 0; c < 12; c++) Assert.InRange(Db(everywhere[c][c]) - expected, -0.006, 0.006);
        s.Speakers.EqOnAllSpeakers = false;
        var front = Interpret(Build(s, Atmos), 1000);
        Assert.InRange(Db(front[Left][Left]) - expected, -0.006, 0.006);
        Assert.True((front[Center][Center] - 1).Magnitude < 1e-12);
        Assert.DoesNotContain("Preamp", Build(s, Atmos, levelMatchedBypass: false));
    }

    [Fact]
    public void StereoEffectsActOnTheFrontPairAndHeadphoneEffectsStayOff()
    {
        var s = DspSettings.Flat;
        s.Effects = new EffectsSettings
        {
            Width = 0.4, SwapChannels = true, Balance = 0.3, OutputGainDb = -3,
            CrossfeedEnabled = true, Crossfeed = 1, VirtualSpeakersEnabled = true,
        };
        var files = new ApoConfig.SpeakerFiles(@"Tapline\a.wav", @"Tapline\b.wav", @"Tapline\c.wav", @"Tapline\d.wav");
        var config = Build(s, Atmos, files: files, simulation: SpeakerProfile.Laptop.Simulation());
        Assert.DoesNotContain("Convolution", config);
        Assert.DoesNotContain("VLL", config);
        Assert.DoesNotContain("XL", config);
        Assert.DoesNotContain("Listen as", config);
        Assert.DoesNotContain("HPQ", config);

        var t = Interpret(config, 1000);
        AssertNoCrosstalk(t, Left, Right);
        // The macOS kernel: swap, width, balance, then the output gain on every speaker.
        var g = Math.Pow(10, -3 / 20.0);
        (double L, double R) Kernel(double l, double r)
        {
            (l, r) = (r, l);
            var m = 0.5 * (l + r);
            var side = 0.5 * (l - r) * 0.4;
            (l, r) = (m + side, m - side);
            l *= 0.7;
            return (l * g, r * g);
        }
        var fromL = Kernel(1, 0);
        var fromR = Kernel(0, 1);
        Assert.Equal(fromL.L, t[Left][Left].Real, 4);
        Assert.Equal(fromL.R, t[Right][Left].Real, 4);
        Assert.Equal(fromR.L, t[Left][Right].Real, 4);
        Assert.Equal(fromR.R, t[Right][Right].Real, 4);
        for (var c = 2; c < 12; c++) Assert.InRange(Db(t[c][c]) + 3, -0.01, 0.01);
    }

    private static List<SpeakerCorrection> Corrections() =>
    [
        new(Center, SpeakerPosition.Center, [new EqBand(FilterType.Peak, 63, -6, 4), new EqBand(FilterType.Peak, 1000, 3, 2)], trimDb: -1.5, delayMs: 1.25),
        new(SideLeft, SpeakerPosition.SurroundLeft, trimDb: -3, delayMs: 2.5),
        new(Sub, SpeakerPosition.Lfe, [new EqBand(FilterType.Peak, 45, -8, 5), new EqBand(FilterType.Peak, 30, 0, 2)], delayMs: 0),
    ];

    [Fact]
    public void EachSpeakerGetsItsCorrectionTrimAndDelayLast()
    {
        var s = DspSettings.Flat;
        s.Bands = [new EqBand(FilterType.Peak, 250, -3, 1)];
        s.Speakers.Layout = Atmos;
        s.Speakers.Corrections = Corrections();
        var config = Build(s, Atmos);
        var headroom = SpeakerSetup.HeadroomDb(s.Speakers.Corrections, SampleRate);
        Assert.InRange(headroom, -3.05, -2.9);
        Assert.Contains("Delay: 1.25 ms", config);
        Assert.Contains("Delay: 2.5 ms", config);
        Assert.Equal(2, Regex.Matches(config, "Delay:").Count);
        Assert.Contains($"Preamp: {(-1.5 + headroom).ToString("0.##", CultureInfo.InvariantCulture)} dB", config);
        // After the EQ, the effects and the output gain.
        Assert.True(config.IndexOf("# Room correction", StringComparison.Ordinal) > config.IndexOf("Fc 250 Hz", StringComparison.Ordinal));
        // A 0 dB bell does nothing and is left out.
        Assert.DoesNotContain("Fc 30 Hz", config);
        Assert.EndsWith("Channel: all" + Environment.NewLine, config);

        var eq = s.ActiveBands;
        var plain = DspSettings.Flat;
        plain.Bands = s.Bands;
        foreach (var f in new[] { 45.0, 63, 250, 1000, 4000 })
        {
            var t = Interpret(config, f);
            AssertNoCrosstalk(t);
            Complex Phase(double ms) => Complex.Exp(new Complex(0, -2 * Math.PI * f * ms / 1000));
            // The front left has no correction of its own: the shared headroom only, no delay.
            Assert.InRange(Db(t[Left][Left]) - (Response(eq, f) + headroom), -0.01, 0.01);
            var uncorrected = Interpret(Build(plain, Atmos), f)[Left][Left];
            Assert.True((t[Left][Left] - uncorrected * Tapline.Core.Db.ToLinear(headroom)).Magnitude < 1e-3 * uncorrected.Magnitude, $"{f} Hz");
            var center = Corrections()[0];
            var expectedCenter = Response(eq, f) + Response(center.Bands, f) + center.TrimDb + headroom;
            Assert.InRange(Db(t[Center][Center]) - expectedCenter, -0.01, 0.01);
            // Its delay, against the same speaker without one.
            var undelayed = s.Clone();
            undelayed.Speakers.Corrections[0].DelayMs = 0;
            var reference = Interpret(Build(undelayed, Atmos), f)[Center][Center];
            Assert.True((t[Center][Center] - reference * Phase(1.25)).Magnitude < 1e-6 * (1 + reference.Magnitude), $"{f} Hz");
            Assert.InRange(Db(t[SideLeft][SideLeft]) - (Response(eq, f) - 3 + headroom), -0.01, 0.01);
            Assert.InRange(Db(t[Sub][Sub]) - (Response(eq, f) + Response(Corrections()[2].Bands, f) + headroom), -0.01, 0.01);
        }
    }

    [Fact]
    public void CorrectionsOnlyPlayOnTheirLayoutAndWhenOn()
    {
        var s = DspSettings.Flat;
        s.Speakers.Corrections = Corrections();
        // Measured on macOS's 7.1.4: the same speakers in another channel order.
        s.Speakers.Layout = SpeakerLayout.Atmos714;
        var other = Build(s, Atmos);
        Assert.DoesNotContain("Delay:", other);
        Assert.DoesNotContain("# Room correction", other);
        Assert.DoesNotContain("Preamp", other);
        s.Speakers.Layout = Atmos;
        s.Speakers.CorrectionEnabled = false;
        var off = Build(s, Atmos);
        Assert.Equal(other, off);
        s.Speakers.CorrectionEnabled = true;
        Assert.Contains("Delay: 2.5 ms", Build(s, Atmos));
        // A stereo output never plays them.
        Assert.DoesNotContain("Delay:", Build(s, SpeakerLayout.FromChannelMask(0x3, 2), channels: 2));
    }

    [Fact]
    public void StereoOutputsAndInterfacesAreUnchanged()
    {
        var s = DspSettings.Flat;
        s.Bands = [new EqBand(FilterType.Peak, 1000, 3, 1), new EqBand(FilterType.Peak, 68, -4, 4, channels: EqChannels.Left)];
        s.Effects = new EffectsSettings { CrossfeedEnabled = true, Width = 1.2, Balance = 0.1, OutputGainDb = -2 };
        s.Speakers = new SpeakerSetup(eqOnAllSpeakers: false, corrections: Corrections(), layout: Atmos);
        Assert.Equal(Build(s, null, channels: 2), Build(s, SpeakerLayout.FromChannelMask(0x3, 2), channels: 2));
        var interfaceLayout = new SpeakerLayout([SpeakerPosition.FrontLeft, SpeakerPosition.FrontRight, .. Enumerable.Repeat(SpeakerPosition.Unknown, 8)]);
        Assert.Equal(Build(s, null, channels: 10), Build(s, interfaceLayout, channels: 10));
        // A layout for another channel count than the output has.
        Assert.Equal(Build(s, null, channels: 6), Build(s, Atmos, channels: 6));
        Assert.Contains("3=1.0*3", Build(s, null, channels: 6));
        Assert.Contains("XL", Build(s, null, channels: 6));
    }

    [Fact]
    public void ChannelsWithoutASpeakerPassThroughUntouched()
    {
        var layout = new SpeakerLayout([SpeakerPosition.FrontLeft, SpeakerPosition.FrontRight, SpeakerPosition.Center, SpeakerPosition.Lfe,
                                        SpeakerPosition.SurroundLeft, SpeakerPosition.SurroundRight, SpeakerPosition.Unknown, SpeakerPosition.Unknown]);
        var s = DspSettings.Flat;
        s.Bands = [new EqBand(FilterType.Peak, 1000, 6, 1)];
        s.Effects = new EffectsSettings { OutputGainDb = -6, Width = 1.5 };
        s.Speakers.Layout = layout;
        s.Speakers.Corrections = [new SpeakerCorrection(2, SpeakerPosition.Center, [new EqBand(FilterType.Peak, 80, 4, 2)], delayMs: 3),
                                  new SpeakerCorrection(6, SpeakerPosition.Unknown, trimDb: -10)];
        var config = Build(s, layout, channels: 8);
        Assert.Contains("Channel: 1 2 3 4 5 6", config);
        var t = Interpret(config, 1000, channels: 8);
        AssertNoCrosstalk(t, Left, Right);
        Assert.True((t[6][6] - 1).Magnitude < 1e-12);
        Assert.True((t[7][7] - 1).Magnitude < 1e-12);
        var expected = Response(s.ActiveBands, 1000) + s.EffectivePreampDb(SampleRate) - 6 + SpeakerSetup.HeadroomDb(s.Speakers.Corrections, SampleRate)
                       + Response(s.Speakers.Corrections[0].Bands, 1000);
        Assert.InRange(Db(t[Center][Center]) - expected, -0.01, 0.01);
    }

    [Fact]
    public void NoNaNReachesTheConfiguration()
    {
        var s = DspSettings.Flat;
        s.AutoPreamp = false;
        s.PreampDb = double.NaN;
        s.Bands = [new EqBand(FilterType.Peak, double.NaN, double.NaN, double.NaN), new EqBand(FilterType.HighShelf, double.PositiveInfinity, 30, 0)];
        s.Effects = new EffectsSettings { Width = double.NaN, Balance = double.NaN, OutputGainDb = double.NaN };
        s.Speakers.Layout = Atmos;
        s.Speakers.Corrections = Enumerable.Range(0, 12).Select(c => new SpeakerCorrection(c, Atmos.Positions[c],
            [new EqBand(FilterType.Peak, 20, 24, 18), new EqBand(FilterType.Peak, double.NaN, double.PositiveInfinity, -1)],
            trimDb: c % 2 == 0 ? double.NaN : double.NegativeInfinity, delayMs: c % 3 == 0 ? double.PositiveInfinity : double.NaN)).ToList();
        foreach (var eqOn in new[] { true, false })
        {
            s.EqEnabled = eqOn;
            var config = Build(s, Atmos);
            Assert.DoesNotContain("NaN", config);
            Assert.DoesNotContain("Infinity", config);
            Assert.DoesNotContain("∞", config);
            Assert.Contains("Delay: 50 ms", config);
            var t = Interpret(config, 1000);
            Assert.All(t.SelectMany(row => row), v => Assert.True(double.IsFinite(v.Real) && double.IsFinite(v.Imaginary)));
        }
    }
}
