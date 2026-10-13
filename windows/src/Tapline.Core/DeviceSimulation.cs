using System.Text.Json.Serialization;

namespace Tapline.Core;

// Port of Packages/TaplineCore/Sources/TaplineCore/DeviceSimulation.swift.

[JsonConverter(typeof(CamelCaseEnumConverter<SimulationKind>))]
public enum SimulationKind
{
    /// <summary>Headphones or earbuds, fitted from a squig.link measurement.</summary>
    Measured,
    /// <summary>One of the built-in speaker profiles: an approximation.</summary>
    Speaker,
}

/// <summary>How a device's measurement is compared with the user's sound.</summary>
public enum SimulationComparison
{
    /// <summary>Both come from the same database, so the same rig: the raw difference between the
    /// device and the target the correction aims at.</summary>
    SameRig,
    /// <summary>Different databases: each against its own database's target, so that what the rigs
    /// add (coupler, ear simulator, calibration) cancels.</summary>
    CrossRig,
}

/// <summary>Which curve stands for "the user's sound" when simulating a device: a target of
/// <see cref="ReferenceDatabase"/> (a squig.link database id).</summary>
public readonly record struct SimulationPlan(SimulationComparison Comparison, string ReferenceDatabase, string ReferenceTarget);

/// <summary>What a headphone correction was fitted to, so "Listen as" can compare another device with
/// the sound the correction aims at. Recorded when a squig.link or own-measurement correction is applied.</summary>
/// <param name="MeasurementDatabase">squig.link database (its id) the headphones were measured in; null for the user's own measurement.</param>
/// <param name="TargetDatabase">Database the target came from (the measurement's own, or a fallback).</param>
public sealed record CorrectionReference(string? MeasurementDatabase, string TargetDatabase, string TargetSite, string TargetName);

/// <summary>"Listen as": how the music would sound on another playback device, heard through the user's
/// own headphones. On Windows the device's curve is written into Equalizer APO's configuration after the
/// EQ (see <see cref="ApoConfig"/>), so the user's EQ and correction stay as they are, with the stereo
/// width of the device's speakers and a make-up gain that keeps the loudness when it is switched on or off.
///
/// Temporary: it is never part of <see cref="DspSettings"/>, so presets, A/B and undo never see it.</summary>
public sealed class DeviceSimulation : IEquatable<DeviceSimulation>
{
    /// <summary>Filters a simulation holds (the macOS kernel's block).</summary>
    public const int MaxStages = 16;
    /// <summary>Filters of a fitted headphone simulation, shelves included.</summary>
    public const int FittedFilters = 12;
    public const double GainMin = -12, GainMax = 12;

    /// <summary>The squig.link phone id, or the speaker profile's raw value.</summary>
    public string Id { get; }
    public string Name { get; }
    public SimulationKind Kind { get; }
    public IReadOnlyList<EqBand> Bands { get; }
    /// <summary>0 = mono, 1 = unchanged.</summary>
    public double Width { get; }
    /// <summary>Who measured a headphone (the squig.link database's site name).</summary>
    public string? Source { get; }

    [JsonConstructor]
    public DeviceSimulation(string id, string name, SimulationKind kind, IReadOnlyList<EqBand> bands, double width = 1, string? source = null)
    {
        Id = id ?? "";
        Name = name ?? "";
        Kind = kind;
        // The same budget as on macOS: more would be dropped there.
        Bands = (bands ?? []).Take(MaxStages).ToList();
        Width = double.IsNaN(width) ? 1 : Math.Clamp(width, 0, 1);
        Source = source;
    }

    /// <summary>The built-in profile this simulation plays, if it is one.</summary>
    [JsonIgnore]
    public SpeakerProfile? Profile => Kind == SimulationKind.Speaker ? SpeakerProfiles.FromRawValue(Id) : null;

