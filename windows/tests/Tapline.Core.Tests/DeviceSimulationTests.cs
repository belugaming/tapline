using System.Text.Json;
using System.Text.Json.Nodes;
using Tapline.Core;

namespace Tapline.Core.Tests;

// Port of Packages/TaplineCore/Tests/TaplineCoreTests/DeviceSimulationTests.swift. The kernel tests
// (crossfade, channel handling in the realtime path) exercise the macOS C kernel; on Windows the same
// is checked on the Equalizer APO configuration in ApoConfigTests.

public class SpeakerProfileTests
{
    private const double Sr = 48_000;

    private static double Response(SpeakerProfile profile, double f) => ResponseCurve.ResponseDb(profile.Bands(), [f], Sr)[0];

    [Fact]
    public void EveryProfileFitsTheBudget()
    {
        foreach (var profile in SpeakerProfiles.All)
        {
            Assert.InRange(profile.Bands().Count, 1, DeviceSimulation.MaxStages);
            var sim = profile.Simulation();
            Assert.Equal(profile.Bands().Count, sim.Bands.Count);
            Assert.True(sim.Kind == SimulationKind.Speaker && sim.Profile == profile && sim.Id == profile.RawValue(), profile.ToString());
            Assert.All(profile.Bands(), b => Assert.Equal(b.Clamped(), b));
            Assert.Equal(profile, SpeakerProfiles.FromRawValue(profile.RawValue()));
        }
        Assert.Null(SpeakerProfiles.FromRawValue("gramophone"));
        // The ids are the macOS raw values.
        Assert.Equal("bluetoothSpeaker", SpeakerProfile.BluetoothSpeaker.RawValue());
    }

    [Fact]
    public void SmallSpeakersLoseTheBass()
    {
        Assert.True(Response(SpeakerProfile.Phone, 200) < Response(SpeakerProfile.Phone, 2000) - 20);
        Assert.True(Response(SpeakerProfile.Laptop, 80) < Response(SpeakerProfile.Laptop, 1000) - 20);
        Assert.True(Response(SpeakerProfile.Tv, 40) < Response(SpeakerProfile.Tv, 1000) - 15);
        Assert.True(Response(SpeakerProfile.BluetoothSpeaker, 35) < Response(SpeakerProfile.BluetoothSpeaker, 110) - 15);
        // A phone is the most limited of all, a TV reaches deeper than a laptop.
        Assert.True(Response(SpeakerProfile.Phone, 300) < Response(SpeakerProfile.Laptop, 300));
        Assert.True(Response(SpeakerProfile.Laptop, 120) < Response(SpeakerProfile.Tv, 120));
    }

    [Fact]
    public void ProfilesHaveTheirTypicalResonances()
    {
        // Passive radiators: a bass bump above the cut-off.
        Assert.True(Response(SpeakerProfile.BluetoothSpeaker, 110) > Response(SpeakerProfile.BluetoothSpeaker, 1000) + 4);
        // Phones push the presence region.
        Assert.True(Response(SpeakerProfile.Phone, 3200) > Response(SpeakerProfile.Phone, 2000) + 1);
        // A car cabin lifts the deep bass; off-axis tweeters lose treble.
        Assert.True(Response(SpeakerProfile.Car, 40) > Response(SpeakerProfile.Car, 1000) + 3);
        Assert.True(Response(SpeakerProfile.Car, 12_000) < Response(SpeakerProfile.Car, 1000) - 2);
        // Down-firing TV speakers lose treble, and a phone's top end fades.
        Assert.True(Response(SpeakerProfile.Tv, 10_000) < Response(SpeakerProfile.Tv, 1000) - 3);
        Assert.True(Response(SpeakerProfile.Phone, 16_000) < Response(SpeakerProfile.Phone, 3000) - 5);
    }

    [Fact]
    public void ChannelHandlingFollowsTheDevice()
    {
        Assert.Equal(0, SpeakerProfile.Phone.Width());
        Assert.Equal(0, SpeakerProfile.BluetoothSpeaker.Width());
        Assert.True(SpeakerProfile.Laptop.Width() > 0 && SpeakerProfile.Laptop.Width() < SpeakerProfile.Tv.Width());
        Assert.True(SpeakerProfile.Tv.Width() < 1 && SpeakerProfile.Car.Width() < 1 && SpeakerProfile.Car.Width() > 0);
        Assert.All(SpeakerProfiles.All, p => Assert.Equal(p.Width(), p.Simulation().Width));
    }
}

public class SimulationLevelTests
{
    private const double Sr = 48_000;

