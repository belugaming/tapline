namespace Tapline.Core;

/// <summary>Decibel conversions.</summary>
public static class Db
{
    public static double ToLinear(double db) => Math.Pow(10, db / 20);

    public static double FromLinear(double linear) => 20 * Math.Log10(Math.Max(linear, 1e-12));
}

/// <summary>Helpers for <c>(Min, Max)</c> ranges, the counterpart of Swift's <c>ClosedRange</c>.</summary>
public static class RangeExtensions
{
    /// <summary><c>min(max(value, Min), Max)</c>, like Swift's <c>clamped(to:)</c>.</summary>
    public static double Clamp(this double value, (double Min, double Max) range) =>
        Math.Min(Math.Max(value, range.Min), range.Max);

    /// <summary><c>min(max(value, min), max)</c>; unlike <see cref="Math.Clamp(double, double, double)"/> it never throws.</summary>
    public static double Clamp(this double value, double min, double max) => Math.Min(Math.Max(value, min), max);

    public static bool Contains(this (double Min, double Max) range, double value) =>
        value >= range.Min && value <= range.Max;
}

/// <summary>Swift semantics the port depends on.</summary>
internal static class Swift
{
    /// <summary>Swift's <c>rounded()</c>: to nearest, ties away from zero.</summary>
    public static double Rounded(double x) => Math.Round(x, MidpointRounding.AwayFromZero);

    /// <summary><c>String(format: "%.Nf")</c>; .NET's invariant "F" formatting matches printf.</summary>
    public static string Format(double x, int decimals) =>
        x.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary><c>Double(String)</c>: no surrounding whitespace, no thousands separators.</summary>
    public static bool TryParseDouble(string s, out double value) =>
        double.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint
                           | System.Globalization.NumberStyles.AllowExponent,
                        System.Globalization.CultureInfo.InvariantCulture, out value);

    public static double? ParseDouble(string s) => TryParseDouble(s, out var v) ? v : null;

    /// <summary><c>CharacterSet.whitespaces</c>: tab and Unicode space separators (not newlines).</summary>
    public static bool IsWhitespace(char c) =>
        c == '\t' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.SpaceSeparator;

    /// <summary><c>trimmingCharacters(in: .whitespaces)</c>.</summary>
    public static string TrimWhitespaces(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && IsWhitespace(s[start])) start++;
        while (end > start && IsWhitespace(s[end - 1])) end--;
        return s.Substring(start, end - start);
    }

    /// <summary><c>Character.isNewline</c>.</summary>
    public static bool IsNewline(char c) =>
        c is '\n' or '\r' or '\u000B' or '\u000C' or '\u0085' or '\u2028' or '\u2029';

    /// <summary><c>split(whereSeparator: \.isNewline)</c>: non-empty lines.</summary>
    public static IEnumerable<string> Lines(string text)
    {
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && !IsNewline(text[i])) continue;
            if (i > start) yield return text.Substring(start, i - start);
            start = i + 1;
        }
    }

    /// <summary>Splits on any of <paramref name="separators"/>, omitting empty pieces.</summary>
    public static List<string> Split(string s, params char[] separators) =>
        [.. s.Split(separators, StringSplitOptions.RemoveEmptyEntries)];
}