    /// <summary>The bands as they are played: enabled, in range.</summary>
    [JsonIgnore]
    public IReadOnlyList<EqBand> PlayedBands => [.. Bands.Where(b => b.IsEnabled).Select(b => b.Clamped())];

    /// <summary>How much the device's curve changes the loudness of pink noise, in dB (LU, K-weighted
    /// like the EQ bypass). Earbuds that lose the bass come out quieter.</summary>
    public double LoudnessChangeDb(double sampleRate = 48_000)
    {
        var played = PlayedBands;
        return played.Count == 0 ? 0 : ResponseCurve.LoudnessChangeDb(played, sampleRate);
    }

    /// <summary>Gain before the device's filters that cancels their loudness change, so that switching
    /// the simulation compares sound, not level. Mono and narrow stereo leave a centred sound as loud
    /// as it was, so the width does not count.</summary>
    public double MakeupGainDb(double sampleRate = 48_000)
    {
        var gain = -LoudnessChangeDb(sampleRate);
        return double.IsFinite(gain) ? Math.Clamp(gain, GainMin, GainMax) : 0;
    }

    /// <summary>The make-up gain for a chain without a limiter behind it (Equalizer APO): lowered where the
    /// simulation would raise some frequency above the level <paramref name="settings"/>' EQ reaches by
    /// itself (0 dB with the automatic preamp), so it cannot clip what the EQ does not. Switching is then
    /// less than level-matched, by <see cref="LevelShortfallDb"/>.</summary>
    public double MakeupGainDb(DspSettings settings, double sampleRate = 48_000, bool levelMatchedBypass = true)
    {
        var gain = MakeupGainDb(sampleRate);
        var own = PlayedBands;
        if (own.Count == 0) return gain;
        IReadOnlyList<EqBand> eq = settings.EqEnabled ? [.. settings.ActiveBands.Select(b => b.Clamped())] : [];
        var baseDb = settings.EqEnabled ? settings.EffectivePreampDb(sampleRate)
            : (levelMatchedBypass ? settings.BypassGainDb(sampleRate) : 0);
        EqChannels?[] channels = settings.EqEnabled && settings.HasChannelSpecificBands ? [EqChannels.Left, EqChannels.Right] : [null];
        var device = ResponseCurve.ResponseDb(own, Probe, sampleRate);
        var allowed = gain;
        foreach (var channel in channels)
        {
            var before = ResponseCurve.ResponseDb(eq, Probe, sampleRate, channel).Select(v => v + baseDb).ToArray();
            var ceiling = Math.Max(0, before.Length > 0 ? before.Max() : 0);
            var peak = before.Zip(device, (a, b) => a + b).DefaultIfEmpty(0).Max();
            allowed = Math.Min(allowed, ceiling - peak);
        }
        return double.IsFinite(allowed) ? Math.Clamp(allowed, GainMin, GainMax) : 0;
    }

    /// <summary>How much quieter than level-matched the simulation plays with the headroom-keeping make-up gain, in dB.</summary>
    public double LevelShortfallDb(DspSettings settings, double sampleRate = 48_000, bool levelMatchedBypass = true) =>
        Math.Max(0, MakeupGainDb(sampleRate) - MakeupGainDb(settings, sampleRate, levelMatchedBypass));

    private static readonly double[] Probe = ResponseCurve.LogFrequencies(480);

    // MARK: - Headphones and earbuds

    /// <summary>Same rig when the device was measured in the database the user's headphones were.</summary>
    public static SimulationComparison Comparison(string deviceDatabase, CorrectionReference? correction) =>
        correction?.MeasurementDatabase == deviceDatabase ? SimulationComparison.SameRig : SimulationComparison.CrossRig;