    [Fact]
    public void MakeupGainCancelsTheLoudnessChange()
    {
        foreach (var profile in SpeakerProfiles.All)
        {
            var sim = profile.Simulation();
            var makeup = sim.MakeupGainDb(Sr);
            Assert.InRange(makeup, DeviceSimulation.GainMin, DeviceSimulation.GainMax);
            Assert.True(Math.Abs(sim.LoudnessChangeDb(Sr) + makeup) < 1e-9, $"{profile}: {makeup}");
            // The same through the loudness model: the curve plus a flat make-up gain.
            var total = ResponseCurve.LoudnessChangeDb([.. sim.Bands, new EqBand(FilterType.HighShelf, 1, makeup, 0.71)], Sr);
            Assert.True(Math.Abs(total) < 0.05, $"{profile}: {total}");
        }
        // Earbuds that lose the bass come out quieter, so they are made up for. A phone loses
        // even more bass, but its forward presence keeps the K-weighted loudness close.
        var leaky = new DeviceSimulation("x", "x", SimulationKind.Measured, [new EqBand(FilterType.LowShelf, 200, -10, 0.7)]);
        Assert.True(leaky.MakeupGainDb(Sr) > 0.5);
        Assert.True(Math.Abs(SpeakerProfile.Phone.Simulation().MakeupGainDb(Sr)) < 3);
        Assert.Equal(0, new DeviceSimulation("x", "x", SimulationKind.Measured, []).MakeupGainDb());
    }

    [Fact]
    public void MakeupGainStaysInRange()
    {
        var loud = new DeviceSimulation("x", "x", SimulationKind.Measured, [new EqBand(FilterType.HighShelf, 200, 24, 0.71)]);
        Assert.Equal(DeviceSimulation.GainMin, loud.MakeupGainDb());
        var broken = new DeviceSimulation("x", "x", SimulationKind.Measured, [new EqBand(FilterType.Peak, double.NaN, double.NaN, double.NaN)]);
        Assert.True(double.IsFinite(broken.MakeupGainDb()));
        Assert.True(double.IsFinite(broken.MakeupGainDb(DspSettings.Flat)));
    }

    [Fact]
    public void WithoutALimiterTheMakeupKeepsTheHeadroom()
    {
        var sim = SpeakerProfile.Phone.Simulation();
        var curve = ResponseCurve.LogFrequencies(480);
        var device = ResponseCurve.ResponseDb(sim.Bands, curve, Sr);

        // A flat EQ leaves no headroom: the simulation may not raise anything above 0 dB.
        var flat = DspSettings.Flat;
        var safe = sim.MakeupGainDb(flat, Sr);
        Assert.True(safe < sim.MakeupGainDb(Sr));
        Assert.True(device.Max() + safe <= 1e-6);
        Assert.Equal(sim.MakeupGainDb(Sr) - safe, sim.LevelShortfallDb(flat, Sr), 9);

        // A correction's preamp leaves room in the midrange: the full make-up gain fits.
        var corrected = new DspSettings(bands: [new EqBand(FilterType.LowShelf, 105, 6, 0.7)]);
        Assert.Equal(sim.MakeupGainDb(Sr), sim.MakeupGainDb(corrected, Sr), 9);
        Assert.Equal(0, sim.LevelShortfallDb(corrected, Sr));

        // The EQ bypassed at its own loudness leaves the same room as the EQ's preamp.
        corrected.EqEnabled = false;
        Assert.True(sim.MakeupGainDb(corrected, Sr, levelMatchedBypass: true) > sim.MakeupGainDb(corrected, Sr, levelMatchedBypass: false));
    }
}

public class SimulationStageBudgetTests
{
    [Fact]
    public void ASimulationKeepsAtMostSixteenFilters()
    {
        var many = Enumerable.Range(0, 20).Select(i => new EqBand(FilterType.Peak, 100 + 100 * i, 1, 1)).ToList();
        var sim = new DeviceSimulation("x", "x", SimulationKind.Measured, many, 3);
        Assert.Equal(DeviceSimulation.MaxStages, sim.Bands.Count);
        Assert.Equal(many.Take(DeviceSimulation.MaxStages), sim.Bands);
        Assert.Equal(1, sim.Width);

        // Stored by a version with more filters: still within the budget.
        var json = (JsonObject)JsonSerializer.SerializeToNode(sim, TaplineJson.Options)!;
        json["bands"] = JsonSerializer.SerializeToNode(many, TaplineJson.Options);
        json["width"] = -1;
        var decoded = json.Deserialize<DeviceSimulation>(TaplineJson.Options)!;
        Assert.Equal(DeviceSimulation.MaxStages, decoded.Bands.Count);
        Assert.Equal(0, decoded.Width);
        Assert.Equal(sim, JsonSerializer.Deserialize<DeviceSimulation>(JsonSerializer.Serialize(sim, TaplineJson.Options), TaplineJson.Options));

        // The same field names as on macOS.
        var measured = new DeviceSimulation("db|AirPods", "AirPods", SimulationKind.Measured, many, source: "Rig A");
        var node = JsonSerializer.SerializeToNode(measured, TaplineJson.Options)!;
        Assert.Equal("measured", (string?)node["kind"]);
        Assert.Equal("Rig A", (string?)node["source"]);
        Assert.Null(node["profile"]);
        Assert.Equal("Rig A", node.Deserialize<DeviceSimulation>(TaplineJson.Options)!.Source);
    }

