using System.Text.Json.Serialization;

namespace Tapline.Core;

/// <summary>Where the speaker of one output channel stands, named as in Dolby's speaker layouts.
/// Read from the speaker mask on Windows (from Core Audio's channel labels on macOS).</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<SpeakerPosition>))]
public enum SpeakerPosition
{
    FrontLeft,
    FrontRight,
    Center,
    Lfe,
    FrontLeftCenter,
    FrontRightCenter,
    WideLeft,
    WideRight,
    /// <summary>The surrounds beside the listener (5.1's surrounds, 7.1's side surrounds).</summary>
    SurroundLeft,
    SurroundRight,
    /// <summary>7.1's rear surrounds, behind the listener.</summary>
    RearLeft,
    RearRight,
    RearCenter,
    TopFrontLeft,
    TopFrontCenter,
    TopFrontRight,
    TopMiddleLeft,
    TopMiddleCenter,
    TopMiddleRight,
    TopRearLeft,
    TopRearCenter,
    TopRearRight,
    Lfe2,
    /// <summary>A channel without a known place, such as an unlabelled channel of an interface.</summary>
    Unknown,
}

public enum SpeakerGroup
{
    Front,
    Surround,
    Height,
    Lfe,
    Unknown,
}

public static class SpeakerPositionExtensions
{
    public static SpeakerGroup Group(this SpeakerPosition p) => p switch
    {
        SpeakerPosition.FrontLeft or SpeakerPosition.FrontRight or SpeakerPosition.Center or SpeakerPosition.FrontLeftCenter
            or SpeakerPosition.FrontRightCenter or SpeakerPosition.WideLeft or SpeakerPosition.WideRight => SpeakerGroup.Front,
        SpeakerPosition.SurroundLeft or SpeakerPosition.SurroundRight or SpeakerPosition.RearLeft or SpeakerPosition.RearRight
            or SpeakerPosition.RearCenter => SpeakerGroup.Surround,
        SpeakerPosition.TopFrontLeft or SpeakerPosition.TopFrontCenter or SpeakerPosition.TopFrontRight or SpeakerPosition.TopMiddleLeft
            or SpeakerPosition.TopMiddleCenter or SpeakerPosition.TopMiddleRight or SpeakerPosition.TopRearLeft or SpeakerPosition.TopRearCenter
            or SpeakerPosition.TopRearRight => SpeakerGroup.Height,
        SpeakerPosition.Lfe or SpeakerPosition.Lfe2 => SpeakerGroup.Lfe,
        _ => SpeakerGroup.Unknown,
    };

    public static bool IsLfe(this SpeakerPosition p) => p.Group() == SpeakerGroup.Lfe;

    public static bool IsHeight(this SpeakerPosition p) => p.Group() == SpeakerGroup.Height;

    /// <summary>The front left and right speakers, which the stereo effects act on.</summary>
    public static bool IsFrontPair(this SpeakerPosition p) => p is SpeakerPosition.FrontLeft or SpeakerPosition.FrontRight;

    /// <summary>Dolby's short name: L, R, C, LFE, Ls, Rrs, Ltf…</summary>
    public static string Abbreviation(this SpeakerPosition p) => p switch
    {
        SpeakerPosition.FrontLeft => "L",
        SpeakerPosition.FrontRight => "R",
        SpeakerPosition.Center => "C",
        SpeakerPosition.Lfe => "LFE",
        SpeakerPosition.FrontLeftCenter => "Lc",
        SpeakerPosition.FrontRightCenter => "Rc",
        SpeakerPosition.WideLeft => "Lw",
        SpeakerPosition.WideRight => "Rw",
        SpeakerPosition.SurroundLeft => "Ls",
        SpeakerPosition.SurroundRight => "Rs",
        SpeakerPosition.RearLeft => "Lrs",
        SpeakerPosition.RearRight => "Rrs",
        SpeakerPosition.RearCenter => "Cs",
        SpeakerPosition.TopFrontLeft => "Ltf",
        SpeakerPosition.TopFrontCenter => "Ctf",
        SpeakerPosition.TopFrontRight => "Rtf",
        SpeakerPosition.TopMiddleLeft => "Ltm",
        SpeakerPosition.TopMiddleCenter => "Ctm",
        SpeakerPosition.TopMiddleRight => "Rtm",
        SpeakerPosition.TopRearLeft => "Ltr",
        SpeakerPosition.TopRearCenter => "Ctr",
        SpeakerPosition.TopRearRight => "Rtr",
        SpeakerPosition.Lfe2 => "LFE2",
        _ => "?",
    };

