using System.Text.Json.Serialization;

namespace Tapline.Core;

[JsonConverter(typeof(CamelCaseEnumConverter<FilterType>))]
public enum FilterType
{
    Peak,
    LowShelf,
    HighShelf,
    LowPass,
    HighPass,
    Notch,
    BandPass,
}

public static class FilterTypeExtensions
{
    /// <summary>Whether the gain parameter has any effect for this filter type.</summary>
    public static bool UsesGain(this FilterType type) =>
        type is FilterType.Peak or FilterType.LowShelf or FilterType.HighShelf;
}

/// <summary>Which output channels a band processes.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<EqChannels>))]
public enum EqChannels
{
    Both,
    Left,
    Right,
}

public static class EqChannelsExtensions
{
    /// <summary>Whether a band set to <paramref name="self"/> processes <paramref name="channel"/> (Left or Right).</summary>
    public static bool Includes(this EqChannels self, EqChannels channel) => self == EqChannels.Both || self == channel;
}

public record struct EqBand
{
    public const double FrequencyMin = 20, FrequencyMax = 20_000;
    public const double GainMin = -24, GainMax = 24;
    public const double QMin = 0.1, QMax = 18;

    public Guid Id { get; set; }
    public FilterType Type { get; set; }
    public double Frequency { get; set; }
    public double Gain { get; set; }
    public double Q { get; set; }
    public bool IsEnabled { get; set; }
    public EqChannels Channels { get; set; }

    /// <summary>A 1 kHz peak at 0 dB with a new id (also what JSON fills in for missing values).</summary>
    [JsonConstructor]
    public EqBand() : this(FilterType.Peak, 1000) { }

    public EqBand(FilterType type, double frequency, double gain = 0, double q = 1, bool isEnabled = true,
                  EqChannels channels = EqChannels.Both, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Type = type;
        Frequency = frequency;
        Gain = gain;
        Q = q;
        IsEnabled = isEnabled;
        Channels = channels;
    }

    /// <summary>Keeps every parameter in range. NaN becomes a neutral value (1 kHz, 0 dB, Q 1).</summary>
    public readonly EqBand Clamped() => this with
    {
        Frequency = double.IsNaN(Frequency) ? 1000 : Frequency.Clamp((FrequencyMin, FrequencyMax)),
        Gain = double.IsNaN(Gain) ? 0 : Gain.Clamp((GainMin, GainMax)),
        Q = double.IsNaN(Q) ? 1 : Q.Clamp((QMin, QMax)),
    };
}

/// <summary>Simple "macro" tone controls layered on top of the parametric EQ.</summary>
public record struct ToneSettings
{
    public const double Min = -12, Max = 12;

    public double Bass { get; set; }     // low shelf 105 Hz
    public double Warmth { get; set; }   // peak 250 Hz
    public double Clarity { get; set; }  // peak 3 kHz
    public double Air { get; set; }      // high shelf 10 kHz

    [JsonConstructor]
    public ToneSettings() { }

    public ToneSettings(double bass = 0, double warmth = 0, double clarity = 0, double air = 0)
    {
        Bass = bass;
        Warmth = warmth;
        Clarity = clarity;
        Air = air;
    }

    [JsonIgnore]
    public readonly bool IsNeutral => Bass == 0 && Warmth == 0 && Clarity == 0 && Air == 0;

    [JsonIgnore]
    public readonly IReadOnlyList<EqBand> Bands =>
    [
        new(FilterType.LowShelf, 105, Bass, 0.71, Bass != 0, id: Ids[0]),
        new(FilterType.Peak, 250, Warmth, 0.8, Warmth != 0, id: Ids[1]),
        new(FilterType.Peak, 3000, Clarity, 0.9, Clarity != 0, id: Ids[2]),
        new(FilterType.HighShelf, 10_000, Air, 0.71, Air != 0, id: Ids[3]),
    ];

    private static readonly Guid[] Ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
}

public record struct EffectsSettings
{
    /// <summary>Limits the realtime kernel clamps to.</summary>
    public const double BalanceMin = -1, BalanceMax = 1;
    public const double WidthMin = 0, WidthMax = 2;
    public const double CrossfeedMin = 0, CrossfeedMax = 1;
    public const double LimiterCeilingMin = -12, LimiterCeilingMax = 0;
    public const double OutputGainMin = -24, OutputGainMax = 12;