    [Fact]
    public void AFittedSimulationFitsTheBudget()
    {
        var grid = ResponseCurve.LogFrequencies(400);
        var wobbly = new FrequencyResponse(grid, [.. grid.Select(f => 70 + 6 * Math.Sin(Math.Log2(f) * 3))]);
        var flat = new FrequencyResponse(grid, [.. grid.Select(_ => 70.0)]);
        var r = DeviceSimulation.Fit(wobbly, flat, filters: 40);
        Assert.InRange(r.Bands.Count, 1, DeviceSimulation.MaxStages);
    }

    [Fact]
    public void CorrectionReferencesSurviveJson()
    {
        var reference = new CorrectionReference("https://a.squig.link/", "https://a.squig.link/", "Rig A", "Harman");
        var json = JsonSerializer.Serialize(reference, TaplineJson.Options);
        Assert.Contains("\"measurementDatabase\"", json);
        Assert.Equal(reference, JsonSerializer.Deserialize<CorrectionReference>(json, TaplineJson.Options));
        var own = new CorrectionReference(null, "https://b.squig.link/", "Rig B", "Harman");
        Assert.Equal(own, JsonSerializer.Deserialize<CorrectionReference>(JsonSerializer.Serialize(own, TaplineJson.Options), TaplineJson.Options));
    }
}

public class HeadphoneSimulationTests
{
    private const double Sr = 48_000;
    private static readonly double[] Grid = ResponseCurve.LogFrequencies(600);

    /// A "neutral" curve with the shape of a headphone target (bass shelf, ear gain).
    private static readonly EqBand[] Neutral = [new(FilterType.LowShelf, 105, 6, 0.7), new(FilterType.Peak, 3000, 8, 1)];
    /// What two measurement rigs add to everything they measure, targets included.
    private static readonly EqBand[] RigA = [new(FilterType.LowShelf, 80, -4, 0.7), new(FilterType.Peak, 4000, 5, 2)];
    private static readonly EqBand[] RigB = [new(FilterType.LowShelf, 200, 3, 0.7), new(FilterType.Peak, 1500, 4, 1.2)];
    /// How the simulated device differs from neutral: leaky earbud bass, a presence peak, a dip.
    private static readonly EqBand[] Device = [new(FilterType.LowShelf, 150, -10, 0.7), new(FilterType.Peak, 2500, 4, 1.5), new(FilterType.Peak, 6000, -3, 3)];

    private static FrequencyResponse Curve(params EqBand[][] bands) =>
        new(Grid, [.. ResponseCurve.ResponseDb(bands.SelectMany(b => b), Grid, Sr).Select(v => 80 + v)]);

    /// RMS difference between two curves from 30 Hz to 5 kHz, after matching their level at 200 Hz - 1.2 kHz.
    private static double Deviation(IReadOnlyList<double> a, IReadOnlyList<double> b, IReadOnlyList<double> f)
    {
        var mids = Enumerable.Range(0, f.Count).Where(i => f[i] >= 200 && f[i] <= 1200).ToList();
        var offset = mids.Sum(i => a[i] - b[i]) / mids.Count;
        var band = Enumerable.Range(0, f.Count).Where(i => f[i] >= 30 && f[i] <= 5000).ToList();
        return Math.Sqrt(band.Sum(i => Math.Pow(a[i] - b[i] - offset, 2)) / band.Count);
    }

    private static double[] SimulatedChange(EqFitter.Result r) => ResponseCurve.ResponseDb(r.Bands, r.Frequencies, Sr);

    private static double[] Truth => ResponseCurve.ResponseDb(Device, EqFitter.Grid, Sr);

    [Fact]
    public void FitsTheDifferenceToTheDevice()
    {
        var r = DeviceSimulation.Fit(Curve(Neutral, Device), Curve(Neutral));
        Assert.True(r.Bands.Count <= DeviceSimulation.FittedFilters);
        // The user's sound with the simulation follows the device...
        Assert.True(Deviation(r.Predicted, r.Target, r.Frequencies) < 0.8);
        // ...because the EQ is the device's difference from the reference.
        Assert.True(Deviation(SimulatedChange(r), Truth, r.Frequencies) < 0.8);
        // Earbud bass is far below what a correction would cut.
        Assert.True(SimulatedChange(r)[0] < -7);
    }

