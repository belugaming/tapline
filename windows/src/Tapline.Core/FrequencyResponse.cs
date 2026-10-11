namespace Tapline.Core;

/// <summary>A measured or target frequency response (frequency in Hz, level in dB).</summary>
public sealed class FrequencyResponse : IEquatable<FrequencyResponse>
{
    public double[] Frequencies { get; set; }
    public double[] Levels { get; set; }

    public FrequencyResponse()
    {
        Frequencies = [];
        Levels = [];
    }

    public FrequencyResponse(double[] frequencies, double[] levels)
    {
        if (frequencies.Length != levels.Length)
            throw new ArgumentException("Frequencies and levels must have the same length.", nameof(levels));
        Frequencies = frequencies;
        Levels = levels;
    }

    /// <summary>Parses REW exports, squig.link <c>.txt</c> files and simple CSVs:
    /// any line whose first two numbers are a positive frequency and a level.</summary>
    public static FrequencyResponse? Parse(string text)
    {
        var points = new List<(double F, double Db)>();
        foreach (var raw in Swift.Lines(text))
        {
            var line = Swift.TrimWhitespaces(raw);
            if (line.Length == 0 || "*#;/".Contains(line[0])) continue;
            var tokens = Swift.Split(line, ' ', '\t', ',', ';');
            if (tokens.Count < 2
                || !Swift.TryParseDouble(tokens[0], out double f) || !Swift.TryParseDouble(tokens[1], out double db)
                || !(f > 0) || !double.IsFinite(f) || !double.IsFinite(db)) continue;
            points.Add((f, db));
        }
        var freqs = new List<double>();
        var levels = new List<double>();
        foreach (var (f, db) in points.OrderBy(p => p.F))
        {
            if (freqs.Count > 0 && !(f > freqs[^1])) continue;
            freqs.Add(f);
            levels.Add(db);
        }
        if (freqs.Count < 10) return null;
        return new FrequencyResponse([.. freqs], [.. levels]);
    }

    /// <summary>Level at <paramref name="f"/>, linearly interpolated on a log-frequency axis and held flat beyond the ends.</summary>
    public double LevelAt(double f)
    {
        if (Frequencies.Length == 0) return 0;
        if (f <= Frequencies[0]) return Levels[0];
        if (f >= Frequencies[^1]) return Levels[^1];
        int lo = 0, hi = Frequencies.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (Frequencies[mid] <= f) lo = mid; else hi = mid;
        }
        double t = Math.Log(f / Frequencies[lo]) / Math.Log(Frequencies[hi] / Frequencies[lo]);
        return Levels[lo] + (Levels[hi] - Levels[lo]) * t;
    }

    public double[] Resampled(IReadOnlyList<double> grid)
    {
        var result = new double[grid.Count];
        for (int i = 0; i < result.Length; i++) result[i] = LevelAt(grid[i]);
        return result;
    }

    /// <summary>Averages several responses (e.g. left and right channel) in dB on a grid.</summary>
    public static FrequencyResponse? Average(IReadOnlyList<FrequencyResponse> responses, IReadOnlyList<double> grid)
    {
        if (responses.Count == 0) return null;
        var sampled = responses.Select(r => r.Resampled(grid)).ToArray();
        var mean = new double[grid.Count];
        for (int i = 0; i < mean.Length; i++)
        {
            double sum = 0;
            foreach (var s in sampled) sum += s[i];
            mean[i] = sum / sampled.Length;
        }
        return new FrequencyResponse([.. grid], mean);
    }

    public bool Equals(FrequencyResponse? other) =>
        other is not null && Frequencies.AsSpan().SequenceEqual(other.Frequencies) && Levels.AsSpan().SequenceEqual(other.Levels);

    public override bool Equals(object? obj) => obj is FrequencyResponse other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var f in Frequencies) hash.Add(f);
        foreach (var l in Levels) hash.Add(l);
        return hash.ToHashCode();
    }
}

internal static class Smoothing
{
    /// <summary>Moving average whose width (in octaves) depends on frequency.
    /// <paramref name="grid"/> must be log-spaced.</summary>
    public static double[] FractionalOctave(IReadOnlyList<double> values, IReadOnlyList<double> grid, Func<double, double> width)
    {
        if (grid.Count <= 2) return [.. values];
        double pointsPerOctave = (grid.Count - 1) / Math.Log2(grid[^1] / grid[0]);
        var result = new double[values.Count];
        for (int i = 0; i < result.Length; i++)
        {
            int half = Math.Max(0, (int)Swift.Rounded(width(grid[i]) * pointsPerOctave / 2));
            int lo = Math.Max(0, i - half), hi = Math.Min(values.Count - 1, i + half);
            double sum = 0;
            for (int j = lo; j <= hi; j++) sum += values[j];
            result[i] = sum / (hi - lo + 1);
        }
        return result;
    }
}
