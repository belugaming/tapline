using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Tapline.UI;

static class Theme
{
    // The instrument panel behind graphs is always dark, like hardware.
    public static readonly Brush Panel = Rgb(0.055, 0.063, 0.078);
    public static readonly Brush PanelRaised = Rgb(0.086, 0.098, 0.118);
    public static readonly Brush PanelStroke = White(0.07);
    public static readonly Brush GridMajor = White(0.10);
    public static readonly Brush GridMinor = White(0.045);
    public static readonly Brush GridLabel = White(0.42);
    public static readonly Brush Curve = Brushes.White;
    /// The right channel's curve when left and right differ.
    public static readonly Brush CurveRight = Rgb(0.55, 0.76, 1.0);
    public static readonly Color SpectrumColor = C(0.25, 0.78, 0.95);
    public static readonly Brush SpectrumPost = new SolidColorBrush(SpectrumColor).Frozen();
    public static readonly Color AccentColor = C(0.18, 0.80, 0.62);
    public static readonly Brush Accent = new SolidColorBrush(AccentColor).Frozen();
    public static readonly Color WarningColor = C(1.0, 0.72, 0.28);
    public static readonly Brush Warning = new SolidColorBrush(WarningColor).Frozen();
    public static readonly Color DangerColor = C(1.0, 0.36, 0.36);
    public static readonly Brush Danger = new SolidColorBrush(DangerColor).Frozen();

    // Window chrome, close to the macOS dark appearance.
    public static readonly Brush WindowBackground = Hex(0x1F1F1F);
    public static readonly Brush SidebarBackground = Hex(0x191919);
    public static readonly Brush CardBackground = Hex(0x282828);
    public static readonly Brush CardStroke = White(0.08);
    public static readonly Brush ControlFill = White(0.06);
    public static readonly Brush ControlFillStrong = White(0.10);
    public static readonly Brush Text = Hex(0xF2F2F2);
    public static readonly Brush Secondary = White(0.60);
    public static readonly Brush Tertiary = White(0.36);
    public static readonly Brush Separator = White(0.10);

    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
    public static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");
    public static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
    public static readonly Typeface BoldFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    /// Distinct hues of similar lightness so no band looks more important than another.
    private static readonly Color[] BandPalette =
    [
        C(1.00, 0.45, 0.42),
        C(1.00, 0.71, 0.30),
        C(0.70, 0.88, 0.36),
        C(0.25, 0.84, 0.70),
        C(0.33, 0.73, 1.00),
        C(0.53, 0.58, 1.00),
        C(0.79, 0.51, 1.00),
        C(1.00, 0.50, 0.78),
    ];

    public static Color BandColor(int index) => BandPalette[index % BandPalette.Length];

    public static Brush Brush(Color c, double opacity = 1) =>
        new SolidColorBrush(Color.FromArgb((byte)Math.Round(c.A * opacity), c.R, c.G, c.B)).Frozen();

    public static Color C(double r, double g, double b) =>
        Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));

    private static Brush Rgb(double r, double g, double b) => new SolidColorBrush(C(r, g, b)).Frozen();
    private static Brush White(double opacity) => new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), 255, 255, 255)).Frozen();
    private static Brush Hex(int rgb) => new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)).Frozen();

    public static T Frozen<T>(this T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    public static FormattedText Label(string text, double size, Brush brush, Typeface? face = null, double dpi = 1) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face ?? LabelFace, size, brush, dpi);
}

/// Segoe Fluent Icons / MDL2 code points.
static class Glyphs
{
    public const string Equalizer = "";
    public const string Volume = "";
    public const string Volume3 = "";
    public const string Mute = "";
    public const string Speakers = "";
    public const string Headphones = "";
    public const string Microphone = "";
    public const string Audio = "";
    public const string Settings = "";
    public const string Wand = "";
    public const string Lightbulb = "";
    public const string Info = "";
    public const string Warning = "";
    public const string Check = "";
    /// A check mark in a circle.
    public const string Completed = "";
    public const string ErrorBadge = "";
    public const string Add = "";
    public const string Delete = "";
    public const string Undo = "";
    public const string Redo = "";
    public const string More = "";
    public const string Search = "";
    public const string Power = "";
    public const string Edit = "";
    public const string Save = "";
    public const string Import = "";
    public const string Export = "";
    public const string OpenExternal = "";
    public const string Refresh = "";
    public const string Folder = "";
    public const string Monitor = "";
    public const string Laptop = "";
    public const string Library = "";
    public const string Diagnostic = "";
    public const string Shield = "";
    public const string Keyboard = "";
    public const string Play = "";
    public const string ChevronDown = "";
    public const string Spectrum = "";
    public const string Download = "";
    public const string Copy = "";
    public const string Swap = "";
    public const string Dial = "";
    public const string Pointer = "";
}
