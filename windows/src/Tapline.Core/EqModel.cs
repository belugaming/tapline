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

    /// <summary>The band with its gain scaled by <paramref name="strength"/> (0...1), to apply part of a
    /// ready-made correction. Filters without a gain (high-pass, notch…) stay as they are.</summary>
    public readonly EqBand Scaled(double strength) =>
        Type.UsesGain() ? this with { Gain = Gain * (double.IsNaN(strength) ? 1 : Math.Clamp(strength, 0, 1)) } : this;
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
    public const string DefaultVirtualSpeakers = "sadie-ku100-room";

    public double Balance { get; set; } = 0;          // -1 ... 1
    public double Width { get; set; } = 1;            // 0 ... 2
    public bool Mono { get; set; } = false;
    public bool SwapChannels { get; set; } = false;
    public bool CrossfeedEnabled { get; set; } = false;
    public double Crossfeed { get; set; } = 0.5;      // 0 ... 1
    /// <summary>Headphones: speakers at ±30° through binaural impulse responses. Excludes crossfeed.</summary>
    public bool VirtualSpeakersEnabled { get; set; } = false;
    /// <summary>The impulse responses: a built-in set's id or an imported set's.</summary>
    public string VirtualSpeakers { get; set; } = DefaultVirtualSpeakers;
    public bool LimiterEnabled { get; set; } = true;
    [JsonPropertyName("limiterCeilingDB")]
    public double LimiterCeilingDb { get; set; } = -0.3;
    [JsonPropertyName("outputGainDB")]
    public double OutputGainDb { get; set; } = 0;

    [JsonConstructor]
    public EffectsSettings() { }
}

/// <summary>One speaker's room correction on a multichannel output, from the Room page.</summary>
public sealed class SpeakerCorrection : IEquatable<SpeakerCorrection>
{
    public const int MaxBands = 16;
    public static readonly (double Min, double Max) TrimRange = (-24, 0);
    public static readonly (double Min, double Max) DelayRange = (0, SpeakerAlignment.MaxDelayMs);

    private List<EqBand> _bands = [];

    /// <summary>The output channel (0-based, counted over the whole device).</summary>
    public int Channel { get; set; }
    public SpeakerPosition Position { get; set; } = SpeakerPosition.Unknown;
    /// <summary>Its room correction. The <c>Channels</c> of each band is ignored.</summary>
    public List<EqBand> Bands { get => _bands; set => _bands = value ?? []; }
    /// <summary>Level trim, never a boost.</summary>
    [JsonPropertyName("trimDB")]
    public double TrimDb { get; set; }
    /// <summary>Holds the speaker back until the farthest one arrives.</summary>
    [JsonPropertyName("delayMS")]
    public double DelayMs { get; set; }

    public SpeakerCorrection() { }

    public SpeakerCorrection(int channel, SpeakerPosition position, IEnumerable<EqBand>? bands = null, double trimDb = 0, double delayMs = 0)
    {
        Channel = channel;
        Position = position;
        Bands = bands is null ? [] : [.. bands];
        TrimDb = trimDb;
        DelayMs = delayMs;
    }

    public SpeakerCorrection Clone() => new(Channel, Position, Bands, TrimDb, DelayMs);

    public bool Equals(SpeakerCorrection? other) =>
        other is not null && Channel == other.Channel && Position == other.Position && TrimDb.Equals(other.TrimDb)
        && DelayMs.Equals(other.DelayMs) && Bands.SequenceEqual(other.Bands);

    public override bool Equals(object? obj) => obj is SpeakerCorrection other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Channel);
        hash.Add(Position);
        hash.Add(TrimDb);
        hash.Add(DelayMs);
        foreach (var band in Bands) hash.Add(band);
        return hash.ToHashCode();
    }
}

/// <summary>How a multichannel output (5.1, 7.1.4…) is processed. The EQ, tone controls and output
/// gain play on every speaker (or on the front pair only); each speaker then gets its own
/// room correction, trim and delay. A stereo output ignores all of this.</summary>
public sealed class SpeakerSetup : IEquatable<SpeakerSetup>
{
    /// <summary>LFE channels carry nothing above about 120 Hz; the EQ's boosts there would only lift noise.</summary>
    public const double LfeBoostCeiling = 120;

    private List<SpeakerCorrection> _corrections = [];

    /// <summary>The EQ plays on every speaker; off: on the front left and right only. Never boosts
    /// above 120 Hz on LFE channels.</summary>
    public bool EqOnAllSpeakers { get; set; } = true;
    /// <summary>Whether the speaker corrections play. Switching off keeps them.</summary>
    public bool CorrectionEnabled { get; set; } = true;
    public List<SpeakerCorrection> Corrections { get => _corrections; set => _corrections = value ?? []; }
    /// <summary>The layout the corrections were measured on: they only play while the output still has it.</summary>
    public SpeakerLayout? Layout { get; set; }

    public SpeakerSetup() { }

    public SpeakerSetup(bool eqOnAllSpeakers = true, bool correctionEnabled = true, IEnumerable<SpeakerCorrection>? corrections = null,
                        SpeakerLayout? layout = null)
    {
        EqOnAllSpeakers = eqOnAllSpeakers;
        CorrectionEnabled = correctionEnabled;
        Corrections = corrections is null ? [] : [.. corrections];
        Layout = layout;
    }

    /// <summary>The corrections that play on an output with <paramref name="layout"/>: none when switched off
    /// or measured on other speakers.</summary>
    public IReadOnlyList<SpeakerCorrection> ActiveCorrections(SpeakerLayout? layout)
    {
        if (!CorrectionEnabled || layout is null || !layout.Equals(Layout)) return [];
        return Corrections;
    }

