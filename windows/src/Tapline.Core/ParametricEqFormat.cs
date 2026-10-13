namespace Tapline.Core;

public enum ParametricEqParseError
{
    NoFilters,
}

public sealed class ParametricEqParseException(ParametricEqParseError error)
    : FormatException("No filters found in the parametric EQ text.")
{
    public ParametricEqParseError Error { get; } = error;
}

/// <summary>Reads and writes the Equalizer APO / AutoEQ "ParametricEQ.txt" format:
/// <code>
/// Preamp: -6.4 dB
/// Filter 1: ON LSC Fc 105 Hz Gain 4.2 dB Q 0.70
/// Filter 2: ON PK Fc 132 Hz Gain -10.8 dB Q 1.01
/// </code>
/// <c>Channel: L</c> / <c>Channel: R</c> / <c>Channel: all</c> lines select which channels the
/// following filters apply to, as in Equalizer APO.</summary>
public static class ParametricEqFormat
{
    public sealed class Parsed
    {
        public double? PreampDb { get; init; }
        public List<EqBand> Bands { get; init; } = [];
    }

    private static readonly char[] Newlines = ['\n', '\u000B', '\u000C', '\r', '\u0085', '\u2028', '\u2029'];

    /// <exception cref="ParametricEqParseException">The text contains no filters.</exception>
    public static Parsed Parse(string text)
    {
        double? preamp = null;
        var bands = new List<EqBand>();
        // null: the current channel selection is one Tapline cannot process (e.g. centre).
        EqChannels? channels = EqChannels.Both;

        foreach (var rawLine in text.Split(Newlines))
        {
            var line = Swift.TrimWhitespaces(rawLine);
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var lower = line.ToLowerInvariant();

            if (lower.StartsWith("preamp:", StringComparison.Ordinal))
            {
                preamp = Tokenize(line["preamp:".Length..]).Select(Swift.ParseDouble).FirstOrDefault(v => v is { } x && double.IsFinite(x));
                continue;
            }
            if (lower.StartsWith("channel:", StringComparison.Ordinal))
            {
                channels = ParseChannels(Tokenize(lower["channel:".Length..]));
                continue;
            }
            if (!lower.StartsWith("filter", StringComparison.Ordinal)) continue;
            int colon = line.IndexOf(':');
            if (channels is not { } current || colon < 0) continue;
            if (ParseFilter(Tokenize(line[(colon + 1)..])) is not { } band) continue;
            band.Channels = current;
            bands.Add(band);
        }

        if (bands.Count == 0) throw new ParametricEqParseException(ParametricEqParseError.NoFilters);
        return new Parsed { PreampDb = preamp, Bands = bands };
    }

    private static EqChannels? ParseChannels(List<string> tokens)
    {
        var set = tokens.ToHashSet();
        if (set.Count == 0 || set.Contains("all") || (set.Contains("l") && set.Contains("r"))) return EqChannels.Both;
        if (set.Contains("l") || set.Contains("1")) return EqChannels.Left;
        if (set.Contains("r") || set.Contains("2")) return EqChannels.Right;
        return null;
    }

    private static List<string> Tokenize(string s) => Swift.Split(s, ' ', '\t');

