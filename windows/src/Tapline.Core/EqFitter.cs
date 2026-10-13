namespace Tapline.Core;

/// <summary>Turns a headphone measurement and a target curve into a parametric EQ,
/// in the spirit of AutoEQ: smooth the required correction, start with two
/// shelves, then greedily add bells where the error is largest and refine all
/// filter parameters together after each addition.</summary>
public static class EqFitter
{
    public record struct Options
    {
        /// <summary>Total number of filters, shelves included.</summary>
        public int MaxFilters { get; set; } = 10;
        /// <summary>Extra bass on top of the target (low shelf at 105 Hz).</summary>
        public double BassAdjustDb { get; set; } = 0;
        /// <summary>Extra tilt on top of the target, dB per octave around 1 kHz.</summary>
        public double TiltDbPerOctave { get; set; } = 0;
        /// <summary>Leave everything below this frequency untouched (e.g. headphones used
        /// in a different mode than they were measured in).</summary>
        public double? KeepBassBelow { get; set; }
        /// <summary>Same defaults as AutoEQ: boosts are capped harder than cuts.</summary>
        public double MaxBoostDb { get; set; } = 6;
        public double MaxCutDb { get; set; } = 12;
        /// <summary>Correct only below this frequency (room correction: leave the treble to the speakers).</summary>
        public double? CorrectOnlyBelow { get; set; }
        /// <summary>Never boost below this frequency (the speakers' low-frequency limit);
        /// resonances there are still cut.</summary>
        public double? NoBoostBelow { get; set; }
        /// <summary>Narrowest bell allowed. Room modes need more than headphones.</summary>
        public double MaxQ { get; set; } = 6;
        /// <summary>Smoothing width in octaves below 1 kHz.</summary>
        public double LowSmoothingOctaves { get; set; } = 1.0 / 6;
        /// <summary>Fixed bell slots of a hardware EQ. When set, the fit uses at most one
        /// bell per slot, each within its slot's limits, and no shelves;
        /// <see cref="MaxFilters"/> is ignored.</summary>
        public IReadOnlyList<Slot>? Slots { get; set; }
        /// <summary>Round the result to the steps a hardware EQ can be set to.</summary>
        public double? GainStepDb { get; set; }
        public double? QStep { get; set; }
        /// <summary>How much of the correction to apply, 0...1, like a dry/wet control: the EQ is
        /// fitted to the measurement moved this far towards the target.</summary>
        public double Strength { get; set; } = 1;

        public Options() { }

        public Options(int maxFilters = 10, double bassAdjustDb = 0, double tiltDbPerOctave = 0, double? keepBassBelow = null)
        {
            MaxFilters = maxFilters;
            BassAdjustDb = bassAdjustDb;
            TiltDbPerOctave = tiltDbPerOctave;
            KeepBassBelow = keepBassBelow;
        }

        public readonly bool Equals(Options other) =>
            MaxFilters == other.MaxFilters && BassAdjustDb.Equals(other.BassAdjustDb) && TiltDbPerOctave.Equals(other.TiltDbPerOctave)
            && KeepBassBelow == other.KeepBassBelow && MaxBoostDb.Equals(other.MaxBoostDb) && MaxCutDb.Equals(other.MaxCutDb)
            && CorrectOnlyBelow == other.CorrectOnlyBelow && NoBoostBelow == other.NoBoostBelow && MaxQ.Equals(other.MaxQ)
            && LowSmoothingOctaves.Equals(other.LowSmoothingOctaves) && GainStepDb == other.GainStepDb && QStep == other.QStep
            && Strength.Equals(other.Strength)
            && (Slots is null ? other.Slots is null : other.Slots is not null && Slots.SequenceEqual(other.Slots));

        public override readonly int GetHashCode() =>
            HashCode.Combine(MaxFilters, MaxBoostDb, MaxCutDb, MaxQ, CorrectOnlyBelow, NoBoostBelow, Slots?.Count);
    }

    /// <summary>One band of a hardware parametric EQ.</summary>
    public readonly record struct Slot((double Min, double Max) FrequencyRange, (double Min, double Max) QRange)
    {
        public bool Accepts(double frequency, double q) =>
            frequency >= FrequencyRange.Min - 1e-6 && frequency <= FrequencyRange.Max + 1e-6
            && q >= QRange.Min - 1e-6 && q <= QRange.Max + 1e-6;
    }