    /// <summary>Headroom for the corrections' boosts, dB (zero or negative). The same for every speaker,
    /// so the trims keep the speakers level-matched.</summary>
    public static double HeadroomDb(IEnumerable<SpeakerCorrection> corrections, double sampleRate = 48_000)
    {
        double boost = 0;
        foreach (var c in corrections) boost = Math.Max(boost, ResponseCurve.MaxBoostDb([.. c.Bands.Select(Mono)], sampleRate));
        return -Math.Max(0, boost);
    }

    /// <summary>A band as a speaker plays it: on its one channel.</summary>
    public static EqBand Mono(EqBand band) => band.Clamped() with { Channels = EqChannels.Both };

    /// <summary>Whether the EQ leaves <paramref name="band"/> out on LFE channels: bells and high shelves that boost above 120 Hz.</summary>
    public static bool SkipsOnLfe(EqBand band) =>
        band.Gain > 0 && band.Frequency > LfeBoostCeiling && band.Type is FilterType.Peak or FilterType.HighShelf;

    public SpeakerSetup Clone() => new(EqOnAllSpeakers, CorrectionEnabled, Corrections.Select(c => c.Clone()), Layout);

    public bool Equals(SpeakerSetup? other) =>
        other is not null && EqOnAllSpeakers == other.EqOnAllSpeakers && CorrectionEnabled == other.CorrectionEnabled
        && Equals(Layout, other.Layout) && Corrections.SequenceEqual(other.Corrections);

    public override bool Equals(object? obj) => obj is SpeakerSetup other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(EqOnAllSpeakers);
        hash.Add(CorrectionEnabled);
        hash.Add(Layout);
        foreach (var c in Corrections) hash.Add(c);
        return hash.ToHashCode();
    }
}

/// <summary>The full processing state for one output device.</summary>
public sealed class DspSettings : IEquatable<DspSettings>
{
    public const int MaxBands = 24;
    public const double PreampMin = -24, PreampMax = 12;

    private List<EqBand> _bands = [];
    private SpeakerSetup _speakers = new();

    public bool EqEnabled { get; set; } = true;
    [JsonPropertyName("preampDB")]
    public double PreampDb { get; set; } = 0;
    public bool AutoPreamp { get; set; } = true;
    public List<EqBand> Bands { get => _bands; set => _bands = value ?? []; }
    public ToneSettings Tone { get; set; } = new();
    public EffectsSettings Effects { get; set; } = new();
    /// <summary>Multichannel outputs only.</summary>
    public SpeakerSetup Speakers { get => _speakers; set => _speakers = value ?? new(); }

    public DspSettings() { }

    public DspSettings(bool eqEnabled = true, double preampDb = 0, bool autoPreamp = true, IEnumerable<EqBand>? bands = null,
                       ToneSettings? tone = null, EffectsSettings? effects = null, SpeakerSetup? speakers = null)
    {
        EqEnabled = eqEnabled;
        PreampDb = preampDb;
        AutoPreamp = autoPreamp;
        Bands = bands is null ? [] : [.. bands];
        Tone = tone ?? new ToneSettings();
        Effects = effects ?? new EffectsSettings();
        Speakers = speakers?.Clone() ?? new SpeakerSetup();
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

    /// <summary>How much the whole EQ (bands, tone controls and preamp) changes the loudness of
    /// pink noise, in dB (LU, K-weighted as in ITU-R BS.1770). A correction with a -6 dB
    /// preamp typically comes out a few dB quieter than the original.</summary>
    public double EqLoudnessDb(double sampleRate = 48_000) => EffectivePreampDb(sampleRate) + CurveLoudnessDb(sampleRate);

    /// <summary>Gain that takes the place of the EQ while it is bypassed, so that switching it off
    /// changes the sound but not the loudness: the EQ's loudness change, but never a boost.</summary>
    public double BypassGainDb(double sampleRate = 48_000) => BypassGainFromLoudness(EqLoudnessDb(sampleRate));

    internal static double BypassGainFromLoudness(double loudnessDb) =>
        double.IsFinite(loudnessDb) ? Math.Clamp(loudnessDb, PreampMin, 0) : 0;

    /// <summary>Loudness change of the bands and tone controls as they are played. Left and
    /// right count equally when they get different curves.</summary>
    internal double CurveLoudnessDb(double sampleRate)
    {
        var bands = ActiveBands.Select(b => b.Clamped()).ToList();
        if (bands.Count == 0) return 0;
        if (!HasChannelSpecificBands) return ResponseCurve.LoudnessChangeDb(bands, sampleRate);
        double power = Math.Pow(10, ResponseCurve.LoudnessChangeDb(bands, sampleRate, EqChannels.Left) / 10)
                       + Math.Pow(10, ResponseCurve.LoudnessChangeDb(bands, sampleRate, EqChannels.Right) / 10);
        return 10 * Math.Log10(power / 2);
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
        Speakers = Speakers.Clone(),
    };

    public bool Equals(DspSettings? other) =>
        other is not null
        && EqEnabled == other.EqEnabled
        && PreampDb.Equals(other.PreampDb)
        && AutoPreamp == other.AutoPreamp
        && Tone == other.Tone
        && Effects == other.Effects
        && Speakers.Equals(other.Speakers)
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
        hash.Add(Speakers);
        foreach (var band in Bands) hash.Add(band);
        return hash.ToHashCode();
    }
}
