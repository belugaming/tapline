namespace Tapline.Core;

/// <summary>A multichannel measurement in one recording: a sweep on each speaker in turn, then short
/// timing chirps: the reference speaker (front left), every speaker, the reference again.
/// As in <see cref="StereoSweepPlan"/>, the two reference chirps bracket the others, so the clock drift
/// between two audio interfaces (one playing, one recording) cancels. LFE channels get a
/// low chirp (20-200 Hz) instead, which a subwoofer can play.</summary>
public sealed class SpeakerSweepPlan
{
    /// <param name="Channel">The output channel it plays on (0-based).</param>
    public readonly record struct Speaker(int Channel, bool IsLfe = false);

    public double SampleRate { get; }
    /// <summary>In playing order.</summary>
    public IReadOnlyList<Speaker> Speakers { get; }
    /// <summary>Index into <see cref="Speakers"/> of the timing reference.</summary>
    public int Reference { get; }
    public SineSweep Sweep { get; }
    public SineSweep Chirp { get; }
    public SineSweep LowChirp { get; }

    public const double PreRoll = 0.6;
    /// <summary>Between sweeps: longer than the analysis window, so one speaker's room decay stays out of the next.</summary>
    public const double Gap = 1.2;
    public const double ChirpSeconds = 0.25;
    public const double ChirpSpacing = 0.5;
    public const double LowChirpSeconds = 0.5;
    public const double LowChirpSpacing = 1.0;
    public const double Tail = 0.8;
    /// <summary>Shorter than the stereo sweep: a 7.1.4 layout plays twelve of them per position.</summary>
    public const double DefaultSweepSeconds = 6.0;

    /// <param name="endFrequency">The top of the sweeps, the same for the played signal and the analysis
    /// (see <see cref="SineSweep.EndFrequencyFor"/>).</param>
    public SpeakerSweepPlan(double sampleRate, IReadOnlyList<Speaker> speakers, int reference = 0, double sweepSeconds = DefaultSweepSeconds,
                            double endFrequency = 20_000)
    {
        SampleRate = sampleRate;
        Speakers = [.. speakers];
        Reference = reference >= 0 && reference < speakers.Count ? reference : 0;
        Sweep = new SineSweep(sampleRate, sweepSeconds, endFrequency: endFrequency);
        Chirp = new SineSweep(sampleRate, ChirpSeconds, startFrequency: 500, endFrequency: Math.Min(16_000, endFrequency));
        LowChirp = new SineSweep(sampleRate, LowChirpSeconds, startFrequency: 20, endFrequency: 200);
    }

    /// <summary>When speaker <paramref name="i"/>'s sweep starts, seconds.</summary>
    public double SweepStart(int i) => PreRoll + i * (Sweep.Duration + Gap);

    /// <summary>The timing chirps in order: which speaker (index into <see cref="Speakers"/>) and when, seconds.
    /// The reference speaker comes first and last.</summary>
    public IReadOnlyList<(int Speaker, double Start)> ChirpSlots
    {
        get
        {
            if (Speakers.Count == 0) return [];
            double t = SweepStart(Speakers.Count);
            var slots = new List<(int Speaker, double Start)>(Speakers.Count + 2);
            foreach (int i in new[] { Reference }.Concat(Enumerable.Range(0, Speakers.Count)).Append(Reference))
            {
                slots.Add((i, t));
                t += Speakers[i].IsLfe ? LowChirpSpacing : ChirpSpacing;
            }
            return slots;
        }
    }

    public double Duration
    {
        get
        {
            if (Speakers.Count == 0) return PreRoll + Tail;
            var last = ChirpSlots[^1];
            return last.Start + (Speakers[last.Speaker].IsLfe ? LowChirpSeconds : ChirpSeconds) + Tail;
        }
    }

    /// <summary>What happens at <paramref name="seconds"/> into the plan: which speaker plays (index into
    /// <see cref="Speakers"/>), and whether it is a timing chirp. Null before the first sweep and in the tail.</summary>
    public (int Speaker, bool Chirps)? Playing(double seconds)
    {
        if (seconds < PreRoll || Speakers.Count == 0) return null;
        double chirps = SweepStart(Speakers.Count);
        if (seconds < chirps - Gap / 2)
        {
            int i = (int)((seconds - PreRoll + Gap / 2) / (Sweep.Duration + Gap));
            return (Math.Clamp(i, 0, Speakers.Count - 1), false);
        }
        (int Speaker, double Start)? slot = null;
        foreach (var s in ChirpSlots)
            if (s.Start <= seconds + 0.05) slot = s;
        if (slot is not { } found) return null;
        return seconds < Duration - Tail ? (found.Speaker, true) : null;
    }