    /// <summary>Puts each filter into a slot that can hold it, lowest frequencies into the
    /// first slots where possible. Returns the slot index per filter, or null if
    /// the filters do not fit.</summary>
    public static int[]? Assign(IReadOnlyList<(double Frequency, double Q)> filters, IReadOnlyList<Slot> slots)
    {
        if (filters.Count > slots.Count) return null;
        var byFrequency = Enumerable.Range(0, filters.Count).OrderBy(i => filters[i].Frequency).ToArray();
        var used = new bool[slots.Count];
        var result = Enumerable.Repeat(-1, filters.Count).ToArray();

        bool Place(int k)
        {
            if (k == byFrequency.Length) return true;
            int i = byFrequency[k];
            for (int s = 0; s < slots.Count; s++)
            {
                if (used[s] || !slots[s].Accepts(filters[i].Frequency, filters[i].Q)) continue;
                used[s] = true;
                result[i] = s;
                if (Place(k + 1)) return true;
                used[s] = false;
            }
            return false;
        }
        return Place(0) ? result : null;
    }

    /// <summary>Hardware frequency steps: 1 Hz below 1 kHz, 10 Hz below 10 kHz, 100 Hz above.</summary>
    internal static double RoundedFrequency(double f)
    {
        if (f < 1000) return Swift.Rounded(f);
        if (f < 10_000) return Swift.Rounded(f / 10) * 10;
        return Swift.Rounded(f / 100) * 100;
    }

    public sealed class Result
    {
        public List<EqBand> Bands { get; init; } = [];
        public double[] Frequencies { get; init; } = [];
        /// <summary>Measurement, level-matched to the target.</summary>
        public double[] Measurement { get; init; } = [];
        /// <summary>Target including the bass / tilt preferences.</summary>
        public double[] Target { get; init; } = [];
        /// <summary>What the EQ was fitted to: the measurement moved <see cref="Options.Strength"/> of the way to <see cref="Target"/>.</summary>
        public double[] FitTarget { get; init; } = [];
        /// <summary>Measurement with the fitted EQ applied.</summary>
        public double[] Predicted { get; init; } = [];
        /// <summary>Weighted RMS deviation from the full target (20 Hz - 10 kHz) before and after EQ:
        /// at lower strengths, part of the deviation is left on purpose.</summary>
        public double ErrorBeforeDb { get; init; }
        public double ErrorAfterDb { get; init; }
    }

    internal static readonly double[] GridValues = ResponseCurve.LogFrequencies(384);

    public static IReadOnlyList<double> Grid { get; } = Array.AsReadOnly(GridValues);

    internal const double SampleRate = 48_000.0;