    /// <summary>Direction seen from the listening position in degrees, 0 straight ahead and positive
    /// to the right (Dolby's recommended angles), for drawing the layout.</summary>
    public static double Azimuth(this SpeakerPosition p) => p switch
    {
        SpeakerPosition.FrontLeft => -30,
        SpeakerPosition.FrontRight => 30,
        SpeakerPosition.Center or SpeakerPosition.TopFrontCenter or SpeakerPosition.TopMiddleCenter => 0,
        SpeakerPosition.Lfe => -15,
        SpeakerPosition.Lfe2 => 15,
        SpeakerPosition.FrontLeftCenter => -15,
        SpeakerPosition.FrontRightCenter => 15,
        SpeakerPosition.WideLeft => -60,
        SpeakerPosition.WideRight => 60,
        SpeakerPosition.SurroundLeft => -100,
        SpeakerPosition.SurroundRight => 100,
        SpeakerPosition.RearLeft => -145,
        SpeakerPosition.RearRight => 145,
        SpeakerPosition.RearCenter or SpeakerPosition.TopRearCenter => 180,
        SpeakerPosition.TopFrontLeft => -45,
        SpeakerPosition.TopFrontRight => 45,
        SpeakerPosition.TopMiddleLeft => -90,
        SpeakerPosition.TopMiddleRight => 90,
        SpeakerPosition.TopRearLeft => -135,
        SpeakerPosition.TopRearRight => 135,
        _ => 0,
    };
}

/// <summary>The speakers of a multichannel output, one position per channel in channel order.
/// Immutable; equal when the positions are.</summary>
public sealed class SpeakerLayout : IEquatable<SpeakerLayout>
{
    public IReadOnlyList<SpeakerPosition> Positions { get; }

    [JsonConstructor]
    public SpeakerLayout(IReadOnlyList<SpeakerPosition>? positions)
    {
        Positions = positions is null ? [] : Array.AsReadOnly(positions.ToArray());
    }

    [JsonIgnore]
    public int ChannelCount => Positions.Count;

    /// <summary>More than a left and right speaker: a centre, an LFE channel, surrounds or heights.
    /// Only such layouts get Tapline's multichannel processing; anything else is a stereo pair
    /// with extra channels that pass through untouched, as on an audio interface.</summary>
    [JsonIgnore]
    public bool IsSurround => Positions.Any(p => !p.IsFrontPair() && p != SpeakerPosition.Unknown);

    /// <summary>"7.1.4": the speakers at ear height, the LFE channels and the height speakers.</summary>
    [JsonIgnore]
    public string Name
    {
        get
        {
            int ear = Positions.Count(p => p.Group() is SpeakerGroup.Front or SpeakerGroup.Surround);
            int lfe = Positions.Count(p => p.IsLfe());
            int top = Positions.Count(p => p.IsHeight());
            return top > 0 ? $"{ear}.{lfe}.{top}" : $"{ear}.{lfe}";
        }
    }

    /// <summary>The channels with a known speaker, in channel order: the ones the Room page measures.</summary>
    [JsonIgnore]
    public IReadOnlyList<int> SpeakerChannels => [.. Enumerable.Range(0, Positions.Count).Where(i => Positions[i] != SpeakerPosition.Unknown)];

