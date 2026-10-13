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

    private static Transfer Interpret(string config, double f, int channels = 2)
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
                case "Delay":
                {
                    Assert.Contains("ms", parameters);
                    var z = Complex.Exp(new Complex(0, -2 * Math.PI * f * Parse(Regex.Match(parameters, @"[-+0-9.]+").Value) / 1000));
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

    private static string Build(DspSettings s, int channels = 2) =>
        ApoConfig.Build(true, [new ApoConfig.Device("{00000000-1111-2222-3333-444444444444}", "Test", channels, SampleRate, s)]);

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
}