    private static EqBand? ParseFilter(List<string> tokens)
    {
        if (tokens.Count < 2) return null;
        bool enabled = tokens[0].ToUpperInvariant() == "ON";
        string typeCode = tokens[1].ToUpperInvariant();

        double? Value(string key)
        {
            int i = tokens.FindIndex(t => string.Equals(t, key, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= tokens.Count) return null;
            return Swift.ParseDouble(tokens[i + 1].Replace(',', '.'));
        }

        if (Value("Fc") is not { } fc) return null;
        double gain = Value("Gain") ?? 0;
        double? q = Value("Q");
        // "NaN", "Infinity" and "1e999" parse as numbers: such a filter is skipped, not imported.
        if (!double.IsFinite(fc) || !double.IsFinite(gain) || (q is { } qv && !double.IsFinite(qv))) return null;

        FilterType type;
        switch (typeCode)
        {
            case "PK" or "PEQ" or "MODAL":
                type = FilterType.Peak;
                break;
            case "LS" or "LSC" or "LSQ" or "LS6" or "LS12":
                type = FilterType.LowShelf;
                q ??= 0.71;
                break;
            case "HS" or "HSC" or "HSQ" or "HS6" or "HS12":
                type = FilterType.HighShelf;
                q ??= 0.71;
                break;
            case "LP" or "LPQ":
                type = FilterType.LowPass;
                q ??= 0.71;
                break;
            case "HP" or "HPQ":
                type = FilterType.HighPass;
                q ??= 0.71;
                break;
            case "NO":
                type = FilterType.Notch;
                q ??= 30;
                break;
            case "BP":
                type = FilterType.BandPass;
                break;
            default:
                return null;
        }

        return new EqBand(type, fc, gain, q ?? 1, enabled).Clamped();
    }

    public static string Export(IEnumerable<EqBand> bands, double preampDb)
    {
        var all = bands.ToList();
        var lines = new List<string> { $"Preamp: {Swift.Format(preampDb, 1)} dB" };
        // Shared filters first (no Channel line applies to all), then each side.
        var shared = all.Where(b => b.Channels == EqChannels.Both).ToList();
        var left = all.Where(b => b.Channels == EqChannels.Left).ToList();
        var right = all.Where(b => b.Channels == EqChannels.Right).ToList();
        int number = 0;
        void Append(List<EqBand> group)
        {
            foreach (var b in group)
            {
                number++;
                lines.Add(FilterLine(b, number));
            }
        }
        Append(shared);
        if (left.Count > 0) { lines.Add("Channel: L"); Append(left); }
        if (right.Count > 0) { lines.Add("Channel: R"); Append(right); }
        if (left.Count > 0 || right.Count > 0) lines.Add("Channel: all");
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>A multichannel room correction, one Equalizer APO section per speaker: its channel
    /// (numbered from 1 in the output's channel order), preamp (trim plus the headroom all
    /// speakers share), delay and filters.
    /// <code>
    /// # C (channel 3)
    /// Channel: 3
    /// Preamp: -4.5 dB
    /// Delay: 1.25 ms
    /// Filter 1: ON PK Fc 63 Hz Gain -6.0 dB Q 4.00
    /// </code></summary>
    public static string ExportSpeakers(IEnumerable<SpeakerCorrection> corrections, string layoutName, double headroomDb)
    {
        var lines = new List<string> { $"# Room correction for {layoutName} speakers, one section per speaker." };
        double headroom = double.IsFinite(headroomDb) ? headroomDb : 0;
        foreach (var c in corrections.OrderBy(c => c.Channel))
        {
            lines.Add("");
            lines.Add($"# {c.Position.Abbreviation()} (channel {c.Channel + 1})");
            lines.Add($"Channel: {c.Channel + 1}");
            double trim = double.IsFinite(c.TrimDb) ? c.TrimDb.Clamp(SpeakerCorrection.TrimRange) : 0;
            lines.Add($"Preamp: {Swift.Format(trim + headroom, 1)} dB");
            double delay = double.IsFinite(c.DelayMs) ? c.DelayMs.Clamp(SpeakerCorrection.DelayRange) : 0;
            if (delay >= 0.005) lines.Add($"Delay: {Swift.Format(delay, 2)} ms");
            int number = 0;
            foreach (var b in c.Bands.Where(b => b.IsEnabled).Take(SpeakerCorrection.MaxBands)) lines.Add(FilterLine(b, ++number));
        }
        lines.Add("");
        lines.Add("Channel: all");
        return string.Join("\n", lines) + "\n";
    }

    private static string FilterLine(EqBand b, int number)
    {
        string code = b.Type switch
        {
            FilterType.Peak => "PK",
            FilterType.LowShelf => "LSC",
            FilterType.HighShelf => "HSC",
            FilterType.LowPass => "LPQ",
            FilterType.HighPass => "HPQ",
            FilterType.Notch => "NO",
            _ => "BP",
        };
        string state = b.IsEnabled ? "ON" : "OFF";
        if (b.Type.UsesGain())
            return $"Filter {number}: {state} {code} Fc {Swift.Format(b.Frequency, 0)} Hz Gain {Swift.Format(b.Gain, 1)} dB Q {Swift.Format(b.Q, 2)}";
        return $"Filter {number}: {state} {code} Fc {Swift.Format(b.Frequency, 0)} Hz Q {Swift.Format(b.Q, 2)}";
    }
}
