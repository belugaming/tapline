using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Tapline.Infrastructure;

/// UI strings. Keys are the English text, as on macOS: Simplified Chinese comes from the
/// macOS Localizable.strings plus a file of Windows-only strings.
static partial class L
{
    /// Follows the Windows display language; TAPLINE_LANG=zh or en overrides it (for snapshots).
    public static bool IsChinese { get; } = (Environment.GetEnvironmentVariable("TAPLINE_LANG") ?? CultureInfo.CurrentUICulture.Name)
        .StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> Table = IsChinese ? Load() : new();

    /// Translates `key`.
    public static string T(string key) => Table.TryGetValue(key, out var v) ? v : key;

    /// Translates `key` and fills in printf-style placeholders (%@, %d, %lld, %.1f, %+.1f, %1$@…).
    public static string F(string key, params object?[] args) => Printf.Format(T(key), args);

    private static Dictionary<string, string> Load()
    {
        var table = new Dictionary<string, string>();
        var assembly = Assembly.GetExecutingAssembly();
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("zh-Hans", StringComparison.Ordinal) && n.EndsWith(".strings", StringComparison.Ordinal))
            .OrderBy(n => n == "zh-Hans.strings" ? 0 : 1);
        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream == null) continue;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            foreach (Match m in Entry().Matches(reader.ReadToEnd()))
                table[Unescape(m.Groups[1].Value)] = Unescape(m.Groups[2].Value);
        }
        return table;
    }

    private static string Unescape(string s) =>
        s.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");

    [GeneratedRegex(@"^\s*""((?:[^""\\]|\\.)*)""\s*=\s*""((?:[^""\\]|\\.)*)""\s*;", RegexOptions.Multiline)]
    private static partial Regex Entry();
}

/// A small printf for the format strings shared with macOS (String(format:) semantics, POSIX locale).
static class Printf
{
    public static string Format(string format, object?[] args)
    {
        var sb = new StringBuilder(format.Length + 16);
        var next = 0;
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c != '%') { sb.Append(c); continue; }
            if (i + 1 < format.Length && format[i + 1] == '%') { sb.Append('%'); i++; continue; }

            var j = i + 1;
            var position = -1;
            var k = j;
            while (k < format.Length && char.IsAsciiDigit(format[k])) k++;
            if (k < format.Length && k > j && format[k] == '$')
            {
                position = int.Parse(format.AsSpan(j, k - j), CultureInfo.InvariantCulture) - 1;
                j = k + 1;
            }
            bool plus = false, left = false, zero = false, space = false;
            while (j < format.Length && "+-0 #".Contains(format[j]))
            {
                switch (format[j])
                {
                    case '+': plus = true; break;
                    case '-': left = true; break;
                    case '0': zero = true; break;
                    case ' ': space = true; break;
                }
                j++;
            }
            var width = 0;
            while (j < format.Length && char.IsAsciiDigit(format[j])) width = width * 10 + (format[j++] - '0');
            var precision = -1;
            if (j < format.Length && format[j] == '.')
            {
                j++;
                precision = 0;
                while (j < format.Length && char.IsAsciiDigit(format[j])) precision = precision * 10 + (format[j++] - '0');
            }
            while (j < format.Length && "lhqzjt".Contains(format[j])) j++;
            if (j >= format.Length) { sb.Append(format, i, format.Length - i); break; }

            var conversion = format[j];
            var arg = position >= 0 ? (position < args.Length ? args[position] : null) : (next < args.Length ? args[next++] : null);
            var text = conversion switch
            {
                '@' or 's' => arg?.ToString() ?? "",
                'd' or 'i' or 'u' => Signed(Convert.ToInt64(arg ?? 0, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), plus, space),
                'f' or 'F' => Signed(Convert.ToDouble(arg ?? 0, CultureInfo.InvariantCulture).ToString("F" + (precision < 0 ? 6 : precision), CultureInfo.InvariantCulture), plus, space),
                'e' or 'E' => Signed(Convert.ToDouble(arg ?? 0, CultureInfo.InvariantCulture).ToString((conversion == 'e' ? "e" : "E") + (precision < 0 ? 6 : precision), CultureInfo.InvariantCulture), plus, space),
                'g' or 'G' => Signed(General(Convert.ToDouble(arg ?? 0, CultureInfo.InvariantCulture), precision), plus, space),
                'x' => Convert.ToInt64(arg ?? 0, CultureInfo.InvariantCulture).ToString("x", CultureInfo.InvariantCulture),
                'X' => Convert.ToInt64(arg ?? 0, CultureInfo.InvariantCulture).ToString("X", CultureInfo.InvariantCulture),
                'c' => arg?.ToString() ?? "",
                _ => format.Substring(i, j - i + 1),
            };
            if (text.Length < width)
            {
                if (left) text = text.PadRight(width);
                else if (zero && conversion is not ('@' or 's')) text = ZeroPad(text, width);
                else text = text.PadLeft(width);
            }
            sb.Append(text);
            i = j;
        }
        return sb.ToString();
    }

    private static string Signed(string s, bool plus, bool space)
    {
        if (s.StartsWith('-')) return s;
        return plus ? "+" + s : (space ? " " + s : s);
    }

    private static string ZeroPad(string s, int width)
    {
        var sign = s.Length > 0 && (s[0] == '-' || s[0] == '+') ? s[..1] : "";
        return sign + s[sign.Length..].PadLeft(width - sign.Length, '0');
    }

    /// %g: `precision` significant digits (default 6), trailing zeros removed.
    private static string General(double v, int precision)
    {
        var p = precision < 0 ? 6 : Math.Max(precision, 1);
        if (v == 0) return "0";
        var exponent = (int)Math.Floor(Math.Log10(Math.Abs(v)));
        if (exponent < -4 || exponent >= p)
            return v.ToString("0." + new string('#', p - 1) + "e+00", CultureInfo.InvariantCulture);
        var decimals = Math.Max(0, p - 1 - exponent);
        var s = Math.Round(v, Math.Min(decimals, 15)).ToString("F" + decimals, CultureInfo.InvariantCulture);
        if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
        return s;
    }
}
