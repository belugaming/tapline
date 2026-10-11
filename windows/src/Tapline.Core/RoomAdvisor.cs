namespace Tapline.Core;

public enum RoomAdviceSeverity
{
    Info,
    Suggestion,
    Warning,
}

/// <summary>What a piece of room advice is about. Match on the nested records, e.g.
/// <c>advice.Kind is RoomAdviceKind.Balance { Louder: EqChannels.Right } b</c>.</summary>
public abstract record RoomAdviceKind
{
    private RoomAdviceKind() { }

    /// <summary>One side is louder through the midrange.</summary>
    public sealed record Balance(EqChannels Louder, double Db) : RoomAdviceKind;
    /// <summary>One speaker is farther from the microphone.</summary>
    public sealed record Distance(EqChannels Farther, double Centimeters) : RoomAdviceKind;
    /// <summary>The two speakers push in opposite directions (a wiring or polarity-switch problem).</summary>
    public sealed record Polarity() : RoomAdviceKind;
    /// <summary>Left and right differ a lot in the bass; separate corrections help.</summary>
    public sealed record ChannelMismatch(double Db, double Frequency) : RoomAdviceKind;
    /// <summary>Sub-bass (30-90 Hz) above (+) or below (-) the target, relative to the midrange.</summary>
    public sealed record Subwoofer(double Db) : RoomAdviceKind;
    /// <summary>Broad excess from 100 to 400 Hz, typical of speakers near walls.
    /// <c>SwitchDb</c> is the nearest bass-tilt setting (-2, -4 or -6 dB).</summary>
    public sealed record BassTilt(double Db, double SwitchDb) : RoomAdviceKind;
    /// <summary>A bump around 160 Hz from the desk or console under the speakers.</summary>
    public sealed record Desktop(double Frequency, double Db) : RoomAdviceKind;
    /// <summary>A resonance between parallel surfaces. The EQ takes care of it.</summary>
    public sealed record RoomMode(double Frequency, double Db, double DimensionMeters) : RoomAdviceKind;
    /// <summary>A narrow, deep dip from sound cancelling itself. EQ cannot fill it.</summary>
    public sealed record Cancellation(double Frequency, double DepthDb, double QuarterWaveCentimeters, bool NearCrossover) : RoomAdviceKind;
    /// <summary>Treble trend versus the target. <c>SwitchDb</c> is the nearest treble-tilt setting.</summary>
    public sealed record Treble(double Db, double SwitchDb, bool Calibrated) : RoomAdviceKind;
    public sealed record FewPositions(int Count) : RoomAdviceKind;
    public sealed record Noisy(double SnrDb) : RoomAdviceKind;
}

/// <summary>Something a room measurement suggests fixing at the speakers or the seat,
/// before (or instead of) EQ.</summary>
public sealed record RoomAdvice(RoomAdviceKind Kind, RoomAdviceSeverity Severity)
{
    public string Id => Kind switch
    {
        RoomAdviceKind.Balance => "balance",
        RoomAdviceKind.Distance => "distance",
        RoomAdviceKind.Polarity => "polarity",
        RoomAdviceKind.ChannelMismatch => "mismatch",
        RoomAdviceKind.Subwoofer => "subwoofer",
        RoomAdviceKind.BassTilt => "bassTilt",
        RoomAdviceKind.Desktop => "desktop",
        RoomAdviceKind.RoomMode m => $"mode-{(int)m.Frequency}",
        RoomAdviceKind.Cancellation c => $"dip-{(int)c.Frequency}",
        RoomAdviceKind.Treble => "treble",
        RoomAdviceKind.FewPositions => "positions",
        RoomAdviceKind.Noisy => "noisy",
        _ => "unknown",
    };
}

public static class RoomAdvisor
{
    public sealed class Input
    {
        /// <summary>Average of every measurement.</summary>
        public FrequencyResponse Measurement { get; set; }
        /// <summary>Target including the bass / tilt preferences.</summary>
        public FrequencyResponse Target { get; set; }
        public FrequencyResponse? Left { get; set; }
        public FrequencyResponse? Right { get; set; }
        /// <summary>Right arrival minus left arrival, seconds.</summary>
        public double? ArrivalDifference { get; set; }
        public bool? OppositePolarity { get; set; }
        public int Positions { get; set; }
        public double WorstSnrDb { get; set; }
        public bool Calibrated { get; set; }