    /// <summary>The timing reference of a measurement: the front left speaker, else the first known one.</summary>
    [JsonIgnore]
    public int? ReferenceChannel
    {
        get
        {
            for (int i = 0; i < Positions.Count; i++)
                if (Positions[i] == SpeakerPosition.FrontLeft) return i;
            var speakers = SpeakerChannels;
            foreach (int i in speakers)
                if (!Positions[i].IsLfe()) return i;
            return speakers.Count > 0 ? speakers[0] : null;
        }
    }

    /// <summary>The channel of <paramref name="position"/>, or null when the layout has no such speaker.</summary>
    public int? ChannelOf(SpeakerPosition position)
    {
        for (int i = 0; i < Positions.Count; i++)
            if (Positions[i] == position) return i;
        return null;
    }

    // macOS's channel orders (Core Audio's kAudioChannelLayoutTag_MPEG_5_1_A, _AudioUnit_7_1, _Atmos_*).
    // A Windows output's order comes from its speaker mask instead: see FromChannelMask.
    private const SpeakerPosition L = SpeakerPosition.FrontLeft, R = SpeakerPosition.FrontRight, C = SpeakerPosition.Center, Lfe = SpeakerPosition.Lfe,
        Ls = SpeakerPosition.SurroundLeft, Rs = SpeakerPosition.SurroundRight, Lrs = SpeakerPosition.RearLeft, Rrs = SpeakerPosition.RearRight,
        Lw = SpeakerPosition.WideLeft, Rw = SpeakerPosition.WideRight, Ltf = SpeakerPosition.TopFrontLeft, Rtf = SpeakerPosition.TopFrontRight,
        Ltm = SpeakerPosition.TopMiddleLeft, Rtm = SpeakerPosition.TopMiddleRight, Ltr = SpeakerPosition.TopRearLeft, Rtr = SpeakerPosition.TopRearRight;

    public static SpeakerLayout Surround51 { get; } = new([L, R, C, Lfe, Ls, Rs]);
    public static SpeakerLayout Surround71 { get; } = new([L, R, C, Lfe, Ls, Rs, Lrs, Rrs]);
    public static SpeakerLayout Atmos512 { get; } = new([L, R, C, Lfe, Ls, Rs, Ltm, Rtm]);
    public static SpeakerLayout Atmos514 { get; } = new([L, R, C, Lfe, Ls, Rs, Ltf, Rtf, Ltr, Rtr]);
    public static SpeakerLayout Atmos712 { get; } = new([L, R, C, Lfe, Ls, Rs, Lrs, Rrs, Ltm, Rtm]);
    public static SpeakerLayout Atmos714 { get; } = new([L, R, C, Lfe, Ls, Rs, Lrs, Rrs, Ltf, Rtf, Ltr, Rtr]);
    public static SpeakerLayout Atmos916 { get; } = new([L, R, C, Lfe, Ls, Rs, Lrs, Rrs, Lw, Rw, Ltf, Rtf, Ltm, Rtm, Ltr, Rtr]);

    /// <summary>macOS's usual layout for a channel count, for outputs that do not label their channels:
    /// 5.1 for 6 channels, 7.1 for 8, 5.1.4 for 10, 7.1.4 for 12, 9.1.6 for 16.</summary>
    public static SpeakerLayout? Standard(int channelCount) => channelCount switch
    {
        6 => Surround51,
        8 => Surround71,
        10 => Atmos514,
        12 => Atmos714,
        16 => Atmos916,
        _ => null,
    };

    // MARK: - Windows speaker masks

    /// <summary>The speaker bits of WAVEFORMATEXTENSIBLE.dwChannelMask (SPEAKER_FRONT_LEFT …
    /// SPEAKER_TOP_BACK_RIGHT). A Windows stream holds the channels of the set bits in this order.</summary>
    public const uint SpeakerBits = 0x3FFFF;

