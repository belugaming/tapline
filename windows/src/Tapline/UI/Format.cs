using System.Globalization;
using Tapline.Core;
using Tapline.Infrastructure;

namespace Tapline.UI;

static class Format
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Frequency(double hz, bool unit = true)
    {
        string s;
        if (hz >= 10_000) s = (hz / 1000).ToString("F1", Inv) + "k";
        else if (hz >= 1000) s = (hz / 1000).ToString("F2", Inv) + "k";
        else if (hz >= 100) s = hz.ToString("F0", Inv);
        else s = hz.ToString("F1", Inv);
        return unit ? s + " Hz" : s;
    }

    /// Whole hertz below 1 kHz.
    public static string Hz(double f) => f < 1000 ? $"{Math.Round(f).ToString(Inv)} Hz" : Frequency(f);

    public static string GridFrequency(double hz) => hz >= 1000 ? Printf.Format("%gk", [hz / 1000]) : Printf.Format("%g", [hz]);

    public static string Gain(double db)
    {
        var v = Math.Abs(db) < 0.05 ? 0 : db;
        return Printf.Format("%+.1f dB", [v]);
    }

    public static string Q(double q) => q.ToString("F2", Inv);

    public static string Percent(double v) => Printf.Format("%.0f%%", [v * 100]);

    public static string MeterDb(float linear)
    {
        var db = 20 * Math.Log10(Math.Max(linear, 1e-12));
        return db < -90 ? "-∞" : db.ToString("F1", Inv);
    }

    public static double Round(double v, int places)
    {
        var p = Math.Pow(10, places);
        return Math.Round(v * p) / p;
    }

    public static string TypeName(FilterType t) => L.T(t switch
    {
        FilterType.Peak => "Bell",
        FilterType.LowShelf => "Low Shelf",
        FilterType.HighShelf => "High Shelf",
        FilterType.LowPass => "Low Pass",
        FilterType.HighPass => "High Pass",
        FilterType.Notch => "Notch",
        FilterType.BandPass => "Band Pass",
        _ => t.ToString(),
    });

    public static string ChannelsTitle(EqChannels c) => L.T(c switch
    {
        EqChannels.Left => "Left",
        EqChannels.Right => "Right",
        _ => "L + R",
    });
}