        public Input(FrequencyResponse measurement, FrequencyResponse target, int positions, double worstSnrDb, bool calibrated,
                     FrequencyResponse? left = null, FrequencyResponse? right = null,
                     double? arrivalDifference = null, bool? oppositePolarity = null)
        {
            Measurement = measurement;
            Target = target;
            Left = left;
            Right = right;
            ArrivalDifference = arrivalDifference;
            OppositePolarity = oppositePolarity;
            Positions = positions;
            WorstSnrDb = worstSnrDb;
            Calibrated = calibrated;
        }
    }

    internal const double SpeedOfSound = 343.0;

    public static List<RoomAdvice> Advise(Input input)
    {
        var g = EqFitter.GridValues;
        int n = g.Length;
        var advice = new List<RoomAdvice>();

        // Deviation from the target, level-matched on the midrange.
        var m = input.Measurement.Resampled(g);
        var t = input.Target.Resampled(g);
        var d = new double[n];
        for (int i = 0; i < n; i++) d[i] = m[i] - t[i];
        double mid = Mean(d, g, 500, 2000);
        for (int i = 0; i < n; i++) d[i] -= mid;
        var third = Smoothing.FractionalOctave(d, g, _ => 1.0 / 3);
        var fine = Smoothing.FractionalOctave(d, g, _ => 1.0 / 12);
        var octave = Smoothing.FractionalOctave(d, g, _ => 1.0);
        // Height of narrow features above their surroundings: the median over an
        // octave either side ignores the feature itself.
        var local = new double[n];
        for (int i = 0; i < n; i++) local[i] = fine[i] - Median(fine, g, g[i] / 2, g[i] * 2);

        // Problems first: these make the rest unreliable.
        if (input.OppositePolarity == true)
            advice.Add(new RoomAdvice(new RoomAdviceKind.Polarity(), RoomAdviceSeverity.Warning));
        if (input.WorstSnrDb < 25)
            advice.Add(new RoomAdvice(new RoomAdviceKind.Noisy(input.WorstSnrDb), RoomAdviceSeverity.Warning));

        // Left / right.
        if (input.Left is { } l && input.Right is { } r)
        {
            var ll = l.Resampled(g);
            var rr = r.Resampled(g);
            double diff = Mean(rr, g, 400, 4000) - Mean(ll, g, 400, 4000);
            if (Math.Abs(diff) >= 0.5)
            {
                advice.Add(new RoomAdvice(new RoomAdviceKind.Balance(diff > 0 ? EqChannels.Right : EqChannels.Left, Math.Abs(diff)),
                                          Math.Abs(diff) >= 1 ? RoomAdviceSeverity.Suggestion : RoomAdviceSeverity.Info));
            }
            var delta = Smoothing.FractionalOctave(Enumerable.Range(0, n).Select(i => (ll[i] - rr[i]) + diff).ToArray(), g, _ => 1.0 / 6);
            int? worst = null;
            for (int i = 0; i < n; i++)
            {
                if (g[i] < 30 || g[i] > 300) continue;
                if (worst is not { } w || Math.Abs(delta[w]) < Math.Abs(delta[i])) worst = i;
            }
            if (worst is { } wi && Math.Abs(delta[wi]) >= 4)
                advice.Add(new RoomAdvice(new RoomAdviceKind.ChannelMismatch(Math.Abs(delta[wi]), g[wi]), RoomAdviceSeverity.Info));
        }
        if (input.ArrivalDifference is { } dt)
        {
            double cm = dt * SpeedOfSound * 100;
            if (Math.Abs(cm) >= 2)
            {
                advice.Add(new RoomAdvice(new RoomAdviceKind.Distance(cm > 0 ? EqChannels.Right : EqChannels.Left, Math.Abs(cm)),
                                          Math.Abs(cm) >= 5 ? RoomAdviceSeverity.Suggestion : RoomAdviceSeverity.Info));
            }
        }

        // Level of the bass regions.
        double sub = Median(third, g, 30, 90);
        if (Math.Abs(sub) >= 3)
            advice.Add(new RoomAdvice(new RoomAdviceKind.Subwoofer(sub), RoomAdviceSeverity.Suggestion));
        double lowMid = Median(third, g, 100, 400);
        if (lowMid >= 1.5)
        {
            double step = Swift.Rounded(lowMid / 2).Clamp(1, 3) * 2;
            advice.Add(new RoomAdvice(new RoomAdviceKind.BassTilt(lowMid, -step), RoomAdviceSeverity.Suggestion));
        }
        // A desk bounce is broad (about an octave wide): look at the octave-smoothed
        // curve so a narrow room mode in the same place does not count.
        int? deskPeak = null;
        for (int i = 0; i < n; i++)
        {
            if (g[i] < 120 || g[i] > 220) continue;
            if (deskPeak is not { } p || octave[p] < octave[i]) deskPeak = i;
        }
        if (deskPeak is { } peak)
        {
            double f = g[peak];
            // Baseline above the subwoofer's range, whose level is judged separately.
            double bump = octave[peak] - Median(fine, g, Math.Max(90, f / 3), f * 3);
            if (bump >= 2.5)
                advice.Add(new RoomAdvice(new RoomAdviceKind.Desktop(f, bump), RoomAdviceSeverity.Suggestion));
        }

        // Narrow features in the bass.
        foreach (var (i, depth) in Extremes(local, g, 30, 300, minimum: true).Where(e => e.Value <= -8).Take(2))
        {
            double f = g[i];
            advice.Add(new RoomAdvice(new RoomAdviceKind.Cancellation(f, -depth, SpeedOfSound / (4 * f) * 100, f >= 50 && f <= 130),
                                      RoomAdviceSeverity.Suggestion));
        }
        foreach (var (i, height) in Extremes(local, g, 25, 300, minimum: false).Where(e => e.Value >= 4).Take(3))
        {
            double f = g[i];
            advice.Add(new RoomAdvice(new RoomAdviceKind.RoomMode(f, height, SpeedOfSound / (2 * f)), RoomAdviceSeverity.Info));
        }

        // Treble: depends on the microphone, so only a hint without calibration.
        double treble = Median(third, g, 4000, 12_000);
        if (Math.Abs(treble) >= 2)
        {
            double step = treble > 0 ? (treble >= 3 ? -4 : -2) : 2;
            advice.Add(new RoomAdvice(new RoomAdviceKind.Treble(treble, step, input.Calibrated),
                                      input.Calibrated ? RoomAdviceSeverity.Suggestion : RoomAdviceSeverity.Info));
        }

        if (input.Positions < 3)
            advice.Add(new RoomAdvice(new RoomAdviceKind.FewPositions(input.Positions), RoomAdviceSeverity.Info));

        return advice.Select((a, index) => (a, index))
            .OrderByDescending(x => x.a.Severity).ThenBy(x => x.index)
            .Select(x => x.a)
            .ToList();
    }