    /// <summary>Windows' default masks (ksmedia.h) for a channel count: stereo, 5.1
    /// (KSAUDIO_SPEAKER_5POINT1_SURROUND), 7.1 (KSAUDIO_SPEAKER_7POINT1_SURROUND), 5.1.4 and 7.1.4.</summary>
    public static uint? DefaultChannelMask(int channelCount) => channelCount switch
    {
        2 => 0x3,
        6 => 0x60F,
        8 => 0x63F,
        10 => 0x2D60F,
        12 => 0x2D63F,
        _ => null,
    };

    /// <summary>The layout of a Windows output from its speaker mask (WAVEFORMATEXTENSIBLE.dwChannelMask)
    /// and channel count, as macOS reads Core Audio's channel labels. The positions follow the mask's
    /// bits in order; channels beyond them are unknown. A mask of 0 (KSAUDIO_SPEAKER_DIRECTOUT) names no
    /// speakers: an audio interface's outputs, which stay a stereo pair with channels that pass
    /// through. Only a digital output to an AV receiver or a TV (<paramref name="toReceiver"/>: HDMI,
    /// DisplayPort or S/PDIF) whose mask names nothing beyond the front pair gets Windows' default
    /// layout for its channel count.
    ///
    /// The back pair (SPEAKER_BACK_LEFT / RIGHT) is 7.1's rear surrounds when the side pair is there
    /// too, else 5.1's surrounds, as Windows' "5.1 (back)" configuration uses it.</summary>
    public static SpeakerLayout FromChannelMask(uint mask, int channels, bool toReceiver = false)
    {
        channels = Math.Max(0, channels);
        mask &= SpeakerBits;
        if (toReceiver && (mask & ~0x3u) == 0 && DefaultChannelMask(channels) is { } fallback) mask = fallback;
        bool sides = (mask & 0x600) != 0;
        var positions = new List<SpeakerPosition>(channels);
        for (int bit = 0; bit < 18 && positions.Count < channels; bit++)
        {
            if ((mask & (1u << bit)) == 0) continue;
            positions.Add(bit switch
            {
                0 => SpeakerPosition.FrontLeft,
                1 => SpeakerPosition.FrontRight,
                2 => SpeakerPosition.Center,
                3 => SpeakerPosition.Lfe,
                4 => sides ? SpeakerPosition.RearLeft : SpeakerPosition.SurroundLeft,
                5 => sides ? SpeakerPosition.RearRight : SpeakerPosition.SurroundRight,
                6 => SpeakerPosition.FrontLeftCenter,
                7 => SpeakerPosition.FrontRightCenter,
                8 => SpeakerPosition.RearCenter,
                9 => SpeakerPosition.SurroundLeft,
                10 => SpeakerPosition.SurroundRight,
                11 => SpeakerPosition.TopMiddleCenter,
                12 => SpeakerPosition.TopFrontLeft,
                13 => SpeakerPosition.TopFrontCenter,
                14 => SpeakerPosition.TopFrontRight,
                15 => SpeakerPosition.TopRearLeft,
                16 => SpeakerPosition.TopRearCenter,
                _ => SpeakerPosition.TopRearRight,
            });
        }
        while (positions.Count < channels) positions.Add(SpeakerPosition.Unknown);
        return new SpeakerLayout(positions);
    }

    public bool Equals(SpeakerLayout? other) => other is not null && Positions.SequenceEqual(other.Positions);

    public override bool Equals(object? obj) => obj is SpeakerLayout other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var p in Positions) hash.Add(p);
        return hash.ToHashCode();
    }

    public static bool operator ==(SpeakerLayout? a, SpeakerLayout? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(SpeakerLayout? a, SpeakerLayout? b) => !(a == b);

    public override string ToString() => $"{Name} ({string.Join(" ", Positions.Select(p => p.Abbreviation()))})";
}