    /// <summary>One signal per speaker, in the order of <see cref="Speakers"/>, all of the same length.</summary>
    public float[][] Render()
    {
        int count = (int)Swift.Rounded(Duration * SampleRate);
        var output = new float[Speakers.Count][];
        for (int i = 0; i < output.Length; i++) output[i] = new float[count];
        void Place(float[] signal, double seconds, int i)
        {
            int start = (int)Swift.Rounded(seconds * SampleRate);
            for (int j = 0; j < signal.Length && start + j < count; j++) output[i][start + j] = signal[j];
        }
        for (int i = 0; i < Speakers.Count; i++) Place(Sweep.Signal, SweepStart(i), i);
        foreach (var slot in ChirpSlots)
            Place(Speakers[slot.Speaker].IsLfe ? LowChirp.Signal : Chirp.Signal, slot.Start, slot.Speaker);
        return output;
    }

    public sealed class SpeakerResult
    {
        public required int Channel { get; init; }
        public required FrequencyResponse Response { get; init; }
        /// <summary>Arrival after the reference speaker's, seconds: positive when the speaker is farther
        /// away (for a subwoofer this includes its low-pass filter's delay). Null when unclear.</summary>
        public double? Arrival { get; init; }
        /// <summary>Whether it pushes when the reference speaker pulls. Null when unclear, and always
        /// for LFE channels, whose low-passed chirp shows no clear polarity.</summary>
        public bool? OppositePolarity { get; init; }
        public double SnrDb { get; init; }
    }

    public sealed class Analysis
    {
        public required IReadOnlyList<SpeakerResult> Speakers { get; init; }
        public double PeakDb { get; init; }
    }