    // MARK: - Helpers

    internal static List<int> Indices(IReadOnlyList<double> g, double lo, double hi)
    {
        var result = new List<int>();
        for (int i = 0; i < g.Count; i++)
            if (g[i] >= lo && g[i] <= hi) result.Add(i);
        return result;
    }

    internal static double Mean(IReadOnlyList<double> x, IReadOnlyList<double> g, double lo, double hi)
    {
        var idx = Indices(g, lo, hi);
        double sum = 0;
        foreach (int i in idx) sum += x[i];
        return sum / Math.Max(idx.Count, 1);
    }

    internal static double Median(IReadOnlyList<double> x, IReadOnlyList<double> g, double lo, double hi)
    {
        var v = Indices(g, lo, hi).Select(i => x[i]).Order().ToArray();
        if (v.Length == 0) return 0;
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    internal static double Level(double[] x, double[] g, double f) => new FrequencyResponse(g, x).LevelAt(f);

    /// <summary>Local maxima (or minima) between <paramref name="lo"/> and <paramref name="hi"/>, strongest first, at least
    /// a sixth of an octave apart.</summary>
    internal static List<(int Index, double Value)> Extremes(double[] x, IReadOnlyList<double> g, double lo, double hi, bool minimum)
    {
        double sign = minimum ? -1.0 : 1.0;
        var found = new List<(int Index, double Value)>();
        foreach (int i in Indices(g, lo, hi))
        {
            if (i <= 0 || i >= x.Length - 1) continue;
            if (sign * x[i] >= sign * x[i - 1] && sign * x[i] > sign * x[i + 1]) found.Add((i, x[i]));
        }
        var kept = new List<(int Index, double Value)>();
        foreach (var e in found.OrderByDescending(e => sign * e.Value))
        {
            if (kept.Any(k => Math.Abs(Math.Log2(g[k.Index] / g[e.Index])) < 1.0 / 6)) continue;
            kept.Add(e);
        }
        return kept;
    }
}