    public static Result Fit(FrequencyResponse measurement, FrequencyResponse target, Options? options = null)
    {
        var o = options ?? new Options();
        var f = GridValues;
        int n = f.Length;
        var t = target.Resampled(f);
        var m = measurement.Resampled(f);

        // Preferences on top of the target.
        if (o.BassAdjustDb != 0)
        {
            var shelf = BiquadCoefficients.Design(FilterType.LowShelf, 105, o.BassAdjustDb, 0.71, SampleRate);
            for (int i = 0; i < n; i++) t[i] += shelf.MagnitudeDb(f[i], SampleRate);
        }
        if (o.TiltDbPerOctave != 0)
        {
            for (int i = 0; i < n; i++) t[i] += o.TiltDbPerOctave * Math.Log2(f[i] / 1000);
        }

        // Level-match on the lower midrange, below the ear-gain region where targets differ most.
        double offsetSum = 0;
        int midCount = 0;
        for (int i = 0; i < n; i++)
        {
            if (f[i] < 200 || f[i] > 1200) continue;
            offsetSum = offsetSum + t[i] - m[i];
            midCount++;
        }
        double offset = offsetSum / Math.Max(midCount, 1);
        for (int i = 0; i < n; i++) m[i] += offset;

        // Partial strength: fit to a target between the measurement and the full target, so
        // the EQ is as accurate as at full strength, just less of it.
        double strength = double.IsNaN(o.Strength) ? 1 : Math.Clamp(o.Strength, 0, 1);
        var goal = new double[n];
        for (int i = 0; i < n; i++) goal[i] = strength == 1 ? t[i] : m[i] + strength * (t[i] - m[i]);

        // Required correction, smoothed more in the treble where measurements are least reliable.
        var c = new double[n];
        for (int i = 0; i < n; i++) c[i] = strength * (t[i] - m[i]);
        // Like AutoEQ: fine smoothing below 6 kHz, two-octave smoothing above 8 kHz with
        // a crossfade in between. Measurement rigs disagree wildly up there (coupler
        // resonances, fit), so only the broad treble trend is worth correcting.
        double lowWidth = o.LowSmoothingOctaves;
        var fine = Smoothing.FractionalOctave(c, f, x => x < 1000 ? lowWidth : 1.0 / 5);
        var broad = Smoothing.FractionalOctave(c, f, _ => 2.0);
        for (int i = 0; i < n; i++)
        {
            double k = (Math.Log2(f[i] / 6000) / Math.Log2(8000.0 / 6000)).Clamp(0, 1);
            c[i] = fine[i] * (1 - k) + broad[i] * k;
        }
        for (int i = 0; i < n; i++)
        {
            if (f[i] > 12_000)
                c[i] *= Math.Max(0, 1 - Math.Log(f[i] / 12_000) / Math.Log(20_000.0 / 12_000));
            if (o.CorrectOnlyBelow is { } top)
            {
                if (f[i] >= top * 2) c[i] = 0;
                else if (f[i] > top)
                {
                    double x = Math.Log2(f[i] / top);
                    c[i] *= 1 - x * x * (3 - 2 * x);
                }
            }
            if (o.KeepBassBelow is { } keep)
            {
                if (f[i] <= keep) c[i] = 0;
                else if (f[i] < keep * 2)
                {
                    double x = Math.Log2(f[i] / keep);
                    c[i] *= x * x * (3 - 2 * x);
                }
            }
            if (o.NoBoostBelow is { } floor && f[i] < floor) c[i] = Math.Min(c[i], 0);
            c[i] = c[i].Clamp(-o.MaxCutDb, o.MaxBoostDb);
        }

        var w = new double[n];
        for (int i = 0; i < n; i++)
        {
            double freq = f[i];
            w[i] = freq < 30 ? 0.5 : freq <= 8000 ? 1 : freq <= 12_000 ? 0.5 : freq <= 16_000 ? 0.2 : 0.05;
        }

        var model = new FitModel(f, c, w, o);
        model.Fit();

        var response = model.Total;
        var predicted = new double[n];
        var errBefore = new double[n];
        var errAfter = new double[n];
        for (int i = 0; i < n; i++)
        {
            predicted[i] = m[i] + response[i];
            errBefore[i] = t[i] - m[i];
            errAfter[i] = t[i] - predicted[i];
        }

        return new Result
        {
            Bands = model.Bands(),
            Frequencies = [.. f],
            Measurement = m,
            Target = t,
            FitTarget = goal,
            Predicted = predicted,
            ErrorBeforeDb = WeightedRms(errBefore, w, f),
            ErrorAfterDb = WeightedRms(errAfter, w, f),
        };
    }

    internal static double WeightedRms(double[] e, double[] w, double[] f)
    {
        double sum = 0, weight = 0;
        for (int i = 0; i < e.Length; i++)
        {
            if (f[i] > 10_000) continue;
            sum += w[i] * e[i] * e[i];
            weight += w[i];
        }
        return Math.Sqrt(sum / Math.Max(weight, 1e-9));
    }

    // MARK: - Optimizer

    private sealed class FitModel
    {
        private enum Kind { Peak, LowShelf, HighShelf }

        private struct Filter(Kind kind, double log2F, double gain, double logQ)
        {
            public Kind Kind = kind;
            public double Log2F = log2F;
            public double Gain = gain;
            public double LogQ = logQ;

            public readonly double Frequency => Math.Pow(2, Log2F);
            public readonly double Q => Math.Exp(LogQ);
        }

        private readonly double[] f;
        private readonly double[] c;
        private readonly double[] w;
        private readonly Options options;
        private readonly double[] cosW;
        private readonly double[] cos2W;

        private List<Filter> filters = [];
        private List<double[]> responses = [];
        private double[] total;

