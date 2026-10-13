using System.Globalization;
using System.Text;

namespace Tapline.Core;

/// Turns Tapline's per-device settings into an Equalizer APO configuration.
///
/// Equalizer APO uses the same RBJ biquads as Tapline's graph, so what the graph shows is what
/// plays. Channels are addressed by number ("1" = left, "2" = right), which works whatever channel
/// names the driver reports. Effects follow the order of the macOS kernel: EQ, swap/width,
/// crossfeed or virtual speakers, balance, output gain. There is no limiter in Equalizer APO; the
/// automatic preamp keeps the EQ from clipping. With <c>levelMatchedBypass</c>, a device whose EQ is off gets a
/// preamp equal to the EQ's loudness change instead (never a boost), so switching the EQ off
/// compares sound rather than level. A "Listen as" simulation follows the EQ, as on macOS: its
/// make-up gain and filters, and its stereo width folded into the width matrix.
///
/// A multichannel speaker output (5.1 … 7.1.4, see <see cref="ProcessesPerSpeaker"/>) gets the macOS
/// kernel's multichannel processing instead, channel by channel: the EQ on every speaker (or the
/// front pair only), without its boosts above 120 Hz on LFE channels; swap, width and balance on
/// the front pair; no headphone effects (crossfeed, Virtual Speakers, "Listen as"); the output gain
/// on every speaker; then each speaker's room correction, trim and delay. Channels without a known
/// speaker pass through untouched.
public static class ApoConfig
{
    /// First line of every configuration Tapline writes, so it can tell its own from the user's.
    public const string HeaderLine = "# Managed by Tapline.";
    public const string BackupName = "config-before-tapline.txt";

    /// <summary>The four mono impulse responses of Virtual Speakers, relative to the configuration
    /// file (see <see cref="VirtualSpeakerFiles"/>), at the device's sample rate.</summary>
    public sealed record SpeakerFiles(string FrontLeftToLeft, string FrontLeftToRight, string FrontRightToLeft, string FrontRightToRight);

    /// <summary><paramref name="Speakers"/>: the files for Virtual Speakers when the device has
    /// them on and they could be written; without them the effect is left out.
    /// <paramref name="Simulation"/>: the "Listen as" device playing on this output, if any.
    /// <paramref name="Layout"/>: the output's speakers, one position per channel (null: unknown).</summary>
    public sealed record Device(string Guid, string Name, int Channels, double SampleRate, DspSettings Settings, SpeakerFiles? Speakers = null,
                                DeviceSimulation? Simulation = null, SpeakerLayout? Layout = null);

    /// <summary>Whether an output with <paramref name="channels"/> and <paramref name="layout"/> gets the
    /// per-speaker processing: more than two channels, a surround layout (more than a front pair), and
    /// one speaker position for every channel. Anything else is processed as a stereo pair.</summary>
    public static bool ProcessesPerSpeaker(int channels, SpeakerLayout? layout) =>
        channels > 2 && layout is { IsSurround: true } && layout.ChannelCount == channels;

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
        if (ProcessesPerSpeaker(d.Channels, d.Layout))
        {
            AppendSpeakers(sb, d, d.Layout!, levelMatchedBypass);
            return;
        }
        var s = d.Settings;
        var stereo = d.Channels >= 2;
        sb.AppendLine();
        sb.AppendLine($"# {d.Name.Replace('\n', ' ')}");
        sb.AppendLine($"Device: {d.Guid}");
        sb.AppendLine("Channel: all");

        var number = 1;
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