    /// <summary>Analyses a mono microphone recording at <paramref name="inputRate"/> of <see cref="Render"/> played at
    /// <paramref name="outputRate"/> (null: the same rate). The recording must start before playback.</summary>
    public static Analysis? Analyze(ReadOnlySpan<float> recording, double inputRate, double? outputRate, IReadOnlyList<Speaker> speakers, int reference = 0,
                                    double sweepSeconds = DefaultSweepSeconds, double windowSeconds = 0.5)
    {
        if (speakers.Count == 0) return null;
        double end = SineSweep.EndFrequencyFor(outputRate ?? inputRate, inputRate);
        var plan = new SpeakerSweepPlan(inputRate, speakers, reference, sweepSeconds, end);
        double fs = inputRate;
        int n = plan.Sweep.Signal.Length;
        var slots = plan.ChirpSlots;
        if (recording.Length <= (int)(slots[0].Start * fs)) return null;

        static float[] Slice(ReadOnlySpan<float> x, int from, int to)
        {
            int a = Math.Max(0, Math.Min(from, x.Length)), b = Math.Max(a, Math.Min(to, x.Length));
            return x[a..b].ToArray();
        }

        // Sweeps, each deconvolved on its own. The first one finds the latency (playback,
        // recording, distance); the others are looked for where it puts them.
        int latency = 0;
        var responses = new List<FrequencyResponse?>(speakers.Count);
        for (int i = 0; i < speakers.Count; i++)
        {
            int nominal = (int)(plan.SweepStart(i) * fs);
            bool first = i == 0;
            int begin = Math.Max(0, nominal + latency - (int)((first ? 0.05 : 0.1) * fs));
            int stop = nominal + latency + n + (int)(((first ? 1.0 : 0.1) + windowSeconds + 0.1) * fs);
            var ir = plan.Sweep.ImpulseResponse(Slice(recording, begin, stop));
            int expected = nominal + latency - begin + n - 1;
            int? peak = first
                ? StereoSweepPlan.PeakIndex(ir, expected - (int)(0.05 * fs), expected + (int)(1.0 * fs))
                : StereoSweepPlan.PeakIndex(ir, expected - (int)(0.05 * fs), expected + (int)(0.05 * fs));
            if (peak is not { } p)
            {
                if (first) return null;
                responses.Add(null);
                continue;
            }
            if (first) latency = p - expected;
            int lo = Math.Max(0, p - (int)(0.05 * fs)), hi = Math.Min(ir.Length, p + (int)((windowSeconds + 0.05) * fs));
            responses.Add(hi > lo ? RoomResponse.FromImpulse(ir.AsSpan(lo, hi - lo), fs, windowSeconds) : null);
        }

        // Chirps: arrival times relative to the reference speaker, with sub-sample precision.
        var arrivals = new double?[speakers.Count];
        var polarities = new bool?[speakers.Count];
        int chirpBegin = Math.Max(0, (int)(slots[0].Start * fs) + latency - (int)(0.1 * fs));
        var segment = Slice(recording, chirpBegin, (int)(plan.Duration * fs) + latency + (int)(0.3 * fs));
        var cir = plan.Chirp.ImpulseResponse(segment);
        var lowCir = speakers.Any(s => s.IsLfe) ? plan.LowChirp.ImpulseResponse(segment) : Array.Empty<float>();
        // Where each chirp starts in the recording as heard, in samples, and the sign of its peak.
        (double Position, float? Sign)? Locate(int slot, double around, double reach)
        {
            var s = speakers[slots[slot].Speaker];
            int chirpLength = (s.IsLfe ? plan.LowChirp : plan.Chirp).Signal.Length;
            int expected = (int)(slots[slot].Start * fs) + latency + (int)Swift.Rounded(around) - chirpBegin + chirpLength - 1;
            int windowLo = expected - (int)(reach * fs), windowHi = expected + (int)(reach * fs);
            if (s.IsLfe)
            {
                if (EnvelopePeak(lowCir, windowLo, windowHi, (int)(0.0025 * fs)) is not { } e) return null;
                return (chirpBegin + e - (chirpLength - 1), null);
            }
            if (StereoSweepPlan.PeakIndex(cir, windowLo, windowHi) is not { } p) return null;
            return (chirpBegin + StereoSweepPlan.InterpolatedPeak(cir, p) - (chirpLength - 1),
                    StereoSweepPlan.ClearSign(cir, p, (int)(0.0005 * fs)));
        }
        int lastSlot = slots.Count - 1;
        if (Locate(0, 0, 0.05) is { } firstHit)
        {
            double firstOffset = firstHit.Position - (slots[0].Start * fs + latency);
            if (Locate(lastSlot, firstOffset, 0.02) is { } lastHit)
            {
                double lastOffset = lastHit.Position - (slots[lastSlot].Start * fs + latency);
                // The reference's offset at any time between its two chirps (linear clock drift).
                double Offset(double seconds)
                {
                    double t0 = slots[0].Start, t1 = slots[lastSlot].Start;
                    return firstOffset + (lastOffset - firstOffset) * (seconds - t0) / Math.Max(t1 - t0, 1e-9);
                }
                float? referenceSign = firstHit.Sign != null && firstHit.Sign == lastHit.Sign ? firstHit.Sign : null;
                for (int slot = 1; slot < lastSlot; slot++)
                {
                    int i = slots[slot].Speaker;
                    double expected = Offset(slots[slot].Start);
                    // Up to about 10 m nearer or farther than the reference; subwoofers lag more.
                    if (Locate(slot, expected, speakers[i].IsLfe ? 0.06 : 0.03) is not { } hit) continue;
                    arrivals[i] = (hit.Position - (slots[slot].Start * fs + latency) - expected) / fs;
                    if (!speakers[i].IsLfe && referenceSign is { } a && hit.Sign is { } b) polarities[i] = a != b;
                }
            }
        }

        // Signal to noise: each sweep against the room before the first one.
        double Rms(ReadOnlySpan<float> x, double lo, double hi)
        {
            int a = Math.Max(0, (int)(lo * fs) + latency), b = Math.Min(x.Length, (int)(hi * fs) + latency);
            if (b <= a) return 1e-9;
            double sum = 0;
            for (int k = a; k < b; k++) sum += (double)x[k] * x[k];
            return Math.Sqrt(sum / (b - a));
        }
        double noise = Rms(recording, 0, PreRoll * 0.8);
        var results = new List<SpeakerResult>();
        for (int i = 0; i < speakers.Count; i++)
        {
            if (responses[i] is not { } response) continue;
            double start = plan.SweepStart(i);
            double snr = 20 * Math.Log10(Math.Max(Rms(recording, start, start + sweepSeconds), 1e-9) / Math.Max(noise, 1e-9));
            results.Add(new SpeakerResult
            {
                Channel = speakers[i].Channel,
                Response = response,
                Arrival = arrivals[i],
                OppositePolarity = polarities[i],
                SnrDb = snr,
            });
        }
        if (results.Count == 0) return null;
        float peakSample = 0;
        foreach (float v in recording) peakSample = Math.Max(peakSample, Math.Abs(v));
        return new Analysis { Speakers = results, PeakDb = 20 * Math.Log10(Math.Max(peakSample, 1e-9)) };
    }

