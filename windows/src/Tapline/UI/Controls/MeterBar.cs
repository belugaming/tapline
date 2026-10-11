using System.Windows;
using System.Windows.Media;

namespace Tapline.UI.Controls;

/// Peak/RMS bar with a peak-hold tick. Peak is drawn faint, RMS solid.
sealed class MeterBar : FrameworkElement
{
    public const double FloorDB = -60;

    private static readonly LinearGradientBrush Vertical = Gradient(new Point(0, 1), new Point(0, 0));
    private static readonly LinearGradientBrush HorizontalGradient = Gradient(new Point(0, 0), new Point(1, 0));
    private static readonly Brush Track = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)).Frozen();
    private static readonly Brush HoldBrush = new SolidColorBrush(Color.FromArgb(217, 255, 255, 255)).Frozen();

    public bool Horizontal { get; init; }
    public float Peak { get; private set; }
    public float Rms { get; private set; }
    public float Hold { get; private set; }

    public void Set(float peak, float rms, float hold)
    {
        if (peak == Peak && rms == Rms && hold == Hold) return;
        Peak = peak;
        Rms = rms;
        Hold = hold;
        InvalidateVisual();
    }

    public static double Position(float linear)
    {
        var db = 20 * Math.Log10(Math.Max(linear, 1e-9));
        return Math.Clamp((db - FloorDB) / -FloorDB, 0, 1);
    }

    private static LinearGradientBrush Gradient(Point start, Point end)
    {
        var b = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        b.GradientStops.Add(new GradientStop(Theme.AccentColor, 0));
        b.GradientStops.Add(new GradientStop(Theme.AccentColor, 0.75));
        b.GradientStops.Add(new GradientStop(Theme.WarningColor, 0.9));
        b.GradientStops.Add(new GradientStop(Theme.DangerColor, 1));
        b.Freeze();
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var full = new Rect(0, 0, w, h);
        dc.PushClip(new RectangleGeometry(full, 2, 2));
        dc.DrawRectangle(Track, null, full);
        var gradient = Horizontal ? HorizontalGradient : Vertical;
        var length = Horizontal ? w : h;
        Rect Part(double n) => Horizontal ? new Rect(0, 0, n * length, h) : new Rect(0, h - n * length, w, n * length);
        dc.PushOpacity(0.55);
        dc.PushClip(new RectangleGeometry(Part(Position(Peak))));
        dc.DrawRectangle(gradient, null, full);
        dc.Pop();
        dc.Pop();
        dc.PushClip(new RectangleGeometry(Part(Position(Rms))));
        dc.DrawRectangle(gradient, null, full);
        dc.Pop();
        if (Hold > 0.0005)
        {
            var p = Position(Hold) * length;
            dc.DrawRectangle(HoldBrush, null, Horizontal ? new Rect(Math.Max(0, p - 1.5), 0, 1.5, h) : new Rect(0, Math.Min(h - 1.5, h - p), w, 1.5));
        }
        dc.Pop();
    }
}