        public FitModel(double[] frequencies, double[] correction, double[] weights, Options options)
        {
            f = frequencies;
            c = correction;
            w = weights;
            this.options = options;
            cosW = frequencies.Select(x => Math.Cos(2 * Math.PI * x / SampleRate)).ToArray();
            cos2W = frequencies.Select(x => Math.Cos(4 * Math.PI * x / SampleRate)).ToArray();
            total = new double[frequencies.Length];
        }

        public double[] Total => total;

        /// <summary>Highest bell frequency: 10 kHz, or a bit above the correction limit.</summary>
        private double PeakCeiling => Math.Min(10_000, (options.CorrectOnlyBelow ?? 10_000) * 1.5);

        /// <summary>Bell limits: the union of the hardware slots, if any.</summary>
        private (double Min, double Max) PeakFrequencyRange
        {
            get
            {
                if (options.Slots is not { Count: > 0 } slots) return (20, PeakCeiling);
                double lo = Math.Max(20, slots.Min(s => s.FrequencyRange.Min));
                return (lo, Math.Max(lo, Math.Min(PeakCeiling, slots.Max(s => s.FrequencyRange.Max))));
            }
        }

        private (double Min, double Max) PeakQRange
        {
            get
            {
                double top = Math.Max(options.MaxQ, 0.5);
                if (options.Slots is not { Count: > 0 } slots) return (0.25, top);
                double lo = slots.Min(s => s.QRange.Min);
                return (lo, Math.Max(lo, Math.Min(top, slots.Max(s => s.QRange.Max))));
            }
        }

        /// <summary>Whether the filters fit the hardware slots (always true without slots).</summary>
        private bool FitsSlots(IReadOnlyList<Filter> list)
        {
            if (options.Slots is not { } slots) return true;
            return Assign(list.Select(x => (x.Frequency, x.Q)).ToArray(), slots) is not null;
        }

        // Bounds per kind
        private Filter Clamp(Filter x)
        {
            switch (x.Kind)
            {
                case Kind.Peak:
                    var fr = PeakFrequencyRange;
                    var qr = PeakQRange;
                    x.Log2F = x.Log2F.Clamp(Math.Log2(fr.Min), Math.Log2(fr.Max));
                    x.LogQ = x.LogQ.Clamp(Math.Log(qr.Min), Math.Log(qr.Max));
                    break;
                case Kind.LowShelf:
                    x.Log2F = x.Log2F.Clamp(Math.Log2(40), Math.Log2(250));
                    x.LogQ = x.LogQ.Clamp(Math.Log(0.4), Math.Log(1.2));
                    break;
                case Kind.HighShelf:
                    x.Log2F = x.Log2F.Clamp(Math.Log2(4000), Math.Log2(14_000));
                    x.LogQ = x.LogQ.Clamp(Math.Log(0.4), Math.Log(1.2));
                    break;
            }
            x.Gain = x.Gain.Clamp(-options.MaxCutDb, options.MaxBoostDb);
            return x;
        }

        private static FilterType TypeOf(Kind kind) =>
            kind == Kind.Peak ? FilterType.Peak : kind == Kind.LowShelf ? FilterType.LowShelf : FilterType.HighShelf;

        private double[] Response(Filter x)
        {
            var k = BiquadCoefficients.Design(TypeOf(x.Kind), x.Frequency, x.Gain, x.Q, SampleRate);
            var result = new double[f.Length];
            for (int i = 0; i < f.Length; i++)
            {
                double num = k.B0 * k.B0 + k.B1 * k.B1 + k.B2 * k.B2 + 2 * (k.B0 * k.B1 + k.B1 * k.B2) * cosW[i] + 2 * k.B0 * k.B2 * cos2W[i];
                double den = 1 + k.A1 * k.A1 + k.A2 * k.A2 + 2 * (k.A1 + k.A1 * k.A2) * cosW[i] + 2 * k.A2 * cos2W[i];
                result[i] = 10 * Math.Log10(Math.Max(num, 1e-30) / Math.Max(den, 1e-30));
            }
            return result;
        }

        private double Error(double[] sum)
        {
            double e = 0;
            for (int i = 0; i < f.Length; i++)
            {
                double d = c[i] - sum[i];
                e += w[i] * d * d;
            }
            return e;
        }

