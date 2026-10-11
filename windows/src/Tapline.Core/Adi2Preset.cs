using System.Text;

namespace Tapline.Core;

public enum Adi2PresetError
{
    /// <summary>More bells than the ADI-2 has, or bells it cannot place (bands 4 and 5 start at 200 Hz and stop at Q 5).</summary>
    DoesNotFit,
    /// <summary>Filters other than bells, one low shelf (Bass, 20-150 Hz) and one high shelf (Treble, 3-10 kHz).</summary>
    UnsupportedFilter,
    NotAPreset,
}

public sealed class Adi2PresetException : Exception
{
    public Adi2PresetError Error { get; }
    /// <summary>The offending filter type for <see cref="Adi2PresetError.UnsupportedFilter"/>.</summary>
    public FilterType? FilterType { get; }

    public Adi2PresetException(Adi2PresetError error, FilterType? filterType = null)
        : base(error switch
        {
            Adi2PresetError.DoesNotFit => "The filters do not fit the ADI-2's parametric EQ.",
            Adi2PresetError.UnsupportedFilter => $"The ADI-2 cannot hold this filter ({filterType}).",
            _ => "Not an ADI-2 preset file.",
        })
    {
        Error = error;
        FilterType = filterType;
    }
}

/// <summary>The parametric EQ of an RME ADI-2 DAC and its preset file (<c>.adieqpr</c>), the
/// format RME's ADI-2 Remote app saves and loads in its PEQ Editor:
/// <code>
/// 	&lt;ADI-2DAC&gt;
/// 		&lt;General&gt;
/// 			&lt;val e="Version" v="2"/&gt;
/// 		&lt;/General&gt;
/// 		&lt;EQPreset1Left&gt;
/// 			&lt;val e="EQEnable" v="1.00,"/&gt;
/// 			&lt;val e="EQBand1Type" v="0.00,"/&gt;
/// 			&lt;val e="EQBand1Gain" v="-4.50,"/&gt;
/// 			...
/// </code>
/// Band 1 and 5 types: 0 = peak, 1 = shelf (the factory default), 2 = cut.
/// Tapline exports bells to the five bands and shelves to Bass / Treble.</summary>
public static class Adi2Preset
{
    /// <summary>Bands 1-3 cover 20 Hz - 20 kHz at Q 0.5 - 9.9, bands 4 and 5 start at
    /// 200 Hz and stop at Q 5.0 (ADI-2 DAC FS manual, chapter 12.2).</summary>
    public static IReadOnlyList<EqFitter.Slot> Slots { get; } = Array.AsReadOnly<EqFitter.Slot>(
    [
        new((20, 20_000), (0.5, 9.9)),
        new((20, 20_000), (0.5, 9.9)),
        new((20, 20_000), (0.5, 9.9)),
        new((200, 20_000), (0.5, 5.0)),
        new((200, 20_000), (0.5, 5.0)),
    ]);
    public const double GainMin = -12, GainMax = 12;
    public const double GainStepDb = 0.5;
    public const double QStep = 0.1;
    public const string FileExtension = "adieqpr";

    /// <summary>Fitter options that only produce what the ADI-2 can be set to.</summary>
    public static EqFitter.Options FitOptions(EqFitter.Options baseOptions)
    {
        var o = baseOptions;
        o.Slots = Slots;
        o.GainStepDb = GainStepDb;
        o.QStep = QStep;
        o.MaxBoostDb = Math.Min(o.MaxBoostDb, GainMax);
        o.MaxCutDb = Math.Min(o.MaxCutDb, -GainMin);
        o.MaxQ = Math.Min(o.MaxQ, 9.9);
        return o;
    }

    /// <summary>Bass and Treble are fixed shelves (manual, chapter 12.3).</summary>
    internal static readonly (double Min, double Max) BassRange = (20, 150);
    internal static readonly (double Min, double Max) TrebleRange = (3000, 10_000);
    internal static readonly (double Min, double Max) ShelfQRange = (0.5, 1.5);

    // MARK: - Export