    public double Balance { get; set; } = 0;          // -1 ... 1
    public double Width { get; set; } = 1;            // 0 ... 2
    public bool Mono { get; set; } = false;
    public bool SwapChannels { get; set; } = false;
    public bool CrossfeedEnabled { get; set; } = false;
    public double Crossfeed { get; set; } = 0.5;      // 0 ... 1
    public bool LimiterEnabled { get; set; } = true;
    [JsonPropertyName("limiterCeilingDB")]
    public double LimiterCeilingDb { get; set; } = -0.3;
    [JsonPropertyName("outputGainDB")]
    public double OutputGainDb { get; set; } = 0;

    [JsonConstructor]
    public EffectsSettings() { }
}

/// <summary>The full processing state for one output device.</summary>
public sealed class DspSettings : IEquatable<DspSettings>
{
    public const int MaxBands = 24;
    public const double PreampMin = -24, PreampMax = 12;

    private List<EqBand> _bands = [];

    public bool EqEnabled { get; set; } = true;
    [JsonPropertyName("preampDB")]
    public double PreampDb { get; set; } = 0;
    public bool AutoPreamp { get; set; } = true;
    public List<EqBand> Bands { get => _bands; set => _bands = value ?? []; }
    public ToneSettings Tone { get; set; } = new();
    public EffectsSettings Effects { get; set; } = new();

    public DspSettings() { }

    public DspSettings(bool eqEnabled = true, double preampDb = 0, bool autoPreamp = true, IEnumerable<EqBand>? bands = null,
                       ToneSettings? tone = null, EffectsSettings? effects = null)
    {
        EqEnabled = eqEnabled;
        PreampDb = preampDb;
        AutoPreamp = autoPreamp;
        Bands = bands is null ? [] : [.. bands];
        Tone = tone ?? new ToneSettings();
        Effects = effects ?? new EffectsSettings();
    }

    /// <summary>Bands that actually shape the signal: parametric bands followed by tone macros.</summary>
    [JsonIgnore]
    public IReadOnlyList<EqBand> ActiveBands => [.. Bands.Where(b => b.IsEnabled), .. Tone.Bands.Where(b => b.IsEnabled)];

    /// <summary>Whether left and right get different curves.</summary>
    [JsonIgnore]
    public bool HasChannelSpecificBands => Bands.Any(b => b.IsEnabled && b.Channels != EqChannels.Both);

    /// <summary>Preamp that is actually applied: either the manual value, or enough
    /// headroom to cancel the largest boost of the curve.</summary>
    public double EffectivePreampDb(double sampleRate = 48_000)
    {
        if (!AutoPreamp) return PreampDb.Clamp(PreampMin, PreampMax);
        // Clamped like the bands Equalizer APO gets, so a NaN band cannot make the preamp NaN.
        double boost = ResponseCurve.MaxBoostDb([.. ActiveBands.Select(b => b.Clamped())], sampleRate);
        return -Math.Max(0, boost);
    }

    /// <summary>The default five-band layout used for new profiles.</summary>
    public static List<EqBand> DefaultBands() =>
    [
        new(FilterType.LowShelf, 80, 0, 0.71),
        new(FilterType.Peak, 250, 0, 1.0),
        new(FilterType.Peak, 1000, 0, 1.0),
        new(FilterType.Peak, 4000, 0, 1.0),
        new(FilterType.HighShelf, 10_000, 0, 0.71),
    ];

    private static readonly DspSettings FlatValue = new(bands: DefaultBands());

    /// <summary>Default settings with the default bands: a new instance on every get (band ids stay the same).</summary>
    public static DspSettings Flat => FlatValue.Clone();

    public DspSettings Clone() => new()
    {
        EqEnabled = EqEnabled,
        PreampDb = PreampDb,
        AutoPreamp = AutoPreamp,
        Bands = [.. Bands],
        Tone = Tone,
        Effects = Effects,
    };

    public bool Equals(DspSettings? other) =>
        other is not null
        && EqEnabled == other.EqEnabled
        && PreampDb.Equals(other.PreampDb)
        && AutoPreamp == other.AutoPreamp
        && Tone == other.Tone
        && Effects == other.Effects
        && Bands.SequenceEqual(other.Bands);

    public override bool Equals(object? obj) => obj is DspSettings other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(EqEnabled);
        hash.Add(PreampDb);
        hash.Add(AutoPreamp);
        hash.Add(Tone);
        hash.Add(Effects);
        foreach (var band in Bands) hash.Add(band);
        return hash.ToHashCode();
    }
}