        private void Add(Filter x)
        {
            x = Clamp(x);
            var r = Response(x);
            filters.Add(x);
            responses.Add(r);
            for (int i = 0; i < f.Length; i++) total[i] += r[i];
        }

        private double Mean(double[] values, double lo, double hi)
        {
            double sum = 0;
            int count = 0;
            for (int i = 0; i < f.Length; i++)
            {
                if (f[i] < lo || f[i] > hi) continue;
                sum += values[i];
                count++;
            }
            return sum / Math.Max(count, 1);
        }

        private void RecomputeTotal()
        {
            total = new double[f.Length];
            for (int i = 0; i < f.Length; i++)
            {
                double sum = 0;
                foreach (var r in responses) sum += r[i];
                total[i] = sum;
            }
        }

        public void Fit()
        {
            int budget = Math.Max(1, options.Slots?.Count ?? options.MaxFilters);

            // Shelves for broad bass / treble trends (hardware slots take bells only).
            if (options.Slots is null)
            {
                double bass = Mean(c, 20, 60);
                if (Math.Abs(bass) > 0.5 && filters.Count < budget && options.KeepBassBelow is null)
                    Add(new Filter(Kind.LowShelf, Math.Log2(105), bass, Math.Log(0.71)));
                double treble = Mean(c, 10_000, 14_000);
                if (Math.Abs(treble) > 0.5 && filters.Count < budget && options.CorrectOnlyBelow is null)
                    Add(new Filter(Kind.HighShelf, Math.Log2(10_000), treble * 0.6, Math.Log(0.71)));
                Refine();
            }

            while (filters.Count < budget)
            {
                // Largest weighted residual in the band we trust, among the spots a free slot can take.
                var range = PeakFrequencyRange;
                var candidates = Enumerable.Range(0, f.Length)
                    .Where(i => f[i] >= Math.Max(25, range.Min) && f[i] <= range.Max)
                    .Select(i => (Index: i, Score: Math.Abs(c[i] - total[i]) * w[i]))
                    .Where(x => x.Score > 0.35)
                    .OrderByDescending(x => x.Score)
                    .ToList();
                bool added = false;
                foreach (var candidate in candidates)
                {
                    int best = candidate.Index;
                    double peak = c[best] - total[best];

                    // Width at half height -> initial Q.
                    int lo = best, hi = best;
                    while (lo > 0 && Math.Abs(c[lo - 1] - total[lo - 1]) > Math.Abs(peak) / 2 && (c[lo - 1] - total[lo - 1]) * peak > 0) lo--;
                    while (hi < f.Length - 1 && Math.Abs(c[hi + 1] - total[hi + 1]) > Math.Abs(peak) / 2 && (c[hi + 1] - total[hi + 1]) * peak > 0) hi++;
                    double octaves = Math.Max(Math.Log2(f[hi] / f[lo]), 0.1);
                    double q = Math.Sqrt(Math.Pow(2, octaves)) / (Math.Pow(2, octaves) - 1);
                    var x = Clamp(new Filter(Kind.Peak, Math.Log2(f[best]), peak, Math.Log(q)));
                    if (Placeable(x) is { } placed)
                    {
                        Add(placed);
                        added = true;
                        break;
                    }
                    if (options.Slots is null) break;
                }
                if (!added) break;
                Refine(maxPasses: 40);
            }
            Refine(maxPasses: 400);
            Quantize();

            // Drop filters that ended up doing nothing.
            var keep = Enumerable.Range(0, filters.Count).Where(i => Math.Abs(filters[i].Gain) >= 0.25).ToList();
            if (keep.Count != filters.Count)
            {
                filters = keep.Select(i => filters[i]).ToList();
                responses = keep.Select(i => responses[i]).ToList();
                RecomputeTotal();
            }
        }

        /// <summary><paramref name="x"/> as is if it fits a free slot, else with its Q narrowed or widened into
        /// the limits of a free slot that covers its frequency, else null.</summary>
        private Filter? Placeable(Filter x)
        {
            if (FitsSlots([.. filters, x])) return x;
            if (options.Slots is not { } slots) return null;
            foreach (var slot in slots)
            {
                if (!slot.FrequencyRange.Contains(x.Frequency)) continue;
                var y = x;
                y.LogQ = y.LogQ.Clamp(Math.Log(slot.QRange.Min), Math.Log(slot.QRange.Max));
                if (FitsSlots([.. filters, y])) return y;
            }
            return null;
        }