    /// <summary>Builds a preset file. With <paramref name="right"/>, Dual EQ is switched on and each
    /// channel gets its own bands.</summary>
    /// <exception cref="Adi2PresetException">The bands do not fit the hardware.</exception>
    public static string Export(IReadOnlyList<EqBand> left, string name, IReadOnlyList<EqBand>? right = null)
    {
        static bool Exportable(EqBand b) => b.Type == FilterType.Peak || !b.Type.UsesGain();
        var l = Placed(left.Where(Exportable).ToList());
        var r = right is null ? null : Placed(right.Where(Exportable).ToList());
        var (bass, treble) = ToneShelves([.. left, .. right ?? []]);

        var lines = new List<string> { "\t<ADI-2DAC>", "\t\t<General>", "\t\t\t<val e=\"Version\" v=\"2\"/>", "\t\t</General>" };
        void Val(string key, double value) => lines.Add($"\t\t\t<val e=\"{key}\" v=\"{Swift.Format(value, 2)},\"/>");
        void Bands(EqBand?[] slots)
        {
            Val("EQEnable", 1);
            for (int i = 0; i < slots.Length; i++)
            {
                int n = i + 1;
                var b = slots[i] ?? new EqBand(FilterType.Peak, DefaultFrequencies[i]);
                if (n == 1 || n == 5) Val($"EQBand{n}Type", 0);
                Val($"EQBand{n}Gain", b.Gain);
                Val($"EQBand{n}Freq", b.Frequency);
                Val($"EQBand{n}Q", b.Q);
            }
        }

        lines.Add("\t\t<EQPreset1Left>");
        Bands(l);
        // Shelves go to Bass / Treble, loaded with the preset. Without shelves they
        // keep the factory values and are not loaded, so the unit's tone controls stay as they are.
        Val("BassTrebleEn", 1);
        Val("EQBassGain", bass?.Gain ?? 0);
        Val("EQBassFreq", bass?.Frequency ?? 85);
        Val("EQBassQ", bass?.Q ?? 0.9);
        Val("EQTrebleGain", treble?.Gain ?? 0);
        Val("EQTrebleFreq", treble?.Frequency ?? 6500);
        Val("EQTrebleQ", treble?.Q ?? 0.7);
        Val("DualEQ", r is null ? 0 : 1);
        Val("EQ Preset Select", 1);
        Val("Load BT w. EQ", bass is not null || treble is not null ? 1 : 0);
        lines.Add("\t\t</EQPreset1Left>");
        lines.Add("\t\t<EQPreset1Right>");
        Bands(r ?? l);
        lines.Add("\t\t</EQPreset1Right>");
        lines.Add("\t\t<PresetNames>");
        lines.Add($"\t\t\t<val e=\"EQPresetName1\" v=\"{PresetName(name)}\"/>");
        lines.Add("\t\t</PresetNames>");
        lines.Add("\t</ADI-2DAC>");
        return string.Join("\n", lines) + "\n";
    }

    private static readonly double[] DefaultFrequencies = [100, 500, 1000, 5000, 10_000];

    internal static EqBand Rounded(EqBand b) => b with
    {
        Gain = (Swift.Rounded(b.Gain / GainStepDb) * GainStepDb).Clamp(GainMin, GainMax),
        Q = Math.Max(QStep, Swift.Rounded(b.Q / QStep) * QStep),
        Frequency = EqFitter.RoundedFrequency(b.Frequency).Clamp(20, 20_000),
    };

    /// <summary>Rounds the bells to the hardware's steps and puts each into a band slot.</summary>
    internal static EqBand?[] Placed(IReadOnlyList<EqBand> bands)
    {
        var active = bands.Where(b => b.IsEnabled && (!b.Type.UsesGain() || Math.Abs(b.Gain) >= GainStepDb / 2)).ToList();
        foreach (var other in active)
            if (other.Type != FilterType.Peak) throw new Adi2PresetException(Adi2PresetError.UnsupportedFilter, other.Type);
        var rounded = active.Select(Rounded).ToList();
        var placement = EqFitter.Assign(rounded.Select(b => (b.Frequency, b.Q)).ToArray(), Slots)
                        ?? throw new Adi2PresetException(Adi2PresetError.DoesNotFit);
        var result = new EqBand?[Slots.Count];
        for (int i = 0; i < placement.Length; i++) result[placement[i]] = rounded[i];
        return result;
    }

    /// <summary>At most one low shelf for Bass and one high shelf for Treble (the same on both channels).</summary>
    internal static (EqBand? Bass, EqBand? Treble) ToneShelves(IReadOnlyList<EqBand> bands)
    {
        EqBand? Pick(FilterType type, (double Min, double Max) range)
        {
            var found = bands.Where(b => b.IsEnabled && b.Type == type && Math.Abs(b.Gain) >= GainStepDb / 2).ToList();
            if (found.Count == 0) return null;
            var first = found[0];
            static bool Same(EqBand a, EqBand b) => a.Frequency == b.Frequency && a.Gain == b.Gain && a.Q == b.Q;
            if (!found.All(b => Same(b, first)) || !range.Contains(Swift.Rounded(first.Frequency)))
                throw new Adi2PresetException(Adi2PresetError.UnsupportedFilter, type);
            var shelf = Rounded(first);
            shelf.Frequency = type == FilterType.HighShelf ? Swift.Rounded(first.Frequency / 100) * 100 : Swift.Rounded(first.Frequency);
            shelf.Q = shelf.Q.Clamp(ShelfQRange);
            return shelf;
        }
        var bass = Pick(FilterType.LowShelf, BassRange);
        var treble = Pick(FilterType.HighShelf, TrebleRange);
        return (bass, treble);
    }

    /// <summary>Printable ASCII, no XML specials, at most 20 characters.</summary>
    internal static string PresetName(string name)
    {
        var allowed = new StringBuilder();
        foreach (char ch in name)
            if (ch >= 32 && ch < 128 && "<>&\"".IndexOf(ch) < 0) allowed.Append(ch);
        var s = Swift.TrimWhitespaces(allowed.ToString());
        if (s.Length == 0) s = "Tapline";
        return s.Length > 20 ? s[..20] : s;
    }

