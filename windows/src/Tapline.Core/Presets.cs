using System.Text.Json.Serialization;

namespace Tapline.Core;

/// <summary>Per-app mixer settings, keyed by app id.</summary>
public record struct AppAudioSettings
{
    public string Name { get; set; } = "";
    [JsonPropertyName("bundleID")]
    public string? BundleId { get; set; }
    public double Volume { get; set; } = 1;
    public bool Muted { get; set; } = false;
    /// <summary>Play the app directly, without any Tapline processing.</summary>
    public bool Bypass { get; set; } = false;

    [JsonConstructor]
    public AppAudioSettings() { }

    public AppAudioSettings(string name, string? bundleId = null, double volume = 1, bool muted = false, bool bypass = false)
    {
        Name = name;
        BundleId = bundleId;
        Volume = volume;
        Muted = muted;
        Bypass = bypass;
    }

    [JsonIgnore]
    public readonly float Gain => Muted ? 0 : (float)Volume;
}

/// <summary>A saved EQ curve (bands, preamp and tone). Effects stay with the device.</summary>
public sealed class EqPreset : IEquatable<EqPreset>
{
    private List<EqBand> _bands = [];

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<EqBand> Bands { get => _bands; set => _bands = value ?? []; }
    [JsonPropertyName("preampDB")]
    public double PreampDb { get; set; } = 0;
    public bool AutoPreamp { get; set; } = true;
    public ToneSettings Tone { get; set; } = new();
    public bool IsBuiltIn { get; set; } = false;
    public string? Source { get; set; }
    /// <summary>Saved with "Include effects": crossfeed, limiter, balance… switch together with the curve,
    /// e.g. a "Headphones" and a "Speakers" setup for the same interface.</summary>
    public EffectsSettings? Effects { get; set; }

    public EqPreset() { }

    public EqPreset(string name, IEnumerable<EqBand> bands, double preampDb = 0, bool autoPreamp = true, ToneSettings? tone = null,
                    bool isBuiltIn = false, string? source = null, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Name = name;
        Bands = [.. bands];
        PreampDb = preampDb;
        AutoPreamp = autoPreamp;
        Tone = tone ?? new ToneSettings();
        IsBuiltIn = isBuiltIn;
        Source = source;
    }

    /// <summary>A new preset holding the curve of <paramref name="settings"/> (and its effects with <paramref name="includeEffects"/>).</summary>
    public static EqPreset From(string name, DspSettings settings, string? source = null, bool includeEffects = false) =>
        new(name, settings.Bands, settings.PreampDb, settings.AutoPreamp, settings.Tone, source: source)
        {
            Effects = includeEffects ? settings.Effects : null,
        };

    /// <summary>Loads the preset into <paramref name="settings"/> (bands get new ids).</summary>
    public void ApplyTo(DspSettings settings)
    {
        settings.Bands = Bands.Select(b => b with { Id = Guid.NewGuid() }).ToList();
        settings.PreampDb = PreampDb;
        settings.AutoPreamp = AutoPreamp;
        settings.Tone = Tone;
        settings.EqEnabled = true;
        if (Effects is { } effects) settings.Effects = effects;
    }

    /// <summary>Whether <paramref name="settings"/> currently hold this preset's curve (band ids ignored).</summary>
    public bool Matches(DspSettings settings)
    {
        static IEnumerable<EqBand> Strip(IEnumerable<EqBand> bands) => bands.Select(b => b with { Id = Guid.Empty });
        if (Effects is { } effects && effects != settings.Effects) return false;
        return Strip(Bands).SequenceEqual(Strip(settings.Bands)) && Tone == settings.Tone && AutoPreamp == settings.AutoPreamp
               && (AutoPreamp || PreampDb == settings.PreampDb);
    }

    public EqPreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        Bands = [.. Bands],
        PreampDb = PreampDb,
        AutoPreamp = AutoPreamp,
        Tone = Tone,
        IsBuiltIn = IsBuiltIn,
        Source = Source,
        Effects = Effects,
    };

    public bool Equals(EqPreset? other) =>
        other is not null
        && Id == other.Id
        && Name == other.Name
        && PreampDb.Equals(other.PreampDb)
        && AutoPreamp == other.AutoPreamp
        && Tone == other.Tone
        && IsBuiltIn == other.IsBuiltIn
        && Source == other.Source
        && Nullable.Equals(Effects, other.Effects)
        && Bands.SequenceEqual(other.Bands);

    public override bool Equals(object? obj) => obj is EqPreset other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Name);
        hash.Add(PreampDb);
        hash.Add(AutoPreamp);
        hash.Add(Tone);
        hash.Add(IsBuiltIn);
        hash.Add(Source);
        hash.Add(Effects);
        foreach (var band in Bands) hash.Add(band);
        return hash.ToHashCode();
    }
}

public static class BuiltInPresets
{
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    private static EqPreset Make(int n, string name, EqBand[] bands) => new(name, bands, isBuiltIn: true, id: Id(n));

    private static readonly EqPreset[] Presets =
    [
        new("Flat", DspSettings.DefaultBands(), isBuiltIn: true, id: Id(1)),
        Make(2, "Bass Boost",
        [
            new(FilterType.LowShelf, 110, 5, 0.71),
            new(FilterType.Peak, 250, -1, 1.0),
            new(FilterType.Peak, 1000, 0, 1.0),
            new(FilterType.Peak, 4000, 0, 1.0),
            new(FilterType.HighShelf, 10_000, 0, 0.71),
        ]),
        Make(3, "Bass Reducer",
        [
            new(FilterType.LowShelf, 120, -5, 0.71),
            new(FilterType.Peak, 250, -1, 1.0),
        ]),
        Make(4, "Treble Boost",
        [
            new(FilterType.HighShelf, 6000, 4, 0.71),
        ]),
        Make(5, "Vocal Clarity",
        [
            new(FilterType.HighPass, 80, q: 0.71),
            new(FilterType.Peak, 250, -2, 1.0),
            new(FilterType.Peak, 2500, 3, 1.0),
            new(FilterType.Peak, 5000, 1.5, 1.2),
        ]),
        Make(6, "Warm",
        [
            new(FilterType.LowShelf, 150, 2.5, 0.71),
            new(FilterType.Peak, 3500, -1.5, 1.0),
            new(FilterType.HighShelf, 9000, -2, 0.71),
        ]),
        Make(7, "Bright",
        [
            new(FilterType.Peak, 250, -1.5, 1.0),
            new(FilterType.Peak, 4000, 2, 0.9),
            new(FilterType.HighShelf, 10_000, 3, 0.71),
        ]),
        Make(8, "V-Shape",
        [
            new(FilterType.LowShelf, 100, 4, 0.71),
            new(FilterType.Peak, 1000, -3, 0.7),
            new(FilterType.HighShelf, 8000, 4, 0.71),
        ]),
        Make(9, "Loudness (low volume)",
        [
            new(FilterType.LowShelf, 90, 6, 0.71),
            new(FilterType.Peak, 3000, -1, 1.0),
            new(FilterType.HighShelf, 12_000, 3, 0.71),
        ]),
        Make(10, "Podcast / Speech",
        [
            new(FilterType.HighPass, 100, q: 0.71),
            new(FilterType.Peak, 200, -2, 0.9),
            new(FilterType.Peak, 3000, 3, 0.8),
            new(FilterType.LowPass, 14_000, q: 0.71),
        ]),
    ];

    /// <summary>The built-in presets (fresh copies; ids, band ids included, are stable).
    /// Names are English localization keys.</summary>
    public static IReadOnlyList<EqPreset> All => Presets.Select(p => p.Clone()).ToList();
}