        /// <summary>Rounds every filter to the hardware's steps and keeps it inside its slot.</summary>
        private void Quantize()
        {
            if (options.Slots is null && options.GainStepDb is null && options.QStep is null) return;
            for (int k = 0; k < filters.Count; k++)
            {
                var x = filters[k];
                if (options.GainStepDb is { } gainStep && gainStep > 0) x.Gain = Swift.Rounded(x.Gain / gainStep) * gainStep;
                if (options.QStep is { } qStep && qStep > 0) x.LogQ = Math.Log(Math.Max(qStep, Swift.Rounded(x.Q / qStep) * qStep));
                if (options.Slots is not null) x.Log2F = Math.Log2(RoundedFrequency(x.Frequency));
                filters[k] = x;
            }
            if (options.Slots is { } slots
                && (Assign(filters.Select(x => (x.Frequency, x.Q)).ToArray(), slots) ?? RelaxedPlacement(slots)) is { } placement)
            {
                for (int k = 0; k < filters.Count; k++)
                {
                    var slot = slots[placement[k]];
                    var x = filters[k];
                    x.Log2F = x.Log2F.Clamp(Math.Log2(slot.FrequencyRange.Min), Math.Log2(slot.FrequencyRange.Max));
                    x.LogQ = x.LogQ.Clamp(Math.Log(slot.QRange.Min), Math.Log(slot.QRange.Max));
                    filters[k] = x;
                }
            }
            responses = filters.Select(Response).ToList();
            RecomputeTotal();
        }

        /// <summary>Placement by frequency alone, for when rounding nudged a Q just past a limit.</summary>
        private int[]? RelaxedPlacement(IReadOnlyList<Slot> slots)
        {
            var loose = slots.Select(s => new Slot(s.FrequencyRange, (0, 1000))).ToArray();
            return Assign(filters.Select(x => (x.Frequency, x.Q)).ToArray(), loose);
        }

        /// <summary>Coordinate pattern search over (log2 f, gain, ln q) of every filter.</summary>
        private void Refine(int maxPasses = 400)
        {
            if (filters.Count == 0) return;
            double current = Error(total);
            double scale = 1.0;
            const double stepFreq = 0.08, stepGain = 0.4, stepQ = 0.15;
            int passes = 0;
            var candidate = new double[f.Length];
            while (scale > 0.015 && passes < maxPasses)
            {
                passes++;
                bool improved = false;
                for (int k = 0; k < filters.Count; k++)
                {
                    for (int param = 0; param < 3; param++)
                    {
                        foreach (double direction in (ReadOnlySpan<double>)[1.0, -1.0])
                        {
                            var x = filters[k];
                            switch (param)
                            {
                                case 0: x.Log2F += direction * stepFreq * scale; break;
                                case 1: x.Gain += direction * stepGain * scale; break;
                                default: x.LogQ += direction * stepQ * scale; break;
                            }
                            x = Clamp(x);
                            if (options.Slots is not null)
                            {
                                var trial = filters.ToArray();
                                trial[k] = x;
                                if (!FitsSlots(trial)) continue;
                            }
                            var r = Response(x);
                            var old = responses[k];
                            for (int i = 0; i < f.Length; i++) candidate[i] = total[i] + (r[i] - old[i]);
                            double e = Error(candidate);
                            if (e < current - 1e-9)
                            {
                                filters[k] = x;
                                responses[k] = r;
                                (total, candidate) = (candidate, total);
                                current = e;
                                improved = true;
                                break;
                            }
                        }
                    }
                }
                if (!improved) scale *= 0.5;
            }
        }

        public List<EqBand> Bands()
        {
            static int Order(Filter x) => x.Kind == Kind.LowShelf ? 0 : x.Kind == Kind.Peak ? 1 : 2;
            return filters
                .OrderBy(Order).ThenBy(x => x.Frequency)
                .Select(x => new EqBand(TypeOf(x.Kind),
                                        Swift.Rounded(x.Frequency),
                                        Swift.Rounded(x.Gain * 10) / 10,
                                        Swift.Rounded(x.Q * 100) / 100))
                .ToList();
        }
    }
}