    /// <summary>The reference for a device measured in <paramref name="deviceDatabase"/>, among the
    /// <paramref name="targets"/> of <paramref name="targetDatabase"/>: on a same-rig comparison the database
    /// the correction's target came from, else the device's own database (or its fallback when it has no
    /// targets). <paramref name="preferredTarget"/> if the database has it, else the one named like the user's
    /// target (on the same rig: the very target the correction aims at), else a Harman target, else the
    /// first. Null when no target is available.</summary>
    public static SimulationPlan? Plan(string deviceDatabase, string targetDatabase, IReadOnlyList<string> targets,
                                       CorrectionReference? correction, string? preferredTarget = null)
    {
        var name = preferredTarget != null && targets.Contains(preferredTarget)
            ? preferredTarget
            : ReferenceTarget(targets, correction?.TargetName);
        if (name == null) return null;
        return new SimulationPlan(Comparison(deviceDatabase, correction), targetDatabase, name);
    }

    /// <summary>The target of a database that best matches the user's target: the same name, else a
    /// Harman target, else the first.</summary>
    public static string? ReferenceTarget(IReadOnlyList<string> targets, string? userTarget)
    {
        if (userTarget != null && targets.FirstOrDefault(t => string.Equals(t, userTarget, StringComparison.OrdinalIgnoreCase)) is { } same) return same;
        return targets.FirstOrDefault(t => t.Contains("harman", StringComparison.OrdinalIgnoreCase)) ?? targets.FirstOrDefault();
    }

    /// <summary>Fits the EQ that turns <paramref name="reference"/> (what the user hears now: the target their
    /// correction aims at, or the device's own database's target) into <paramref name="device"/>, with the
    /// fitter acting on the device as its target. The fitter's treble handling applies: above ~10 kHz
    /// measurements differ from rig to rig and fit to fit, so only the broad trend is copied.</summary>
    public static EqFitter.Result Fit(FrequencyResponse device, FrequencyResponse reference, int filters = FittedFilters)
    {
        // Devices differ from a target far more than headphones need correcting.
        var options = new EqFitter.Options(maxFilters: Math.Min(filters, MaxStages)) { MaxBoostDb = 10, MaxCutDb = 18 };
        return EqFitter.Fit(reference, device, options);
    }

    public bool Equals(DeviceSimulation? other) =>
        other is not null && Id == other.Id && Name == other.Name && Kind == other.Kind && Width.Equals(other.Width)
        && Source == other.Source && Bands.SequenceEqual(other.Bands);

    public override bool Equals(object? obj) => obj is DeviceSimulation other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Kind, Width, Source, Bands.Count);
}

/// <summary>Built-in approximations of the speakers in everyday devices. They are written from general,
/// published knowledge of how such speakers behave (small drivers cannot move enough air for bass, small
/// boxes resonate, TV speakers fire downwards, a car cabin lifts the bass), not from measurements of any
/// product: they show a typical character, not one model.</summary>
public enum SpeakerProfile
{
    Phone,
    Laptop,
    Tv,
    BluetoothSpeaker,
    Car,
}

public static class SpeakerProfiles
{
    public static IReadOnlyList<SpeakerProfile> All { get; } = Enum.GetValues<SpeakerProfile>();

    /// <summary>The Swift raw value, used as the simulation's id.</summary>
    public static string RawValue(this SpeakerProfile profile) => profile switch
    {
        SpeakerProfile.Phone => "phone",
        SpeakerProfile.Laptop => "laptop",
        SpeakerProfile.Tv => "tv",
        SpeakerProfile.BluetoothSpeaker => "bluetoothSpeaker",
        _ => "car",
    };

    public static SpeakerProfile? FromRawValue(string rawValue) =>
        All.Where(p => p.RawValue() == rawValue).Select(p => (SpeakerProfile?)p).FirstOrDefault();

    /// <summary>English name; the app shows its own translation.</summary>
    public static string Name(this SpeakerProfile profile) => profile switch
    {
        SpeakerProfile.Phone => "Phone speaker",
        SpeakerProfile.Laptop => "Laptop speakers",
        SpeakerProfile.Tv => "TV speakers",
        SpeakerProfile.BluetoothSpeaker => "Small Bluetooth speaker",
        _ => "Car",
    };