    /// <summary>Peak of the RMS envelope (over ±<paramref name="halfWidth"/> samples) inside [<paramref name="rangeLo"/>,
    /// <paramref name="rangeHi"/>), interpolated: the arrival of a band-limited pulse whose largest swing could be
    /// either of two half-waves.</summary>
    internal static double? EnvelopePeak(float[] x, int rangeLo, int rangeHi, int halfWidth)
    {
        int lo = Math.Max(0, rangeLo), hi = Math.Min(x.Length, rangeHi);
        if (hi <= lo || halfWidth <= 0) return null;
        // Running sum of squares.
        var prefix = new double[x.Length + 1];
        for (int i = 0; i < x.Length; i++) prefix[i + 1] = prefix[i] + (double)x[i] * x[i];
        double Energy(int i)
        {
            int a = Math.Max(0, i - halfWidth), b = Math.Min(x.Length, i + halfWidth + 1);
            return (prefix[b] - prefix[a]) / Math.Max(b - a, 1);
        }
        int best = lo;
        double bestValue = Energy(lo);
        for (int i = lo; i < hi; i++)
        {
            double e = Energy(i);
            if (e > bestValue)
            {
                best = i;
                bestValue = e;
            }
        }
        if (!(bestValue > 0)) return null;
        if (best <= 0 || best >= x.Length - 1) return best;
        double ea = Energy(best - 1), eb = bestValue, ec = Energy(best + 1);
        double d = ea - 2 * eb + ec;
        return Math.Abs(d) > 1e-18 ? best + 0.5 * (ea - ec) / d : best;
    }
}

/// <summary>Level trims and delays that make every speaker of a multichannel layout equally loud and
/// arrive at the same moment at the listening position.</summary>
public static class SpeakerAlignment
{
    /// <param name="LevelDb"><see cref="Level"/> of its measured response, dB.</param>
    /// <param name="Arrival">Arrival after the reference speaker's, seconds (null: not measured).</param>
    public readonly record struct Speaker(SpeakerPosition Position, double LevelDb, double? Arrival);

    /// <param name="TrimDb">Never a boost: louder speakers are turned down to the quietest.</param>
    public readonly record struct Result(double TrimDb, double DelayMs);

    /// <summary>Equalizer APO's (and the macOS kernel's) limit is far longer; anything beyond this is a
    /// measurement gone wrong rather than a room.</summary>
    public const double MaxDelayMs = 50;
    public const double MaxTrimDb = 12;
    public const double SpeedOfSound = 343;

    /// <summary>A speaker's level in its own band, dB: the third-octave smoothed mean from 500 Hz to
    /// 2 kHz, or from 30 to 80 Hz for an LFE channel.</summary>
    public static double Level(FrequencyResponse response, SpeakerPosition position)
    {
        var g = EqFitter.GridValues;
        var smooth = Smoothing.FractionalOctave(response.Resampled(g), g, _ => 1.0 / 3);
        var (lo, hi) = position.IsLfe() ? (30.0, 80.0) : (500.0, 2000.0);
        double sum = 0;
        int count = 0;
        for (int i = 0; i < g.Length; i++)
        {
            if (g[i] < lo || g[i] > hi) continue;
            sum += smooth[i];
            count++;
        }
        return sum / Math.Max(count, 1);
    }

    /// <summary>Trims that turn every speaker down to the quietest one, and delays that hold back the
    /// nearer speakers until the farthest one arrives. LFE channels are delayed with the rest
    /// but keep their level: how loud the subwoofer plays is a matter of taste and of the
    /// receiver's +10 dB LFE gain, set at the subwoofer or in the receiver.</summary>
    public static Result[] Align(IReadOnlyList<Speaker> speakers)
    {
        var mains = speakers.Where(s => !s.Position.IsLfe() && double.IsFinite(s.LevelDb)).ToList();
        double? quietest = mains.Count > 0 ? mains.Min(s => s.LevelDb) : null;
        var arrivals = speakers.Where(s => s.Arrival is { } a && double.IsFinite(a)).Select(s => s.Arrival!.Value).ToList();
        double? latest = arrivals.Count > 0 ? arrivals.Max() : null;
        return speakers.Select(s =>
        {
            double trim = 0;
            if (!s.Position.IsLfe() && quietest is { } q && double.IsFinite(s.LevelDb))
                trim = (q - s.LevelDb).Clamp(-MaxTrimDb, 0);
            double delay = 0;
            if (latest is { } l && s.Arrival is { } arrival && double.IsFinite(arrival))
                delay = ((l - arrival) * 1000).Clamp(0, MaxDelayMs);
            return new Result(trim, delay);
        }).ToArray();
    }
}

