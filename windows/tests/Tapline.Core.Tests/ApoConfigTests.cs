using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Tapline.Core;

namespace Tapline.Core.Tests;

/// Checks the generated Equalizer APO configuration by interpreting it the way Equalizer APO does
/// (following its FilterEngine, ChannelHelper, CopyFilter, BiQuadFilterFactory and DelayFilter
/// sources) and comparing the resulting transfer functions with Tapline's own curves and the
/// macOS kernel's effect formulas.
public class ApoConfigTests
{
    private const double SampleRate = 48_000;

    /// Transfer from input L and R to output L and R at one frequency.
    private readonly record struct Transfer(Complex LL, Complex LR, Complex RL, Complex RR);

    private static readonly Regex TypeRegex = new(@"^\s*ON\s+([A-Za-z]+)");
    private static readonly Regex FreqRegex = new(@"\s+Fc\s*([-+0-9.eE]+)\s*H\s*z");
    private static readonly Regex GainRegex = new(@"\s+Gain\s*([-+0-9.eE]+)\s*dB");
    private static readonly Regex QRegex = new(@"\s+Q\s*([-+0-9.eE]+)");

    private static double Parse(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary><paramref name="files"/>: impulse responses for Convolution commands, by the path in the configuration.</summary>
    private static Transfer Interpret(string config, double f, int channels = 2, IReadOnlyDictionary<string, float[]>? files = null)
    {
        var all = new List<string> { "L", "R" };
        for (var i = 3; i <= channels; i++) all.Add("C" + i);
        var state = new Dictionary<string, (Complex L, Complex R)>();
        foreach (var c in all) state[c] = c switch { "L" => (Complex.One, Complex.Zero), "R" => (Complex.Zero, Complex.One), _ => (Complex.Zero, Complex.Zero) };
        var selected = new List<string>(all);

        string Resolve(string word, List<string> names)
        {
            if (!char.IsAsciiDigit(word[0])) return word;
            var index = int.Parse(word, CultureInfo.InvariantCulture) - 1;
            Assert.InRange(index, 0, names.Count - 1);
            return names[index];
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
                    Assert.All(selected, s => Assert.True(state.ContainsKey(s), s));
                    break;
                case "Preamp":
                {
                    var g = Math.Pow(10, Parse(Regex.Match(parameters, @"[-+0-9.]+").Value) / 20);
                    foreach (var c in selected) state[c] = (state[c].L * g, state[c].R * g);
                    break;
                }
                case "Convolution":
                {
                    // As ConvolutionFilter: a mono file is applied to each selected channel.
                    var path = parameters.Trim();
                    Assert.True(files != null && files.TryGetValue(path, out _), path);
                    var h = files![path];
                    Complex response = 0;
                    for (var n = 0; n < h.Length; n++)
                        response += h[n] * Complex.Exp(new Complex(0, -2 * Math.PI * f * n / SampleRate));
                    foreach (var c in selected) state[c] = (state[c].L * response, state[c].R * response);
                    break;
                }
                case "Delay":
                {
                    // As DelayFilter: in milliseconds or in samples.
                    var samples = parameters.TrimEnd().EndsWith("samples", StringComparison.Ordinal);
                    Assert.True(samples || parameters.TrimEnd().EndsWith("ms", StringComparison.Ordinal), line);
                    var value = Parse(Regex.Match(parameters, @"[-+0-9.]+").Value);
                    var seconds = samples ? value / SampleRate : value / 1000;
                    var z = Complex.Exp(new Complex(0, -2 * Math.PI * f * seconds));
                    foreach (var c in selected) state[c] = (state[c].L * z, state[c].R * z);
                    break;
                }
                case "Copy":
                {
                    var next = new Dictionary<string, (Complex L, Complex R)>(state);
                    var targets = new List<string>();
                    foreach (var assignment in parameters.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = assignment.Split('=');
                        Assert.Equal(2, parts.Length);
                        var target = Resolve(parts[0], selected);
                        Complex l = 0, r = 0;
                        foreach (var summand in parts[1].Split('+'))
                        {
                            var factors = summand.Split('*');
                            // A bare integer would be a channel number: factors and constants carry a decimal point.
                            Assert.Contains('.', factors[0]);
                            if (factors.Length == 1) continue; // a constant (e.g. 0.0 = silence) adds no signal
                            Assert.Equal(2, factors.Length);
                            var source = Resolve(factors[1], selected);
                            Assert.True(state.ContainsKey(source), source);
                            l += Parse(factors[0]) * state[source].L;
                            r += Parse(factors[0]) * state[source].R;
                        }
                        next[target] = (l, r);
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
                    if (type.UsesGain()) Assert.True(gain.Success, line);
                    var q = QRegex.Match(rest);
                    Assert.True(q.Success, line);
                    var c0 = BiquadCoefficients.Design(type, fc, gain.Success ? Parse(gain.Groups[1].Value) : 0, Parse(q.Groups[1].Value), SampleRate);
                    var z1 = Complex.Exp(new Complex(0, -2 * Math.PI * f / SampleRate));
                    var h = (c0.B0 + c0.B1 * z1 + c0.B2 * z1 * z1) / (1 + c0.A1 * z1 + c0.A2 * z1 * z1);
                    foreach (var c in selected) state[c] = (state[c].L * h, state[c].R * h);
                    break;
                }
            }
        }
        return new Transfer(state["L"].L, state["L"].R, state["R"].L, state["R"].R);
    }

    private static string Build(DspSettings s, int channels = 2, DeviceSimulation? simulation = null, bool levelMatchedBypass = true) =>
        ApoConfig.Build(true, [new ApoConfig.Device("{00000000-1111-2222-3333-444444444444}", "Test", channels, SampleRate, s, Simulation: simulation)], levelMatchedBypass);

    private static double Db(Complex c) => 20 * Math.Log10(Math.Max(c.Magnitude, 1e-15));

    [Fact]
    public void EqMatchesTheGraphPerChannel()
    {
        var s = DspSettings.Flat;
        s.Bands =
        [
            new EqBand(FilterType.HighPass, 30, 0, 0.71),
            new EqBand(FilterType.LowShelf, 105, 4.2, 0.7),
            new EqBand(FilterType.Peak, 1875, 3.4, 1.8),
            new EqBand(FilterType.Notch, 6000, 0, 4),
            new EqBand(FilterType.BandPass, 900, 0, 0.5, isEnabled: false),
            new EqBand(FilterType.HighShelf, 10_000, -2.5, 0.7),
            new EqBand(FilterType.LowPass, 18_000, 0, 0.71),
            new EqBand(FilterType.Peak, 68, -4, 4, channels: EqChannels.Left),
            new EqBand(FilterType.Peak, 68, 3, 4, channels: EqChannels.Right),
        ];
        s.Tone = new ToneSettings(bass: 2, clarity: -1.5);
        var config = Build(s);
        var frequencies = ResponseCurve.LogFrequencies(80);
        var left = ResponseCurve.ResponseDb(s.ActiveBands, frequencies, SampleRate, EqChannels.Left);
        var right = ResponseCurve.ResponseDb(s.ActiveBands, frequencies, SampleRate, EqChannels.Right);
        var preamp = s.EffectivePreampDb(SampleRate);
        Assert.True(preamp < -1);
        for (var i = 0; i < frequencies.Length; i++)
        {
            var t = Interpret(config, frequencies[i]);
            // The file keeps two decimals of preamp and gain.
            Assert.InRange(Db(t.LL) - (left[i] + preamp), -0.01, 0.01);
            Assert.InRange(Db(t.RR) - (right[i] + preamp), -0.01, 0.01);
            Assert.True(t.LR.Magnitude < 1e-12 && t.RL.Magnitude < 1e-12);
        }
    }

    [Theory]
    [InlineData(1.5, false, false, 0.3, -2.0)]
    [InlineData(0.4, true, false, -0.5, 3.0)]
    [InlineData(1.0, false, true, 0.0, 0.0)]
    [InlineData(1.0, true, false, 0.0, 0.0)]
    [InlineData(0.0, false, false, 1.0, 0.0)]
    public void EffectsFollowTheKernel(double width, bool swap, bool mono, double balance, double gainDb)
    {
        var s = DspSettings.Flat;
        s.Effects = new EffectsSettings { Width = width, SwapChannels = swap, Mono = mono, Balance = balance, OutputGainDb = gainDb };
        var t = Interpret(Build(s), 1000);

        // The macOS kernel: swap, width (mono = 0), balance, output gain.
        (double L, double R) Kernel(double l, double r)
        {
            if (swap) (l, r) = (r, l);
            var w = mono ? 0 : width;
            var m = 0.5 * (l + r);
            var side = 0.5 * (l - r) * w;
            (l, r) = (m + side, m - side);
            if (balance > 0) l *= 1 - balance; else r *= 1 + balance;
            var g = Math.Pow(10, gainDb / 20);
            return (l * g, r * g);
        }
        var fromL = Kernel(1, 0);
        var fromR = Kernel(0, 1);
        Assert.Equal(fromL.L, t.LL.Real, 5);
        Assert.Equal(fromL.R, t.RL.Real, 5);
        Assert.Equal(fromR.L, t.LR.Real, 5);
        Assert.Equal(fromR.R, t.RR.Real, 5);
    }

    [Fact]
    public void CrossfeedIsALowPassedDelayedCrossPath()
    {
        var s = DspSettings.Flat;
        s.Effects = new EffectsSettings { CrossfeedEnabled = true, Crossfeed = 0.5, Balance = 0.2 };
        var config = Build(s);
        var kx = 0.45 * 0.5;
        var norm = 1 / (1 + 0.7 * kx);
        var low = Interpret(config, 30);
        Assert.Equal(norm * 0.8, low.LL.Magnitude, 3);
        Assert.Equal(norm * kx * 0.8, low.LR.Magnitude, 2);
        Assert.Equal(norm * kx, low.RL.Magnitude, 2);
        var high = Interpret(config, 8000);
        Assert.True(high.LR.Magnitude < norm * kx * 0.05);
        // The cross path arrives ~0.3 ms late: phase at 300 Hz lags by more than the low-pass alone.
        Assert.True(Interpret(config, 300).LR.Phase < -0.5);
    }

    /// <summary>A small, deterministic set of four different decaying responses.</summary>
    private static BinauralIrs TestSpeakers(int length = 600)
    {
        var random = new Random(23);
        float[] Ir(double scale) => Enumerable.Range(0, length)
            .Select(i => (float)((random.NextDouble() * 2 - 1) * scale * Math.Exp(-i / (length / 5.0)))).ToArray();
        return new BinauralIrs(SampleRate, [Ir(1), Ir(0.3), Ir(0.3), Ir(1)]);
    }

    /// <summary>The files Tapline writes for a set, and their samples as Equalizer APO reads them.</summary>
    private static (ApoConfig.SpeakerFiles Reference, Dictionary<string, float[]> Samples) SpeakerFiles(BinauralIrs irs, string id = "test")
    {
        var files = VirtualSpeakerFiles.Build(irs, id, SampleRate);
        var reference = VirtualSpeakerFiles.Reference(files);
        var samples = files.ToDictionary(f => VirtualSpeakerFiles.Folder + @"\" + f.Name, f =>
        {
            var wav = WavFile.Parse(f.Data);
            Assert.Equal(SampleRate, wav.SampleRate);
            Assert.Single(wav.Channels);
            return wav.Channels[0];
        });
        return (reference, samples);
    }

    /// <summary>Equal up to the file's float samples and the configuration's six decimals.</summary>
    private static bool Close(Complex actual, Complex expected) => (actual - expected).Magnitude <= 2e-5 * (1 + expected.Magnitude);

    private static Complex Dft(float[] h, double f)
    {
        Complex sum = 0;
        for (var n = 0; n < h.Length; n++) sum += h[n] * Complex.Exp(new Complex(0, -2 * Math.PI * f * n / SampleRate));
        return sum;
    }

    [Fact]
    public void VirtualSpeakersConvolveEachEarWithBothSpeakers()
    {
        var irs = TestSpeakers();
        var (reference, samples) = SpeakerFiles(irs);
        var s = DspSettings.Flat;
        // Crossfeed stays off whatever its own switch says.
        s.Effects = new EffectsSettings { VirtualSpeakersEnabled = true, CrossfeedEnabled = true, Balance = 0.25, OutputGainDb = -2 };
        var config = ApoConfig.Build(true, [new ApoConfig.Device("{x}", "Test", 2, SampleRate, s, reference)]);
        Assert.Contains(@"Convolution: Tapline\VirtualSpeakers\test-48000-FL-L.wav", config);
        Assert.DoesNotContain("Delay:", config);

        var prepared = irs.Prepared(SampleRate);
        var gain = prepared.LoudnessGain;
        var output = Math.Pow(10, -2 / 20.0);
        foreach (var f in new[] { 60.0, 440, 1000, 3000, 9000 })
        {
            var t = Interpret(config, f, files: samples);
            Complex Expected(int i) => Dft(prepared.Responses[i], f) * gain * output;
            // Left ear: both speakers' paths to it, with the balance cut on the left.
            Assert.True(Close(t.LL, Expected(BinauralIrs.FrontLeftToLeft) * 0.75), $"{f} Hz");
            Assert.True(Close(t.LR, Expected(BinauralIrs.FrontRightToLeft) * 0.75), $"{f} Hz");
            Assert.True(Close(t.RL, Expected(BinauralIrs.FrontLeftToRight)), $"{f} Hz");
            Assert.True(Close(t.RR, Expected(BinauralIrs.FrontRightToRight)), $"{f} Hz");
        }
    }

    [Fact]
    public void VirtualSpeakersFollowSwapAndWidth()
    {
        var irs = TestSpeakers(300);
        var (reference, samples) = SpeakerFiles(irs);
        var s = DspSettings.Flat;
        s.Effects = new EffectsSettings { VirtualSpeakersEnabled = true, SwapChannels = true };
        var config = ApoConfig.Build(true, [new ApoConfig.Device("{x}", "Test", 2, SampleRate, s, reference)]);
        var prepared = irs.Prepared(SampleRate);
        var gain = prepared.LoudnessGain;
        var t = Interpret(config, 1000, files: samples);
        // Swapped: the left input plays from the right speaker.
        Assert.True(Close(t.LL, Dft(prepared.Responses[BinauralIrs.FrontRightToLeft], 1000) * gain));
        Assert.True(Close(t.RR, Dft(prepared.Responses[BinauralIrs.FrontLeftToRight], 1000) * gain));
    }

    [Fact]
    public void VirtualSpeakersNeedTheirFiles()
    {
        var s = DspSettings.Flat;
        s.Effects = new EffectsSettings { VirtualSpeakersEnabled = true, CrossfeedEnabled = true };
        // The files could not be written: the device plays without the effect (and without crossfeed).
        var config = Build(s);
        Assert.DoesNotContain("Convolution", config);
        Assert.DoesNotContain("Copy:", config);
        // Mono devices never get it.
        var (reference, _) = SpeakerFiles(TestSpeakers(100));
        var mono = ApoConfig.Build(true, [new ApoConfig.Device("{x}", "Test", 1, SampleRate, s, reference)]);
        Assert.DoesNotContain("Convolution", mono);
        // Switched off: the files are ignored.
        s.Effects = new EffectsSettings { VirtualSpeakersEnabled = false };
        Assert.DoesNotContain("Convolution", ApoConfig.Build(true, [new ApoConfig.Device("{x}", "Test", 2, SampleRate, s, reference)]));
    }

    [Fact]
    public void VirtualSpeakersKeepSurroundChannels()
    {
        var (reference, samples) = SpeakerFiles(TestSpeakers(100));
        var s = DspSettings.Flat;
        s.Effects = new EffectsSettings { VirtualSpeakersEnabled = true };
        var config = ApoConfig.Build(true, [new ApoConfig.Device("{x}", "Test", 6, SampleRate, s, reference)]);
        Assert.Contains("3=1.0*3", config);
        Assert.Contains("6=1.0*6", config);
        Interpret(config, 1000, channels: 6, files: samples);
    }

    [Fact]
    public void SurroundDevicesKeepTheirOtherChannels()
    {
        var s = DspSettings.Flat;
        s.Effects = new EffectsSettings { CrossfeedEnabled = true, Width = 1.2 };
        var config = Build(s, channels: 6);
        Assert.Contains("3=1.0*3", config);
        Assert.Contains("6=1.0*6", config);
        Interpret(config, 1000, channels: 6);
    }

    [Fact]
    public void MonoDevicesGetNoStereoCommands()
    {
        var s = DspSettings.Flat;
        s.Bands = [new EqBand(FilterType.Peak, 1000, 3, 1, channels: EqChannels.Left), new EqBand(FilterType.Peak, 2000, 3, 1, channels: EqChannels.Right)];
        s.Effects = new EffectsSettings { CrossfeedEnabled = true, Balance = 0.5, OutputGainDb = -3 };
        var config = Build(s, channels: 1);
        Assert.DoesNotContain("Copy:", config);
        Assert.DoesNotContain("Channel: 2", config);
        Assert.Contains("Fc 1000 Hz", config);
        Assert.DoesNotContain("Fc 2000 Hz", config);
        Assert.Contains("Preamp: -3 dB", config);
    }

    [Fact]
    public void FlatSettingsWriteNoFilters()
    {
        var config = Build(DspSettings.Flat);
        Assert.DoesNotContain("Filter", config);
        Assert.DoesNotContain("Copy:", config);
        Assert.Contains("Device: {00000000-1111-2222-3333-444444444444}", config);
    }

    [Fact]
    public void OffWritesOnlyComments()
    {
        var s = DspSettings.Flat;
        s.Bands = [new EqBand(FilterType.Peak, 1000, 6, 1)];
        var config = ApoConfig.Build(false, [new ApoConfig.Device("{x}", "Test", 2, SampleRate, s)]);
        Assert.StartsWith(ApoConfig.HeaderLine, config);
        Assert.All(config.Split('\n'), l => Assert.True(l.Trim().Length == 0 || l.StartsWith('#'), l));
    }

    [Fact]
    public void EqOffKeepsTheEffects()
    {
        var s = DspSettings.Flat;
        s.EqEnabled = false;
        s.Bands = [new EqBand(FilterType.Peak, 1000, 6, 1)];
        s.Effects = new EffectsSettings { Mono = true };
        var config = ApoConfig.Build(true, [new ApoConfig.Device("{x}", "Test", 2, SampleRate, s)], levelMatchedBypass: false);
        Assert.DoesNotContain("Filter", config);
        Assert.DoesNotContain("Preamp", config);
        Assert.Contains("Copy:", config);
    }

    [Fact]
    public void EqOffIsLevelMatched()
    {
        var s = DspSettings.Flat;
        s.EqEnabled = false;
        s.Bands = [new EqBand(FilterType.LowShelf, 105, 6, 0.7), new EqBand(FilterType.Peak, 1000, 6, 1)];
        s.Effects = new EffectsSettings { Width = 1.3 };
        var config = Build(s);
        Assert.DoesNotContain("Filter", config);
        Assert.Contains("Copy:", config);
        var expected = s.BypassGainDb(SampleRate);
        Assert.InRange(expected, -6, -2);
        // No curve, just the EQ's loudness change at every frequency (for a centred sound, L and R in phase).
        foreach (var f in new[] { 50.0, 1000, 10_000 })
        {
            var t = Interpret(config, f);
            Assert.True(Math.Abs(Db(t.LL + t.LR) - expected) < 0.006, $"{f} Hz: {Db(t.LL + t.LR)} dB, want {expected}");
        }

        // A bypass that would have to boost plays at the original level.
        s.AutoPreamp = false;
        s.PreampDb = 6;
        Assert.DoesNotContain("Preamp", Build(s));
    }

    // MARK: - Listen as

    [Fact]
    public void SimulationFollowsTheEq()
    {
        var s = DspSettings.Flat;
        s.Bands = [new EqBand(FilterType.LowShelf, 105, 6, 0.7), new EqBand(FilterType.Peak, 3000, -2, 2)];
        var sim = SpeakerProfile.Tv.Simulation();
        var config = Build(s, simulation: sim);
        Assert.Contains("# Listen as TV speakers", config);
        // After the EQ's filters, with numbers of their own.
        Assert.True(config.IndexOf("# Listen as", StringComparison.Ordinal) > config.IndexOf("Filter 2:", StringComparison.Ordinal));
        Assert.Contains($"Filter {2 + sim.Bands.Count}:", config);

        var frequencies = ResponseCurve.LogFrequencies(60);
        var eq = ResponseCurve.ResponseDb(s.ActiveBands, frequencies, SampleRate);
        var device = ResponseCurve.ResponseDb(sim.Bands, frequencies, SampleRate);
        var gain = s.EffectivePreampDb(SampleRate) + sim.MakeupGainDb(s, SampleRate);
        for (var i = 0; i < frequencies.Length; i++)
        {
            // A centred sound: L and R in phase, so the TV's narrower width leaves it as it is.
            var t = Interpret(config, frequencies[i]);
            var centred = Db(t.LL + t.LR);
            Assert.True(Math.Abs(centred - (eq[i] + device[i] + gain)) < 0.03, $"{frequencies[i]} Hz: {centred} dB, want {eq[i] + device[i] + gain}");
        }
    }

    [Fact]
    public void SimulationIsLevelMatchedWhereTheHeadroomAllows()
    {
        // A correction's preamp leaves room: the make-up gain is the full level match.
        var corrected = DspSettings.Flat;
        corrected.Bands = [new EqBand(FilterType.LowShelf, 105, 6, 0.7)];
        var phone = SpeakerProfile.Phone.Simulation();
        Assert.Equal(phone.MakeupGainDb(SampleRate), phone.MakeupGainDb(corrected, SampleRate), 9);

        // Without a correction there is no limiter to catch the phone's presence peak: no frequency
        // may come out louder than the input.
        var config = Build(DspSettings.Flat, simulation: phone);
        foreach (var f in ResponseCurve.LogFrequencies(120))
        {
            var t = Interpret(config, f);
            Assert.True(Db(t.LL + t.LR) < 0.02, $"{f} Hz: {Db(t.LL + t.LR)} dB");
        }
    }

    [Theory]
    [InlineData(SpeakerProfile.Phone, 1.0, false)]
    [InlineData(SpeakerProfile.Laptop, 1.0, false)]
    [InlineData(SpeakerProfile.Laptop, 1.5, true)]
    [InlineData(SpeakerProfile.Car, 0.5, false)]
    public void SimulationNarrowsTheStereoLikeTheDevice(SpeakerProfile profile, double width, bool swap)
    {
        var s = DspSettings.Flat;
        s.Effects = new EffectsSettings { Width = width, SwapChannels = swap };
        var sim = profile.Simulation();
        var t = Interpret(Build(s, simulation: sim), 1000);

        // The kernel: make-up gain, the device's filters and width, then swap and the user's width.
        static (double L, double R) Width(double l, double r, double w)
        {
            var m = 0.5 * (l + r);
            var side = 0.5 * (l - r) * w;
            return (m + side, m - side);
        }
        var makeup = Math.Pow(10, sim.MakeupGainDb(s, SampleRate) / 20);
        (double L, double R) Kernel(double l, double r)
        {
            (l, r) = Width(l, r, sim.Width);
            if (swap) (l, r) = (r, l);
            (l, r) = Width(l, r, width);
            return (l * makeup, r * makeup);
        }
        var fromL = Kernel(1, 0);
        var fromR = Kernel(0, 1);
        // The filters act on both channels alike: a centred sound passes every width, so it shows their response.
        var h = (t.LL + t.LR) / (fromL.L + fromR.L);
        Assert.InRange(Db(h) - ResponseCurve.ResponseDb(sim.Bands, [1000.0], SampleRate)[0], -0.01, 0.01);
        Assert.InRange((t.LL / h).Real - fromL.L, -1e-4, 1e-4);
        Assert.InRange((t.RL / h).Real - fromL.R, -1e-4, 1e-4);
        Assert.InRange((t.LR / h).Real - fromR.L, -1e-4, 1e-4);
        Assert.InRange((t.RR / h).Real - fromR.R, -1e-4, 1e-4);
        if (sim.Width == 0) Assert.True((t.LL - t.RL).Magnitude < 1e-9);
    }

    [Fact]
    public void SimulationPlaysWhileTheEqIsBypassed()
    {
        var s = DspSettings.Flat;
        s.EqEnabled = false;
        s.Bands = [new EqBand(FilterType.LowShelf, 105, 6, 0.7)];
        var sim = SpeakerProfile.BluetoothSpeaker.Simulation();
        var config = Build(s, simulation: sim);
        var expected = s.BypassGainDb(SampleRate) + sim.MakeupGainDb(s, SampleRate)
                       + ResponseCurve.ResponseDb(sim.Bands, [1000.0], SampleRate)[0];
        var t = Interpret(config, 1000);
        Assert.True(Math.Abs(Db(t.LL + t.LR) - expected) < 0.02);
        Assert.Contains("# Listen as", Build(s, simulation: sim, levelMatchedBypass: false));
    }

    [Fact]
    public void SimulationOnAMonoDeviceHasNoStereoCommands()
    {
        var config = Build(DspSettings.Flat, channels: 1, simulation: SpeakerProfile.Laptop.Simulation());
        Assert.Contains("# Listen as", config);
        Assert.Contains("HPQ Fc 280 Hz", config);
        Assert.DoesNotContain("Copy:", config);
    }

    [Fact]
    public void NoSimulationNoTrace()
    {
        var s = DspSettings.Flat;
        s.Bands = [new EqBand(FilterType.Peak, 1000, 3, 1)];
        Assert.DoesNotContain("Listen as", Build(s));
        Assert.Equal(Build(s), Build(s, simulation: null));
    }

    [Fact]
    public void NumbersIgnoreTheUserLocale()
    {
        var s = DspSettings.Flat;
        s.Bands = [new EqBand(FilterType.Peak, 1234.5, -3.25, 1.414)];
        s.Effects = new EffectsSettings { Width = 1.3 };
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var config = Build(s);
            Assert.Contains("Fc 1234.5 Hz Gain -3.25 dB Q 1.414", config);
            Assert.DoesNotContain(",", config.Replace("Edit the EQ in Tapline;", ""));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // MARK: - Linear phase

    /// <summary>The FIR files Tapline writes for <paramref name="s"/>, and their samples as Equalizer APO reads them.</summary>
    private static (ApoConfig.FirFiles Reference, Dictionary<string, float[]> Samples) FirFiles(DspSettings s, int channels = 2)
    {
        var (files, reference) = LinearPhaseFiles.Build(LinearPhaseFiles.Spec.For(s, channels, SampleRate));
        var samples = files.ToDictionary(f => LinearPhaseFiles.Folder + @"\" + f.Name, f =>
        {
            var wav = WavFile.Parse(f.Data);
            Assert.Equal(SampleRate, wav.SampleRate);
            Assert.Single(wav.Channels);
            return wav.Channels[0];
        });
        return (reference, samples);
    }

    private static string BuildLinear(DspSettings s, ApoConfig.FirFiles? fir, int channels = 2, bool levelMatchedBypass = true) =>
        ApoConfig.Build(true, [new ApoConfig.Device("{x}", "Test", channels, SampleRate, s, Fir: fir)], levelMatchedBypass);

    /// <summary>Removes the FIRs' delay: what is left of a linear-phase response is real.</summary>
    private static Complex Undelayed(Complex c, double f, int latency) => c * Complex.Exp(new Complex(0, 2 * Math.PI * f * latency / SampleRate));

    /// <summary>The line after <paramref name="line"/>.</summary>
    private static string After(string config, string line)
    {
        var lines = config.Split('\n').Select(l => l.Trim()).ToList();
        var i = lines.IndexOf(line);
        Assert.True(i >= 0 && i + 1 < lines.Count, line);
        return lines[i + 1];
    }

    [Fact]
    public void LinearPhaseConvolvesEachChannelWithTheCurve()
    {
        var s = DspSettings.Flat;
        s.FilterPhase = FilterPhase.Linear;
        s.Bands = [new EqBand(FilterType.LowShelf, 105, 4, 0.7), new EqBand(FilterType.Peak, 2500, -3, 2),
                   new EqBand(FilterType.Peak, 6000, 4, 3, channels: EqChannels.Right)];
        s.Tone = new ToneSettings(air: 2);
        var (reference, samples) = FirFiles(s);
        Assert.Equal(32_768, reference.Latency);
        Assert.NotEqual(reference.Left, reference.Right);
        var config = BuildLinear(s, reference);
        Assert.Equal("Convolution: " + reference.Left, After(config, "Channel: 1"));
        Assert.Equal("Convolution: " + reference.Right, After(config, "Channel: 2"));
        // The biquads and the preamp are in the FIRs.
        Assert.DoesNotContain("Filter", config);
        Assert.DoesNotContain("Preamp", config);

        var preampDb = s.EffectivePreampDb(SampleRate);
        foreach (var f in new[] { 40.0, 105, 440, 2500, 6000, 12_000 })
        {
            var t = Interpret(config, f, files: samples);
            var left = Undelayed(t.LL, f, reference.Latency);
            var right = Undelayed(t.RR, f, reference.Latency);
            // The magnitude of the biquads...
            var wantLeft = preampDb + ResponseCurve.ResponseDb(s.ActiveBands, [f], SampleRate, EqChannels.Left)[0];
            var wantRight = preampDb + ResponseCurve.ResponseDb(s.ActiveBands, [f], SampleRate, EqChannels.Right)[0];
            Assert.True(Math.Abs(Db(left) - wantLeft) < 0.01, $"{f} Hz left: {Db(left)} dB, want {wantLeft}");
            Assert.True(Math.Abs(Db(right) - wantRight) < 0.01, $"{f} Hz right: {Db(right)} dB, want {wantRight}");
            // ...and no phase shift beyond the delay.
            Assert.True(Math.Abs(left.Imaginary) < 1e-4 * left.Magnitude && left.Real > 0, $"{f} Hz phase {left.Phase}");
            Assert.True(Math.Abs(right.Imaginary) < 1e-4 * right.Magnitude && right.Real > 0, $"{f} Hz phase {right.Phase}");
            Assert.True(t.LR.Magnitude < 1e-9 && t.RL.Magnitude < 1e-9);
        }
    }

    [Fact]
    public void LinearPhaseBypassIsDelayedLikeTheFir()
    {
        var s = DspSettings.Flat;
        s.FilterPhase = FilterPhase.Linear;
        s.Bands = [new EqBand(FilterType.Peak, 1000, 6, 1)];
        var (reference, samples) = FirFiles(s);
        s.EqEnabled = false;
        var config = BuildLinear(s, reference);
        Assert.Contains("Delay: 32768 samples", config);
        Assert.DoesNotContain("Convolution", config);
        // Switching the EQ off keeps the time: the original sound at the bypass gain, exactly as late.
        var gain = Math.Pow(10, s.BypassGainDb(SampleRate) / 20);
        Assert.True(gain < 0.95);
        foreach (var f in new[] { 100.0, 1000, 8000 })
        {
            var t = Undelayed(Interpret(config, f, files: samples).LL, f, reference.Latency);
            Assert.True(Close(t, gain), $"{f} Hz: {t}, want {gain}");
        }
        // Without level matching the delay stays, without a preamp.
        var plain = BuildLinear(s, reference, levelMatchedBypass: false);
        Assert.Contains("Delay: 32768 samples", plain);
        Assert.DoesNotContain("Preamp", plain);
    }

    [Fact]
    public void LinearPhaseKeepsSurroundChannelsInTime()
    {
        var s = DspSettings.Flat;
        s.FilterPhase = FilterPhase.Linear;
        s.Bands = [new EqBand(FilterType.Peak, 1000, 3, 1), new EqBand(FilterType.Peak, 3000, 2, 1, channels: EqChannels.Left)];
        var (reference, samples) = FirFiles(s, channels: 6);
        Assert.NotNull(reference.Others);
        var config = BuildLinear(s, reference, channels: 6);
        Assert.Equal("Convolution: " + reference.Others, After(config, "Channel: 3 4 5 6"));
        Interpret(config, 1000, channels: 6, files: samples);
        // The other channels get the bands for every channel, not the left channel's.
        var expected = s.EffectivePreampDb(SampleRate) + ResponseCurve.ResponseDb([s.Bands[0]], [3000.0], SampleRate)[0];
        var others = Undelayed(Dft(samples[reference.Others!], 3000), 3000, reference.Latency);
        Assert.True(Math.Abs(Db(others) - expected) < 0.01, $"{Db(others)} dB, want {expected}");
        // Files made for a stereo layout are not used on it: the biquads play.
        var stereo = FirFiles(s).Reference;
        Assert.DoesNotContain("Convolution", BuildLinear(s, stereo, channels: 6));
        Assert.Contains("Filter 1:", BuildLinear(s, stereo, channels: 6));
    }

    [Fact]
    public void LinearPhaseOnAMonoDeviceUsesTheLeftCurve()
    {
        var s = DspSettings.Flat;
        s.FilterPhase = FilterPhase.Linear;
        s.Bands = [new EqBand(FilterType.Peak, 1000, 3, 1, channels: EqChannels.Left)];
        var (reference, samples) = FirFiles(s, channels: 1);
        var config = BuildLinear(s, reference, channels: 1);
        Assert.Contains("Convolution: " + reference.Left, config);
        Assert.DoesNotContain("Channel: 1", config);
        Assert.DoesNotContain("Copy:", config);
        var expected = s.EffectivePreampDb(SampleRate) + 3;
        Assert.True(Math.Abs(Db(Dft(samples[reference.Left], 1000)) - expected) < 0.01);
    }

    [Fact]
    public void LinearPhasePlaysTheBiquadsUntilItsFilesAreThere()
    {
        var s = DspSettings.Flat;
        s.Bands = [new EqBand(FilterType.Peak, 1000, 3, 1)];
        var minimum = Build(s);
        s.FilterPhase = FilterPhase.Linear;
        Assert.Equal(minimum, BuildLinear(s, null));
        // And minimum phase ignores files.
        var (reference, _) = FirFiles(s);
        s.FilterPhase = FilterPhase.Minimum;
        Assert.Equal(minimum, BuildLinear(s, reference));
    }
}
