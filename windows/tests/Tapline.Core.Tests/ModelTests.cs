using System.Text.Json;
using System.Text.Json.Nodes;
using Tapline.Core;

namespace Tapline.Core.Tests;

public class ModelTests
{
    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, TaplineJson.Options), TaplineJson.Options)!;

    private static DspSettings Sample() => new(
        preampDb: -3.5,
        autoPreamp: false,
        bands: [new EqBand(FilterType.LowShelf, 105, 4, 0.7), new EqBand(FilterType.Notch, 3000, q: 8, isEnabled: false, channels: EqChannels.Left)],
        tone: new ToneSettings(bass: 2, air: -1),
        effects: new EffectsSettings { Crossfeed = 0.3, CrossfeedEnabled = true, LimiterCeilingDb = -1, OutputGainDb = 2 });

    [Fact]
    public void SettingsRoundTripThroughJson()
    {
        var settings = Sample();
        var again = RoundTrip(settings);
        Assert.Equal(settings, again);
        Assert.NotSame(settings, again);
        Assert.Equal(settings.GetHashCode(), again.GetHashCode());
    }

    [Fact]
    public void JsonUsesTheSwiftKeysAndRawValues()
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(Sample(), TaplineJson.Options))!.AsObject();
        Assert.Equal(["eqEnabled", "preampDB", "autoPreamp", "bands", "tone", "effects", "filterPhase", "speakers"], node.Select(p => p.Key));
        Assert.Equal("minimum", (string?)node["filterPhase"]);
        var band = node["bands"]![1]!.AsObject();
        Assert.Equal(["id", "type", "frequency", "gain", "q", "isEnabled", "channels"], band.Select(p => p.Key));
        Assert.Equal("notch", (string?)band["type"]);
        Assert.Equal("left", (string?)band["channels"]);
        Assert.Equal("lowShelf", (string?)node["bands"]![0]!["type"]);
        Assert.Equal(["bass", "warmth", "clarity", "air"], node["tone"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["balance", "width", "mono", "swapChannels", "crossfeedEnabled", "crossfeed", "virtualSpeakersEnabled", "virtualSpeakers",
                      "limiterEnabled", "limiterCeilingDB", "outputGainDB"],
                     node["effects"]!.AsObject().Select(p => p.Key));
        // Enums are strings even without TaplineJson.Options.
        Assert.Equal("\"highShelf\"", JsonSerializer.Serialize(FilterType.HighShelf));
        Assert.Equal(EqChannels.Right, JsonSerializer.Deserialize<EqChannels>("\"right\""));
    }

    [Fact]
    public void MissingValuesFallBackToTheDefaults()
    {
        var settings = JsonSerializer.Deserialize<DspSettings>("{}", TaplineJson.Options)!;
        Assert.Equal(new DspSettings(), settings);
        Assert.True(settings.EqEnabled && settings.AutoPreamp && settings.Bands.Count == 0);

        var effects = JsonSerializer.Deserialize<EffectsSettings>("""{"mono": true}""", TaplineJson.Options);
        Assert.Equal(new EffectsSettings { Mono = true }, effects);
        Assert.True(effects.Width == 1 && effects.LimiterEnabled && effects.LimiterCeilingDb == -0.3 && effects.Crossfeed == 0.5);

        var partial = JsonSerializer.Deserialize<DspSettings>("""{"preampDB": -2, "effects": {"width": 1.5}, "bands": null}""", TaplineJson.Options)!;
        Assert.Equal(-2, partial.PreampDb);
        Assert.Equal(1.5, partial.Effects.Width);
        Assert.True(partial.Effects.LimiterEnabled);
        Assert.Empty(partial.Bands);

        var preset = JsonSerializer.Deserialize<EqPreset>("""{"name": "Old", "bands": [], "preampDB": 0, "autoPreamp": true, "tone": {}}""", TaplineJson.Options)!;
        Assert.False(preset.IsBuiltIn);
        Assert.Null(preset.Effects);
        Assert.Null(preset.Source);

        var band = JsonSerializer.Deserialize<EqBand>("""{"frequency": 50}""", TaplineJson.Options);
        Assert.True(band.Frequency == 50 && band.Q == 1 && band.Gain == 0 && band.IsEnabled && band.Type == FilterType.Peak
                    && band.Channels == EqChannels.Both && band.Id != Guid.Empty);
        Assert.DoesNotContain("source", JsonSerializer.Serialize(preset, TaplineJson.Options));

        var app = JsonSerializer.Deserialize<AppAudioSettings>("""{"name": "Safari", "bundleID": "com.apple.Safari"}""", TaplineJson.Options);
        Assert.Equal(new AppAudioSettings("Safari", "com.apple.Safari"), app);
        Assert.Equal(1f, app.Gain);
        Assert.Equal(0f, (app with { Muted = true }).Gain);
    }

    [Fact]
    public void PresetsRoundTripThroughJson()
    {
        var preset = EqPreset.From("Speakers", Sample(), source: "Room", includeEffects: true);
        var again = RoundTrip(preset);
        Assert.Equal(preset, again);
        Assert.Equal(preset.Effects, again.Effects);
        Assert.Equal(RoundTrip(BuiltInPresets.All.ToList()), BuiltInPresets.All);
    }

    [Fact]
    public void CloneIsDeep()
    {
        var settings = Sample();
        var copy = settings.Clone();
        Assert.Equal(settings, copy);
        copy.Bands[0] = copy.Bands[0] with { Gain = 9 };
        copy.Bands.Add(new EqBand(FilterType.Peak, 500));
        Assert.Equal(4, settings.Bands[0].Gain);
        Assert.Equal(2, settings.Bands.Count);
        Assert.NotEqual(settings, copy);

        var preset = EqPreset.From("x", settings);
        var presetCopy = preset.Clone();
        presetCopy.Bands.Clear();
        Assert.Equal(2, preset.Bands.Count);
        Assert.NotEqual(preset, presetCopy);
    }

    [Fact]
    public void FlatIsANewInstanceWithStableBands()
    {
        var a = DspSettings.Flat;
        var b = DspSettings.Flat;
        Assert.NotSame(a, b);
        Assert.Equal(a, b);
        a.Bands.Clear();
        Assert.Equal(5, DspSettings.Flat.Bands.Count);
    }

    [Fact]
    public void PresetsApplyAndMatch()
    {
        var settings = new DspSettings(eqEnabled: false, effects: new EffectsSettings { Mono = true });
        var bassBoost = BuiltInPresets.All.Single(p => p.Name == "Bass Boost");
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000002"), bassBoost.Id);
        Assert.False(bassBoost.Matches(settings));

        bassBoost.ApplyTo(settings);
        Assert.True(settings.EqEnabled);
        Assert.True(settings.Effects.Mono);
        Assert.Equal(bassBoost.Bands.Select(b => b.Frequency), settings.Bands.Select(b => b.Frequency));
        Assert.DoesNotContain(settings.Bands, b => bassBoost.Bands.Any(p => p.Id == b.Id));
        Assert.True(bassBoost.Matches(settings));

        // The manual preamp only counts without auto preamp.
        settings.PreampDb = -4;
        Assert.True(bassBoost.Matches(settings));
        settings.Bands[0] = settings.Bands[0] with { Gain = 4 };
        Assert.False(bassBoost.Matches(settings));

        var withEffects = EqPreset.From("Speakers", settings, includeEffects: true);
        Assert.True(withEffects.Matches(settings));
        settings.Effects = settings.Effects with { Width = 1.2 };
        Assert.False(withEffects.Matches(settings));
        withEffects.ApplyTo(settings);
        Assert.Equal(1, settings.Effects.Width);
    }

    [Fact]
    public void BandsClampAndToneMacros()
    {
        var band = new EqBand(FilterType.Peak, 5, 40, 0.01).Clamped();
        Assert.Equal((EqBand.FrequencyMin, EqBand.GainMax, EqBand.QMin), (band.Frequency, band.Gain, band.Q));
        Assert.Equal(new EqBand(FilterType.Peak, 1000).Q, new EqBand().Q);

        var tone = new ToneSettings(bass: 3);
        Assert.False(tone.IsNeutral);
        Assert.True(new ToneSettings().IsNeutral);
        Assert.Equal([true, false, false, false], tone.Bands.Select(b => b.IsEnabled));
        Assert.Equal(tone.Bands.Select(b => b.Id), new ToneSettings(air: 1).Bands.Select(b => b.Id));

        var settings = new DspSettings(bands: [new EqBand(FilterType.Peak, 100, isEnabled: false), new EqBand(FilterType.Peak, 200)], tone: tone);
        Assert.Equal([200.0, 105], settings.ActiveBands.Select(b => b.Frequency));
        Assert.False(settings.HasChannelSpecificBands);
    }

    [Fact]
    public void FitterOptionsKeepTheirDefaults()
    {
        var o = new EqFitter.Options(keepBassBelow: 200);
        Assert.True(o.MaxFilters == 10 && o.MaxBoostDb == 6 && o.MaxCutDb == 12 && o.MaxQ == 6 && o.LowSmoothingOctaves == 1.0 / 6);
        Assert.Equal(new EqFitter.Options { KeepBassBelow = 200 }, o);
        var adi = Adi2Preset.FitOptions(o);
        Assert.True(adi.Slots!.Count == 5 && adi.GainStepDb == 0.5 && adi.QStep == 0.1 && adi.MaxQ == 6 && adi.MaxCutDb == 12);
        Assert.Null(o.Slots);
        Assert.NotEqual(o, adi);
    }

    [Fact]
    public void DecibelConversions()
    {
        Assert.Equal(2, Db.ToLinear(6.0206), 4);
        Assert.Equal(-6.0206, Db.FromLinear(0.5), 4);
        Assert.Equal(-240, Db.FromLinear(0));
        Assert.Equal(5, 7.0.Clamp((0, 5)));
        Assert.Equal(-1, (-3.0).Clamp(-1, 1));
    }
}