/// <summary>What a multichannel measurement suggests checking, speaker by speaker. Match on the
/// nested records, e.g. <c>advice.Kind is SpeakerAdviceKind.Polarity { Channel: 1 }</c>.</summary>
public abstract record SpeakerAdviceKind
{
    private SpeakerAdviceKind() { }

    /// <summary>Pushes when the reference speaker pulls: a wiring or polarity-switch problem.</summary>
    public sealed record Polarity(int Channel) : SpeakerAdviceKind;
    /// <summary>No timing chirp found: the speaker may not play, or plays from another channel.</summary>
    public sealed record Silent(int Channel) : SpeakerAdviceKind;
    /// <summary>Much louder or quieter than the others (dB from their median): check its level control.</summary>
    public sealed record Level(int Channel, double Db) : SpeakerAdviceKind;
    /// <summary>Much nearer or farther than the others (metres from their median): worth checking the distance.</summary>
    public sealed record Distance(int Channel, double Meters) : SpeakerAdviceKind;
    /// <summary>The LFE channel's level in its band relative to the other speakers in theirs.</summary>
    public sealed record Subwoofer(int Channel, double Db) : SpeakerAdviceKind;
    public sealed record Noisy(double SnrDb) : SpeakerAdviceKind;
    public sealed record FewPositions(int Count) : SpeakerAdviceKind;
}

public sealed record SpeakerAdvice(SpeakerAdviceKind Kind, RoomAdviceSeverity Severity)
{
    public string Id => Kind switch
    {
        SpeakerAdviceKind.Polarity k => $"polarity-{k.Channel}",
        SpeakerAdviceKind.Silent k => $"silent-{k.Channel}",
        SpeakerAdviceKind.Level k => $"level-{k.Channel}",
        SpeakerAdviceKind.Distance k => $"distance-{k.Channel}",
        SpeakerAdviceKind.Subwoofer k => $"sub-{k.Channel}",
        SpeakerAdviceKind.Noisy => "noisy",
        SpeakerAdviceKind.FewPositions => "positions",
        _ => "unknown",
    };

    public readonly record struct Speaker(int Channel, SpeakerPosition Position, double LevelDb, double? Arrival, bool? OppositePolarity);

    public static List<SpeakerAdvice> Advise(IReadOnlyList<Speaker> speakers, int positions, double worstSnrDb)
    {
        var output = new List<SpeakerAdvice>();
        foreach (var s in speakers.Where(s => s.OppositePolarity == true))
            output.Add(new SpeakerAdvice(new SpeakerAdviceKind.Polarity(s.Channel), RoomAdviceSeverity.Warning));
        foreach (var s in speakers.Where(s => s.Arrival == null))
            output.Add(new SpeakerAdvice(new SpeakerAdviceKind.Silent(s.Channel), RoomAdviceSeverity.Warning));
        if (worstSnrDb < 25)
            output.Add(new SpeakerAdvice(new SpeakerAdviceKind.Noisy(worstSnrDb), RoomAdviceSeverity.Warning));
        var mains = speakers.Where(s => !s.Position.IsLfe()).ToList();
        static double? Median(IEnumerable<double> values)
        {
            var s = values.Order().ToArray();
            if (s.Length == 0) return null;
            return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
        }
        if (Median(mains.Select(s => s.LevelDb)) is { } typicalLevel)
        {
            foreach (var s in mains.Where(s => Math.Abs(s.LevelDb - typicalLevel) >= 6))
                output.Add(new SpeakerAdvice(new SpeakerAdviceKind.Level(s.Channel, s.LevelDb - typicalLevel), RoomAdviceSeverity.Suggestion));
            foreach (var s in speakers.Where(s => s.Position.IsLfe()))
                output.Add(new SpeakerAdvice(new SpeakerAdviceKind.Subwoofer(s.Channel, s.LevelDb - typicalLevel), RoomAdviceSeverity.Info));
        }
        if (Median(mains.Where(s => s.Arrival.HasValue).Select(s => s.Arrival!.Value)) is { } typicalArrival)
        {
            foreach (var s in mains)
            {
                if (s.Arrival is not { } a) continue;
                double meters = (a - typicalArrival) * SpeakerAlignment.SpeedOfSound;
                if (Math.Abs(meters) >= 1.5)
                    output.Add(new SpeakerAdvice(new SpeakerAdviceKind.Distance(s.Channel, meters), RoomAdviceSeverity.Info));
            }
        }
        if (positions < 3)
            output.Add(new SpeakerAdvice(new SpeakerAdviceKind.FewPositions(positions), RoomAdviceSeverity.Info));
        // Most severe first; otherwise in the order above (OrderByDescending is stable).
        return [.. output.OrderByDescending(a => a.Severity)];
    }
}
