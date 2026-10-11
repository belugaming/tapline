using System.Text.Json;
using System.Text.Json.Nodes;
using Tapline.Core;

namespace Tapline.Core.Tests;

/// <summary>Ports of the macOS 0.2.2 fixes: non-finite numbers and settings this version cannot read.</summary>
public class NonFiniteTests
{
    [Fact]
    public void ClampingTurnsNaNIntoSafeValues()
    {
        var band = new EqBand(FilterType.Peak, double.NaN, double.NaN, double.NaN).Clamped();
        Assert.True(band.Frequency == 1000 && band.Gain == 0 && band.Q == 1);
        Assert.Equal(-1, double.NaN.Clamp(-1, 1));
        Assert.Equal(-1, double.NaN.Clamp((-1, 1)));
        Assert.Equal(1, 5.0.Clamp(-1, 1));
        Assert.Equal(0.5, 0.5.Clamp(-1, 1));

        var s = new DspSettings { AutoPreamp = false, PreampDb = 40 };
        Assert.Equal(12, s.EffectivePreampDb());
        s.PreampDb = double.NaN;
        Assert.True(double.IsFinite(s.EffectivePreampDb()));
        s.AutoPreamp = true;
        s.Bands = [new EqBand(FilterType.Peak, 1000, double.NaN, 1)];
        Assert.True(double.IsFinite(s.EffectivePreampDb()));
    }

    [Fact]
    public void SkipsNonFiniteNumbers()
    {
        // .NET reads "NaN", "Infinity" and "1e999" as numbers; none of them may become a filter or a preamp.
        var parsed = ParametricEqFormat.Parse("""
            Preamp: NaN dB
            Filter 1: ON PK Fc NaN Hz Gain 3 dB Q 1
            Filter 2: ON PK Fc 1000 Hz Gain Infinity dB Q 1
            Filter 3: ON PK Fc 1000 Hz Gain 3 dB Q 1e999
            Filter 4: ON PK Fc 2000 Hz Gain -2 dB Q 1.5
            """);
        Assert.Null(parsed.PreampDb);
        var band = Assert.Single(parsed.Bands);
        Assert.Equal(2000, band.Frequency);
        var e = Assert.Throws<ParametricEqParseException>(() => ParametricEqFormat.Parse("Filter 1: ON PK Fc -Infinity Hz Gain 3 dB Q 1"));
        Assert.Equal(ParametricEqParseError.NoFilters, e.Error);
    }

    [Fact]
    public void ConfigurationNeverHoldsNaN()
    {
        var s = new DspSettings
        {
            AutoPreamp = true,
            Bands = [new EqBand(FilterType.Peak, double.NaN, double.NaN, double.NaN), new EqBand(FilterType.Peak, 1000, 3, 1)],
        };
        s.Effects = s.Effects with { OutputGainDb = double.NaN, Balance = double.NaN, Width = double.NaN, Crossfeed = double.NaN };
        var stereo = ApoConfig.Build(true, [new ApoConfig.Device("{guid}", "Speakers", 2, 48_000, s)]);
        Assert.DoesNotContain("NaN", stereo);
        Assert.DoesNotContain("∞", stereo);

        s.Effects = s.Effects with { CrossfeedEnabled = true };
        s.AutoPreamp = false;
        s.PreampDb = double.PositiveInfinity;
        var crossfeed = ApoConfig.Build(true, [new ApoConfig.Device("{guid}", "Headphones", 2, 48_000, s)]);
        Assert.DoesNotContain("NaN", crossfeed);
        Assert.DoesNotContain("∞", crossfeed);
        Assert.Contains("Preamp: 12 dB", crossfeed);

        var mono = ApoConfig.Build(true, [new ApoConfig.Device("{guid}", "Mono", 1, 48_000, s)]);
        Assert.DoesNotContain("NaN", mono);
    }
}

public class SettingsStorageTests
{
    private static readonly JsonSerializerOptions Options = TaplineJson.Options;

    [Fact]
    public void KeepsEntriesItCannotRead()
    {
        var speakers = JsonSerializer.Serialize(new DspSettings(), Options);
        // "future": saved by a newer version with a filter type this one does not know.
        var stored = JsonNode.Parse($$"""{"speakers": {{speakers}}, "future": {"bands": [{"type": "allPass"}]}}""");
        var store = new TolerantJson();
        var profiles = store.DecodeObject<DspSettings>(stored, Options);
        Assert.NotNull(profiles);
        Assert.Equal(["speakers"], profiles.Keys);
        Assert.Equal(1, store.UnreadableCount);

        // Saving writes the unreadable entry back as it was...
        var saved = store.EncodeObject(profiles, Options);
        Assert.Equal(["future", "speakers"], saved.Select(kv => kv.Key).Order());
        var reread = new TolerantJson();
        var again = reread.DecodeObject<DspSettings>(JsonNode.Parse(saved.ToJsonString()), Options);
        Assert.NotNull(again);
        Assert.Equal(profiles["speakers"], again["speakers"]);
        Assert.Equal(1, reread.UnreadableCount);

        // ...until a new value takes its key.
        var replaced = new Dictionary<string, DspSettings>(profiles) { ["future"] = new DspSettings() };
        var fresh = new TolerantJson();
        var all = fresh.DecodeObject<DspSettings>(JsonNode.Parse(store.EncodeObject(replaced, Options).ToJsonString()), Options);
        Assert.NotNull(all);
        Assert.Equal(2, all.Count);
        Assert.Equal(0, fresh.UnreadableCount);

        // Not an object at all.
        Assert.Null(new TolerantJson().DecodeObject<DspSettings>(JsonNode.Parse("[1, 2]"), Options));
    }

    [Fact]
    public void KeepsElementsOfAListItCannotRead()
    {
        var band = JsonSerializer.Serialize(new EqBand(FilterType.Peak, 1000, 3), Options);
        var stored = JsonNode.Parse($$"""[{{band}}, {"type": "allPass"}, {{band}}]""");
        var store = new TolerantJson();
        var bands = store.DecodeArray<EqBand>(stored, Options);
        Assert.NotNull(bands);
        Assert.Equal(2, bands.Count);
        Assert.Equal(1, store.UnreadableCount);
        Assert.Equal(3, store.EncodeArray(bands, Options).Count);

        store.Forget();
        Assert.Equal(2, store.EncodeArray(bands, Options).Count);
    }
}