    /// <summary>Stereo width of the speakers as heard from where one sits: 0 = mono.</summary>
    public static double Width(this SpeakerProfile profile) => profile switch
    {
        SpeakerProfile.Phone => 0,             // one speaker, or two a few centimetres apart
        SpeakerProfile.Laptop => 0.35,         // two drivers a hand's width apart, close to the ears
        SpeakerProfile.Tv => 0.6,
        SpeakerProfile.BluetoothSpeaker => 0,  // one box
        _ => 0.7,                              // car: off-centre seat, the near side dominates
    };

    public static IReadOnlyList<EqBand> Bands(this SpeakerProfile profile) => profile switch
    {
        // A micro speaker in a tiny chamber: nothing below its resonance around 800 Hz,
        // a honky hump above it, forward presence, and a soft top.
        SpeakerProfile.Phone =>
        [
            new(FilterType.HighPass, 750, 0, 1.0),
            new(FilterType.HighPass, 400, 0, 0.7),
            new(FilterType.Peak, 1100, 2, 1.4),
            new(FilterType.Peak, 3500, 4, 1.6),
            new(FilterType.Peak, 6500, -3, 2.5),
            new(FilterType.LowPass, 12_000, 0, 0.8),
        ],
        // Small drivers in the chassis: bass only from about 250 Hz, a boxy low midrange,
        // an uneven upper midrange, little air through the grilles.
        SpeakerProfile.Laptop =>
        [
            new(FilterType.HighPass, 280, 0, 1.2),
            new(FilterType.HighPass, 160, 0, 0.7),
            new(FilterType.Peak, 450, 2, 1.5),
            new(FilterType.Peak, 1800, 2, 1.5),
            new(FilterType.Peak, 4500, 2.5, 2),
            new(FilterType.HighShelf, 9000, -4, 0.7),
        ],
        // Slim drivers firing down or backwards: a bass bump from the processing above a
        // cut-off near 100 Hz, some boxiness, dialogue lifted, the treble lost to the table.
        SpeakerProfile.Tv =>
        [
            new(FilterType.HighPass, 110, 0, 1.1),
            new(FilterType.HighPass, 75, 0, 0.7),
            new(FilterType.Peak, 160, 2, 1.4),
            new(FilterType.Peak, 300, 1.5, 1.2),
            new(FilterType.Peak, 2500, 1.5, 1),
            new(FilterType.HighShelf, 5000, -5, 0.7),
            new(FilterType.LowPass, 15_000, 0, 0.7),
        ],
        // One small box with passive radiators: a strong bass bump just above a steep
        // cut-off, scooped mids, crisp treble that fades early.
        SpeakerProfile.BluetoothSpeaker =>
        [
            new(FilterType.HighPass, 75, 0, 1.3),
            new(FilterType.HighPass, 60, 0, 0.7),
            new(FilterType.Peak, 110, 4, 1.3),
            new(FilterType.Peak, 1200, -2, 0.9),
            new(FilterType.Peak, 5000, 2.5, 1.6),
            new(FilterType.LowPass, 14_000, 0, 0.7),
        ],
        // The closed cabin lifts the deep bass, door speakers and reflections leave a dip
        // in the upper bass and a peak from the windscreen, off-axis tweeters lose treble.
        _ =>
        [
            new(FilterType.HighPass, 28, 0, 0.7),
            new(FilterType.LowShelf, 70, 6, 0.7),
            new(FilterType.Peak, 250, -3, 1.4),
            new(FilterType.Peak, 2800, 2, 2),
            new(FilterType.HighShelf, 7000, -4, 0.7),
        ],
    };

    public static DeviceSimulation Simulation(this SpeakerProfile profile) =>
        new(profile.RawValue(), profile.Name(), SimulationKind.Speaker, profile.Bands(), profile.Width());
}
