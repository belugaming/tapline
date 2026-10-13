using System.Globalization;
using System.Text;

namespace Tapline.Core;

/// Turns Tapline's per-device settings into an Equalizer APO configuration.
///
/// Equalizer APO uses the same RBJ biquads as Tapline's graph, so what the graph shows is what
/// plays. Channels are addressed by number ("1" = left, "2" = right), which works whatever channel
/// names the driver reports. Effects follow the order of the macOS kernel: EQ, swap/width,
/// crossfeed, balance, output gain. There is no limiter in Equalizer APO; the automatic preamp
/// keeps the EQ from clipping. With <c>levelMatchedBypass</c>, a device whose EQ is off gets a
/// preamp equal to the EQ's loudness change instead (never a boost), so switching the EQ off
/// compares sound rather than level.
public static class ApoConfig
{
    /// First line of every configuration Tapline writes, so it can tell its own from the user's.
    public const string HeaderLine = "# Managed by Tapline.";
    public const string BackupName = "config-before-tapline.txt";

    public sealed record Device(string Guid, string Name, int Channels, double SampleRate, DspSettings Settings);

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Build(bool processing, IEnumerable<Device> devices, bool levelMatchedBypass = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine(HeaderLine + " Edit the EQ in Tapline; changes made here are overwritten.");
        sb.AppendLine($"# The configuration Tapline replaced is kept as {BackupName}.");
        if (!processing)
        {
            sb.AppendLine("# Processing is off.");
            return sb.ToString();
        }
        foreach (var device in devices) AppendDevice(sb, device, levelMatchedBypass);
        return sb.ToString();
    }

    private static void AppendDevice(StringBuilder sb, Device d, bool levelMatchedBypass)
    {
        var s = d.Settings;
        var stereo = d.Channels >= 2;
        sb.AppendLine();
        sb.AppendLine($"# {d.Name.Replace('\n', ' ')}");
        sb.AppendLine($"Device: {d.Guid}");
        sb.AppendLine("Channel: all");

        if (s.EqEnabled)
        {
            var preamp = s.EffectivePreampDb(d.SampleRate);
            if (Math.Abs(preamp) >= 0.005) sb.AppendLine($"Preamp: {N(preamp, 2)} dB");
            // Bells and shelves at 0 dB do nothing: leave them out.
            var bands = s.Bands.Take(DspSettings.MaxBands).Where(b => b.IsEnabled).Select(b => b.Clamped())
                .Concat(s.Tone.Bands.Where(b => b.IsEnabled).Select(b => b.Clamped()))
                .Where(b => !b.Type.UsesGain() || Math.Abs(b.Gain) >= 0.005)
                .ToList();
            var both = bands.Where(b => b.Channels == EqChannels.Both || (!stereo && b.Channels == EqChannels.Left)).ToList();
            var number = 1;
            foreach (var b in both) sb.AppendLine(Filter(number++, b));
            if (stereo)
            {
                foreach (var (channel, label) in new[] { (EqChannels.Left, "1"), (EqChannels.Right, "2") })
                {
                    var only = bands.Where(b => b.Channels == channel).ToList();
                    if (only.Count == 0) continue;
                    sb.AppendLine($"Channel: {label}");
                    foreach (var b in only) sb.AppendLine(Filter(number++, b));
                }
                sb.AppendLine("Channel: all");
            }
        }
        else if (levelMatchedBypass)
        {
            var gain = s.BypassGainDb(d.SampleRate);
            if (Math.Abs(gain) >= 0.005) sb.AppendLine($"Preamp: {N(gain, 2)} dB");
        }

        var fx = s.Effects;
        // Clamp (NaN-safe) every value that is written, so the configuration never holds "NaN".
        var outputGainDb = fx.OutputGainDb.Clamp(EffectsSettings.OutputGainMin, EffectsSettings.OutputGainMax);
        var outputGain = Db.ToLinear(outputGainDb);
        if (!stereo)
        {
            if (Math.Abs(outputGainDb) >= 0.005) sb.AppendLine($"Preamp: {N(outputGainDb, 2)} dB");
            return;
        }

        // Swap, then mid/side width (0 = mono), as one matrix.
        var w = fx.Mono ? 0 : fx.Width.Clamp(EffectsSettings.WidthMin, EffectsSettings.WidthMax);
        double a = 0.5 + 0.5 * w, c = 0.5 - 0.5 * w;
        var (m11, m12, m21, m22) = fx.SwapChannels ? (c, a, a, c) : (a, c, c, a);
        var balance = fx.Balance.Clamp(EffectsSettings.BalanceMin, EffectsSettings.BalanceMax);
        var gainL = balance > 0 ? 1 - balance : 1;
        var gainR = balance < 0 ? 1 + balance : 1;
        var others = Enumerable.Range(3, Math.Max(0, d.Channels - 2)).ToList();

        if (fx.CrossfeedEnabled && fx.Crossfeed > 0.001)
        {
            // ~300 µs interaural delay and a soft 700 Hz low-pass on the cross path, in two virtual channels.
            var kx = 0.45 * fx.Crossfeed.Clamp(EffectsSettings.CrossfeedMin, EffectsSettings.CrossfeedMax);
            var norm = 1 / (1 + 0.7 * kx);
            sb.AppendLine("Copy: " + string.Join(" ",
                new[] { $"1={Sum((m11, "1"), (m12, "2"))}", $"2={Sum((m21, "1"), (m22, "2"))}",
                        $"XL={Sum((m11, "1"), (m12, "2"))}", $"XR={Sum((m21, "1"), (m22, "2"))}" }
                .Concat(others.Select(o => $"{o}={Sum((1, o.ToString(Inv)))}"))));
            sb.AppendLine("Channel: XL XR");
            sb.AppendLine("Filter: ON LPQ Fc 700 Hz Q 0.5");
            sb.AppendLine("Delay: 0.3 ms");
            sb.AppendLine("Channel: all");
            sb.AppendLine("Copy: " + string.Join(" ",
                new[] { $"1={Sum((gainL * outputGain * norm, "1"), (gainL * outputGain * norm * kx, "XR"))}",
                        $"2={Sum((gainR * outputGain * norm, "2"), (gainR * outputGain * norm * kx, "XL"))}" }
                .Concat(others.Select(o => $"{o}={Sum((outputGain, o.ToString(Inv)))}"))));
            return;
        }

        var (l1, l2, r1, r2) = (gainL * outputGain * m11, gainL * outputGain * m12, gainR * outputGain * m21, gainR * outputGain * m22);
        var identity = Near(l1, 1) && Near(l2, 0) && Near(r1, 0) && Near(r2, 1) && Near(outputGain, 1);
        if (identity) return;
        sb.AppendLine("Copy: " + string.Join(" ",
            new[] { $"1={Sum((l1, "1"), (l2, "2"))}", $"2={Sum((r1, "1"), (r2, "2"))}" }
            .Concat(others.Select(o => $"{o}={Sum((outputGain, o.ToString(Inv)))}"))));
    }