    [Fact]
    public void SameRigUsesTheRawDifference()
    {
        var correction = new CorrectionReference("A", "A", "Rig A", "Harman");
        var plan = DeviceSimulation.Plan("A", "A", ["Diffuse field", "Harman"], correction);
        Assert.NotNull(plan);
        Assert.Equal(SimulationComparison.SameRig, plan.Value.Comparison);
        Assert.Equal("A", plan.Value.ReferenceDatabase);
        Assert.Equal("Harman", plan.Value.ReferenceTarget);
        Assert.Equal(SimulationComparison.SameRig, DeviceSimulation.Comparison("A", correction));
        Assert.Equal(SimulationComparison.CrossRig, DeviceSimulation.Comparison("B", correction));
        Assert.Equal(SimulationComparison.CrossRig, DeviceSimulation.Comparison("A", null));
        // The user can compare with another target of the same database.
        Assert.Equal("Diffuse field", DeviceSimulation.Plan("A", "A", ["Diffuse field", "Harman"], correction, "Diffuse field")?.ReferenceTarget);
        // The device and the user's target, both as rig A measures them.
        var r = DeviceSimulation.Fit(Curve(Neutral, Device, RigA), Curve(Neutral, RigA));
        Assert.True(Deviation(SimulatedChange(r), Truth, r.Frequencies) < 0.8);
    }

    [Fact]
    public void CrossRigGoesThroughEachDatabasesTarget()
    {
        var correction = new CorrectionReference("A", "A", "Rig A", "Harman 2018");
        var plan = DeviceSimulation.Plan("B", "B", ["Flat", "harman 2018", "Harman 2019"], correction);
        Assert.NotNull(plan);
        Assert.Equal(SimulationComparison.CrossRig, plan.Value.Comparison);
        Assert.Equal("B", plan.Value.ReferenceDatabase);
        Assert.Equal("harman 2018", plan.Value.ReferenceTarget);

        // The device on rig B against rig B's target: the rigs' differences cancel.
        var deviceOnB = Curve(Neutral, Device, RigB);
        var viaTarget = DeviceSimulation.Fit(deviceOnB, Curve(Neutral, RigB));
        Assert.True(Deviation(SimulatedChange(viaTarget), Truth, viaTarget.Frequencies) < 0.8);
        // The raw difference to the user's target on rig A would carry both rigs along.
        var raw = DeviceSimulation.Fit(deviceOnB, Curve(Neutral, RigA));
        Assert.True(Deviation(SimulatedChange(raw), Truth, raw.Frequencies) > 2);
    }

    [Fact]
    public void CrossRigPicksTheClosestTarget()
    {
        var harman = new CorrectionReference(null, "B", "Rig B", "Harman");
        // The user's own measurement compared with a device from the database its target came from:
        // a cross-rig comparison, with the very same target.
        Assert.Equal(SimulationComparison.CrossRig, DeviceSimulation.Plan("B", "B", ["Harman"], harman)?.Comparison);
        // No correction known: a Harman target, else the first.
        Assert.Equal("Harman IE 2019", DeviceSimulation.Plan("C", "C", ["Flat", "IEF Neutral", "Harman IE 2019"], null)?.ReferenceTarget);
        Assert.Equal("Flat", DeviceSimulation.Plan("C", "C", ["Flat", "Neutral"], null)?.ReferenceTarget);
        // A fallback database's targets when the device's database has none.
        Assert.Equal("F", DeviceSimulation.Plan("C", "F", ["Harman"], null)?.ReferenceDatabase);
        // The user's choice wins, if the database has it.
        Assert.Equal("Flat", DeviceSimulation.Plan("C", "C", ["Flat", "Harman"], null, "Flat")?.ReferenceTarget);
        Assert.Equal("Harman", DeviceSimulation.Plan("C", "C", ["Flat", "Harman"], null, "Gone")?.ReferenceTarget);
        // Nothing to compare with.
        Assert.Null(DeviceSimulation.Plan("C", "C", [], harman));
    }

    [Fact]
    public void TrebleIsOnlyBroadlySimulated()
    {
        // A narrow 13 kHz peak, typical of one rig and one fit, is not worth copying.
        var peaky = Curve(Neutral, [new EqBand(FilterType.Peak, 13_000, 8, 8)]);
        var r = DeviceSimulation.Fit(peaky, Curve(Neutral));
        Assert.All(r.Bands.Where(b => b.Type == FilterType.Peak), b => Assert.True(b.Frequency <= 10_000));
        var at13k = ResponseCurve.ResponseDb(r.Bands, [13_000.0], Sr)[0];
        Assert.True(at13k < 3, $"13 kHz: {at13k} dB");
    }
}