    // MARK: - Import

    public sealed class Parsed
    {
        public string? Name { get; init; }
        /// <summary>Bands as Tapline sees them: per-channel when Dual EQ was on.</summary>
        public List<EqBand> Bands { get; init; } = [];
        public bool IsEnabled { get; init; }
    }

    /// <summary>Reads a preset file. Bass / Treble become shelves when the preset loads them.</summary>
    /// <exception cref="Adi2PresetException">Not a preset file (<see cref="Adi2PresetError.NotAPreset"/>).</exception>
    public static Parsed Parse(string text)
    {
        var sections = Sections(text);
        if (!sections.TryGetValue("EQPreset1Left", out var left)) throw new Adi2PresetException(Adi2PresetError.NotAPreset);
        sections.TryGetValue("EQPreset1Right", out var right);
        bool dual = Get(left, "DualEQ", 0) >= 0.5 && right is not null;

        static double Get(Dictionary<string, double> values, string key, double fallback) =>
            values.TryGetValue(key, out var v) ? v : fallback;

        List<EqBand> Bands(Dictionary<string, double> values, EqChannels channels)
        {
            var result = new List<EqBand>();
            for (int n = 1; n <= 5; n++)
            {
                if (!values.TryGetValue($"EQBand{n}Gain", out var gain) || !values.TryGetValue($"EQBand{n}Freq", out var freq)
                    || !values.TryGetValue($"EQBand{n}Q", out var q)) continue;
                int code = (int)Swift.Rounded(Get(values, $"EQBand{n}Type", 0));
                var type = (n, code) switch
                {
                    (1, 1) => FilterType.LowShelf,
                    (5, 1) => FilterType.HighShelf,
                    (1, >= 2) => FilterType.HighPass,
                    (5, >= 2) => FilterType.LowPass,
                    _ => FilterType.Peak,
                };
                if (type.UsesGain() && Math.Abs(gain) < 0.05) continue;
                result.Add(new EqBand(type, freq, type.UsesGain() ? gain : 0, q, channels: channels).Clamped());
            }
            return result;
        }

        List<EqBand> bands = dual ? [.. Bands(left, EqChannels.Left), .. Bands(right!, EqChannels.Right)] : Bands(left, EqChannels.Both);
        if (Get(left, "Load BT w. EQ", 0) >= 0.5 && Get(left, "BassTrebleEn", 1) >= 0.5)
        {
            if (left.TryGetValue("EQBassGain", out var g) && Math.Abs(g) >= 0.05)
                bands.Insert(0, new EqBand(FilterType.LowShelf, Get(left, "EQBassFreq", 85), g, Get(left, "EQBassQ", 0.9)).Clamped());
            if (left.TryGetValue("EQTrebleGain", out var t) && Math.Abs(t) >= 0.05)
                bands.Add(new EqBand(FilterType.HighShelf, Get(left, "EQTrebleFreq", 6500), t, Get(left, "EQTrebleQ", 0.7)).Clamped());
        }
        return new Parsed { Name = Names(text).FirstOrDefault(), Bands = bands, IsEnabled = Get(left, "EQEnable", 1) >= 0.5 };
    }

    /// <summary>Numeric <c>&lt;val e=".." v=".."/&gt;</c> entries per section.</summary>
    private static Dictionary<string, Dictionary<string, double>> Sections(string text)
    {
        var result = new Dictionary<string, Dictionary<string, double>>();
        string? current = null;
        foreach (var raw in Swift.Lines(text))
        {
            var line = Swift.TrimWhitespaces(raw);
            if (line.StartsWith("</", StringComparison.Ordinal)) { current = null; continue; }
            if (line.StartsWith("<val ", StringComparison.Ordinal))
            {
                if (current is not null && Entry(line) is { } entry
                    && Swift.TryParseDouble(entry.Value.Trim(',', ' '), out double number) && double.IsFinite(number))
                {
                    result[current][entry.Key] = number;
                }
                continue;
            }
            if (line.StartsWith('<') && line.EndsWith('>') && !line.EndsWith("/>", StringComparison.Ordinal))
            {
                current = line[1..^1];
                result.TryAdd(current, []);
            }
        }
        return result;
    }

    private static IEnumerable<string> Names(string text)
    {
        foreach (var raw in Swift.Lines(text))
        {
            var line = Swift.TrimWhitespaces(raw);
            if (Entry(line) is { } entry && entry.Key.StartsWith("EQPresetName", StringComparison.Ordinal)) yield return entry.Value;
        }
    }

    private static (string Key, string Value)? Entry(string line)
    {
        int e = line.IndexOf("e=\"", StringComparison.Ordinal);
        if (e < 0) return null;
        int eStart = e + 3;
        int eEnd = line.IndexOf('"', eStart);
        if (eEnd < 0) return null;
        int v = line.IndexOf("v=\"", eEnd, StringComparison.Ordinal);
        if (v < 0) return null;
        int vStart = v + 3;
        int vEnd = line.IndexOf('"', vStart);
        if (vEnd < 0) return null;
        return (line[eStart..eEnd], line[vStart..vEnd]);
    }
}