    /// One Filter line in the REW/AutoEQ syntax Equalizer APO reads.
    public static string Filter(int number, EqBand b) => b.Type switch
    {
        FilterType.Peak => $"Filter {number}: ON PK Fc {N(b.Frequency, 2)} Hz Gain {N(b.Gain, 2)} dB Q {N(b.Q, 3)}",
        FilterType.LowShelf => $"Filter {number}: ON LSC Fc {N(b.Frequency, 2)} Hz Gain {N(b.Gain, 2)} dB Q {N(b.Q, 3)}",
        FilterType.HighShelf => $"Filter {number}: ON HSC Fc {N(b.Frequency, 2)} Hz Gain {N(b.Gain, 2)} dB Q {N(b.Q, 3)}",
        FilterType.LowPass => $"Filter {number}: ON LPQ Fc {N(b.Frequency, 2)} Hz Q {N(b.Q, 3)}",
        FilterType.HighPass => $"Filter {number}: ON HPQ Fc {N(b.Frequency, 2)} Hz Q {N(b.Q, 3)}",
        FilterType.Notch => $"Filter {number}: ON NO Fc {N(b.Frequency, 2)} Hz Q {N(b.Q, 3)}",
        FilterType.BandPass => $"Filter {number}: ON BP Fc {N(b.Frequency, 2)} Hz Q {N(b.Q, 3)}",
        _ => $"# Filter {number}: unsupported type {b.Type}",
    };

    /// A Copy source expression: "0.75*1+0.25*2". Terms with a zero factor are dropped.
    private static string Sum(params (double Factor, string Channel)[] terms)
    {
        var parts = terms.Where(t => Math.Abs(t.Factor) > 1e-6).Select(t => $"{F(t.Factor)}*{t.Channel}").ToList();
        return parts.Count > 0 ? string.Join("+", parts) : "0.0";
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

    /// A factor with a decimal point and no exponent (Equalizer APO reads integers as channel numbers).
    private static string F(double v)
    {
        var s = v.ToString("0.0#####", Inv);
        return s == "-0.0" ? "0.0" : s;
    }

    private static string N(double v, int decimals) => v.ToString("0." + new string('#', decimals), Inv);
}