        // "Listen as": on top of the EQ, bypassed or not. No limiter follows, so the make-up gain
        // keeps the headroom of the EQ (see DeviceSimulation.MakeupGainDb).
        var simulation = d.Simulation;
        if (simulation != null)
        {
            sb.AppendLine($"# Listen as {simulation.Name.Replace('\n', ' ')}: a temporary simulation, not part of the EQ");
            var makeup = simulation.MakeupGainDb(s, d.SampleRate, levelMatchedBypass);
            if (Math.Abs(makeup) >= 0.005) sb.AppendLine($"Preamp: {N(makeup, 2)} dB");
            foreach (var b in simulation.PlayedBands.Where(b => !b.Type.UsesGain() || Math.Abs(b.Gain) >= 0.005))
                sb.AppendLine(Filter(number++, b));
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

        // Swap, then mid/side width (0 = mono), as one matrix. A simulated device's width comes
        // first; two mid/side widths in a row make one of their product.
        var w = (fx.Mono ? 0 : fx.Width.Clamp(EffectsSettings.WidthMin, EffectsSettings.WidthMax)) * (simulation?.Width ?? 1);
        double a = 0.5 + 0.5 * w, c = 0.5 - 0.5 * w;
        var (m11, m12, m21, m22) = fx.SwapChannels ? (c, a, a, c) : (a, c, c, a);
        var balance = fx.Balance.Clamp(EffectsSettings.BalanceMin, EffectsSettings.BalanceMax);
        var gainL = balance > 0 ? 1 - balance : 1;
        var gainR = balance < 0 ? 1 + balance : 1;
        var others = Enumerable.Range(3, Math.Max(0, d.Channels - 2)).ToList();

        if (fx.VirtualSpeakersEnabled && d.Speakers is { } speakers)
        {
            // Equalizer APO's own convolver, routed the way HeSuVi does it: the left and right
            // channels (after swap and width) go to four virtual channels, one per speaker-to-ear
            // path; each is convolved with its mono impulse response (the loudness-matching gain
            // is in the files), and each ear sums its two paths.
            var left = Sum((m11, "1"), (m12, "2"));
            var right = Sum((m21, "1"), (m22, "2"));
            sb.AppendLine($"Copy: VLL={left} VLR={left} VRL={right} VRR={right}");
            foreach (var (channel, file) in new[] { ("VLL", speakers.FrontLeftToLeft), ("VLR", speakers.FrontLeftToRight),
                                                    ("VRL", speakers.FrontRightToLeft), ("VRR", speakers.FrontRightToRight) })
            {
                sb.AppendLine($"Channel: {channel}");
                sb.AppendLine($"Convolution: {file}");
            }
            sb.AppendLine("Channel: all");
            sb.AppendLine("Copy: " + string.Join(" ",
                new[] { $"1={Sum((gainL * outputGain, "VLL"), (gainL * outputGain, "VRL"))}",
                        $"2={Sum((gainR * outputGain, "VLR"), (gainR * outputGain, "VRR"))}" }
                .Concat(others.Select(o => $"{o}={Sum((outputGain, o.ToString(Inv)))}"))));
            return;
        }

        // Virtual Speakers take crossfeed's place; the UI never switches on both.
        if (fx.CrossfeedEnabled && !fx.VirtualSpeakersEnabled && fx.Crossfeed > 0.001)
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

    /// <summary>A multichannel speaker output, following the macOS kernel's multichannel path.</summary>
    private static void AppendSpeakers(StringBuilder sb, Device d, SpeakerLayout layout, bool levelMatchedBypass)
    {
        var s = d.Settings;
        var positions = layout.Positions;
        int count = positions.Count;
        sb.AppendLine();
        sb.AppendLine($"# {d.Name.Replace('\n', ' ')}");
        sb.AppendLine($"Device: {d.Guid}");
        sb.AppendLine("Channel: all");
        sb.AppendLine($"# {layout.Name} speakers: "
                      + string.Join(", ", Enumerable.Range(0, count).Select(ch => $"{ch + 1} {positions[ch].Abbreviation()}")));

        // The front pair takes the stereo effects; without one, the first two channels (as Windows plays stereo).
        int left = layout.ChannelOf(SpeakerPosition.FrontLeft) ?? -1, right = layout.ChannelOf(SpeakerPosition.FrontRight) ?? -1;
        if (left < 0 || right < 0) (left, right) = (0, 1);
        var speakers = Enumerable.Range(0, count).Where(ch => positions[ch] != SpeakerPosition.Unknown || ch == left || ch == right).ToList();
        bool IsLfeChannel(int ch) => positions[ch].IsLfe() && ch != left && ch != right;

        // Selects channels (numbered from 1): "all" when that is every channel of the output.
        void SelectChannels(IReadOnlyCollection<int> channels) =>
            sb.AppendLine(channels.Distinct().Count() == count
                ? "Channel: all"
                : "Channel: " + string.Join(" ", channels.Distinct().Order().Select(ch => (ch + 1).ToString(Inv))));

        // 1. The EQ (or, bypassed, its level-matched gain): on every speaker or on the front pair.
        //    Left / right bands stay on the front left / right; LFE channels leave out boosts above 120 Hz.
        var number = 1;
        List<int> eqChannels = s.Speakers.EqOnAllSpeakers ? speakers : [left, right];
        if (s.EqEnabled)
        {
            var preamp = s.EffectivePreampDb(d.SampleRate);
            var bands = s.Bands.Take(DspSettings.MaxBands).Where(b => b.IsEnabled).Select(b => b.Clamped())
                .Concat(s.Tone.Bands.Where(b => b.IsEnabled).Select(b => b.Clamped()))
                .Where(b => !b.Type.UsesGain() || Math.Abs(b.Gain) >= 0.005)
                .ToList();
            var shared = bands.Where(b => b.Channels == EqChannels.Both).ToList();
            var everywhere = shared.Where(b => !SpeakerSetup.SkipsOnLfe(b)).ToList();
            var notOnLfe = shared.Where(SpeakerSetup.SkipsOnLfe).ToList();
            if (Math.Abs(preamp) >= 0.005 || everywhere.Count > 0)
            {
                SelectChannels(eqChannels);
                if (Math.Abs(preamp) >= 0.005) sb.AppendLine($"Preamp: {N(preamp, 2)} dB");
                foreach (var b in everywhere) sb.AppendLine(Filter(number++, b));
            }
            if (notOnLfe.Count > 0)
            {
                var mains = eqChannels.Where(ch => !IsLfeChannel(ch)).ToList();
                if (mains.Count < eqChannels.Count) sb.AppendLine("# Not on the LFE: boosts above 120 Hz");
                SelectChannels(mains);
                foreach (var b in notOnLfe) sb.AppendLine(Filter(number++, b));
            }
            foreach (var (side, channel) in new[] { (EqChannels.Left, left), (EqChannels.Right, right) })
            {
                var only = bands.Where(b => b.Channels == side).ToList();
                if (only.Count == 0) continue;
                SelectChannels([channel]);
                foreach (var b in only) sb.AppendLine(Filter(number++, b));
            }
        }
        else if (levelMatchedBypass)
        {
            var gain = s.BypassGainDb(d.SampleRate);
            if (Math.Abs(gain) >= 0.005)
            {
                SelectChannels(eqChannels);
                sb.AppendLine($"Preamp: {N(gain, 2)} dB");
            }
        }

        // 2. Swap, width (0 = mono) and balance on the front pair, as one matrix. The headphone effects
        //    (crossfeed, Virtual Speakers, "Listen as") stay off on speakers.
        var fx = s.Effects;
        var w = fx.Mono ? 0 : fx.Width.Clamp(EffectsSettings.WidthMin, EffectsSettings.WidthMax);
        double a = 0.5 + 0.5 * w, c = 0.5 - 0.5 * w;
        var (m11, m12, m21, m22) = fx.SwapChannels ? (c, a, a, c) : (a, c, c, a);
        var balance = fx.Balance.Clamp(EffectsSettings.BalanceMin, EffectsSettings.BalanceMax);
        var gainL = balance > 0 ? 1 - balance : 1;
        var gainR = balance < 0 ? 1 + balance : 1;
        var (l1, l2, r1, r2) = (gainL * m11, gainL * m12, gainR * m21, gainR * m22);
        if (!(Near(l1, 1) && Near(l2, 0) && Near(r1, 0) && Near(r2, 1)))
        {
            string l = (left + 1).ToString(Inv), r = (right + 1).ToString(Inv);
            sb.AppendLine("Channel: all");
            sb.AppendLine($"Copy: {l}={Sum((l1, l), (l2, r))} {r}={Sum((r1, l), (r2, r))}");
        }

        // 3. The output gain, on every speaker.
        var outputGainDb = fx.OutputGainDb.Clamp(EffectsSettings.OutputGainMin, EffectsSettings.OutputGainMax);
        if (Math.Abs(outputGainDb) >= 0.005)
        {
            SelectChannels(speakers);
            sb.AppendLine($"Preamp: {N(outputGainDb, 2)} dB");
        }

        // 4. Each speaker's room correction, last: the trim plus the headroom all speakers share (so they
        //    stay level-matched), the filters and the delay. Only on the layout it was measured on.
        var corrections = s.Speakers.ActiveCorrections(layout);
        if (corrections.Count > 0)
        {
            var headroom = SpeakerSetup.HeadroomDb(corrections, d.SampleRate);
            var byChannel = new SortedDictionary<int, SpeakerCorrection>();
            foreach (var correction in corrections)
                if (speakers.Contains(correction.Channel)) byChannel[correction.Channel] = correction;
            sb.AppendLine($"# Room correction for the {layout.Name} speakers");
            var plain = speakers.Where(ch => !byChannel.ContainsKey(ch)).ToList();
            if (plain.Count > 0 && Math.Abs(headroom) >= 0.005)
            {
                SelectChannels(plain);
                sb.AppendLine($"Preamp: {N(headroom, 2)} dB");
            }
            foreach (var (channel, correction) in byChannel)
            {
                sb.AppendLine($"# {positions[channel].Abbreviation()}");
                SelectChannels([channel]);
                var trim = double.IsNaN(correction.TrimDb) ? 0 : correction.TrimDb.Clamp(SpeakerCorrection.TrimRange);
                if (Math.Abs(trim + headroom) >= 0.005) sb.AppendLine($"Preamp: {N(trim + headroom, 2)} dB");
                foreach (var b in correction.Bands.Where(b => b.IsEnabled).Take(SpeakerCorrection.MaxBands).Select(SpeakerSetup.Mono)
                             .Where(b => !b.Type.UsesGain() || Math.Abs(b.Gain) >= 0.005))
                    sb.AppendLine(Filter(number++, b));
                var delay = double.IsNaN(correction.DelayMs) ? 0 : correction.DelayMs.Clamp(SpeakerCorrection.DelayRange);
                if (delay >= 0.0005) sb.AppendLine($"Delay: {N(delay, 3)} ms");
            }
        }
        sb.AppendLine("Channel: all");
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
