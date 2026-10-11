using System.Windows;
using System.Windows.Media;
using Tapline.Core;

namespace Tapline.UI.Controls;

/// Small hand-drawn glyph of a filter shape, used in pickers and chips.
sealed class FilterGlyph : FrameworkElement
{
    private FilterType type;

    public FilterGlyph(FilterType type, double width = 18, double height = 11, double lineWidth = 1.5)
    {
        this.type = type;
        Width = width;
        Height = height;
        LineWidth = lineWidth;
        IsHitTestVisible = false;
    }

    public double LineWidth { get; }
    public Brush Stroke { get; set; } = Theme.Secondary;

    public FilterType Type
    {
        get => type;
        set
        {
            if (type == value) return;
            type = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(1, 2, Math.Max(1, ActualWidth - 2), Math.Max(1, ActualHeight - 4));
        dc.DrawGeometry(null, new Pen(Stroke, LineWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Path(type, rect));
    }

    public static Geometry Path(FilterType type, Rect r)
    {
        double w = r.Width, h = r.Height, x = r.X, y = r.Y;
        var mid = y + h * 0.55;
        Point P(double px, double py) => new(px, py);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            switch (type)
            {
                case FilterType.Peak:
                    c.BeginFigure(P(x, mid), false, false);
                    c.BezierTo(P(x + w * 0.3, mid), P(x + w * 0.38, y + h * 0.1), P(x + w * 0.5, y + h * 0.1), true, true);
                    c.BezierTo(P(x + w * 0.62, y + h * 0.1), P(x + w * 0.7, mid), P(x + w, mid), true, true);
                    break;
                case FilterType.LowShelf:
                    c.BeginFigure(P(x, y + h * 0.2), false, false);
                    c.LineTo(P(x + w * 0.3, y + h * 0.2), true, true);
                    c.BezierTo(P(x + w * 0.5, y + h * 0.2), P(x + w * 0.5, mid), P(x + w * 0.7, mid), true, true);
                    c.LineTo(P(x + w, mid), true, true);
                    break;
                case FilterType.HighShelf:
                    c.BeginFigure(P(x, mid), false, false);
                    c.LineTo(P(x + w * 0.3, mid), true, true);
                    c.BezierTo(P(x + w * 0.5, mid), P(x + w * 0.5, y + h * 0.2), P(x + w * 0.7, y + h * 0.2), true, true);
                    c.LineTo(P(x + w, y + h * 0.2), true, true);
                    break;
                case FilterType.LowPass:
                    c.BeginFigure(P(x, y + h * 0.35), false, false);
                    c.LineTo(P(x + w * 0.45, y + h * 0.35), true, true);
                    c.BezierTo(P(x + w * 0.65, y + h * 0.3), P(x + w * 0.75, y + h * 0.8), P(x + w * 0.9, y + h), true, true);
                    break;
                case FilterType.HighPass:
                    c.BeginFigure(P(x + w * 0.1, y + h), false, false);
                    c.BezierTo(P(x + w * 0.25, y + h * 0.8), P(x + w * 0.35, y + h * 0.3), P(x + w * 0.55, y + h * 0.35), true, true);
                    c.LineTo(P(x + w, y + h * 0.35), true, true);
                    break;
                case FilterType.Notch:
                    c.BeginFigure(P(x, y + h * 0.3), false, false);
                    c.LineTo(P(x + w * 0.4, y + h * 0.3), true, true);
                    c.LineTo(P(x + w * 0.5, y + h), true, true);
                    c.LineTo(P(x + w * 0.6, y + h * 0.3), true, true);
                    c.LineTo(P(x + w, y + h * 0.3), true, true);
                    break;
                case FilterType.BandPass:
                    c.BeginFigure(P(x, y + h), false, false);
                    c.BezierTo(P(x + w * 0.3, y + h), P(x + w * 0.35, y + h * 0.2), P(x + w * 0.5, y + h * 0.2), true, true);
                    c.BezierTo(P(x + w * 0.65, y + h * 0.2), P(x + w * 0.7, y + h), P(x + w, y + h), true, true);
                    break;
            }
        }
        g.Freeze();
        return g;
    }
}

/// Small, non-interactive frequency response preview.
sealed class CurveThumbnail : FrameworkElement
{
    private static readonly double[] Frequencies = ResponseCurve.LogFrequencies(120);
    private double[] response = new double[Frequencies.Length];

    public CurveThumbnail()
    {
        IsHitTestVisible = false;
    }

    public double Range { get; set; } = 12;
    public Color Color { get; set; } = Theme.AccentColor;
    public bool ShowGrid { get; set; } = true;

    public void Set(IEnumerable<EqBand> bands, ToneSettings tone, Color? color = null)
    {
        var all = bands.Where(b => b.IsEnabled).Concat(tone.Bands.Where(b => b.IsEnabled)).ToList();
        response = ResponseCurve.ResponseDb(all, Frequencies, 48000);
        if (color is Color c) Color = c;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var midY = h / 2;
        if (ShowGrid) dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)), 0.5), new Point(0, midY), new Point(w, midY));
        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var c = line.Open())
        using (var f = fill.Open())
        {
            for (var i = 0; i < response.Length; i++)
            {
                var x = w * i / (response.Length - 1);
                var y = midY - Math.Clamp(response[i], -Range, Range) / Range * (h / 2 - 2);
                if (i == 0)
                {
                    c.BeginFigure(new Point(x, y), false, false);
                    f.BeginFigure(new Point(0, midY), true, true);
                    f.LineTo(new Point(x, y), false, false);
                }
                else
                {
                    c.LineTo(new Point(x, y), true, true);
                    f.LineTo(new Point(x, y), false, false);
                }
            }
            f.LineTo(new Point(w, midY), false, false);
        }
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(46, Color.R, Color.G, Color.B)), null, fill);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color), 1.5) { LineJoin = PenLineJoin.Round }, line);
    }
}

/// Measurement, target and predicted response on one log-frequency chart.
sealed class MeasurementChart : FrameworkElement
{
    private double[] frequencies = [];
    private double[] measurement = [];
    private double[] target = [];
    private double[] predicted = [];

    public MeasurementChart()
    {
        IsHitTestVisible = false;
    }

    public void Set(IReadOnlyList<double> frequencies, IReadOnlyList<double> measurement, IReadOnlyList<double> target, IReadOnlyList<double> predicted)
    {
        this.frequencies = frequencies.ToArray();
        this.measurement = measurement.ToArray();
        this.target = target.ToArray();
        this.predicted = predicted.ToArray();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(42, 6, Math.Max(1, ActualWidth - 42 - 18), Math.Max(1, ActualHeight - 6 - 22));
        dc.DrawRoundedRectangle(Theme.Panel, new Pen(Theme.PanelStroke, 1), new Rect(0, 0, ActualWidth, ActualHeight), 8, 8);
        if (frequencies.Length < 2) return;
        var all = measurement.Concat(target).Concat(predicted).Where(double.IsFinite).ToList();
        var lo = Math.Floor((all.Count > 0 ? all.Min() : -10) / 5) * 5 - 5;
        var hi = Math.Ceiling((all.Count > 0 ? all.Max() : 10) / 5) * 5 + 5;
        double X(double f) => rect.Left + Math.Log(f / 20) / Math.Log(1000) * rect.Width;
        double Y(double db) => rect.Bottom - (db - lo) / (hi - lo) * rect.Height;
        var grid = new Pen(Theme.GridMinor, 0.5);
        foreach (var f in new[] { 20.0, 50, 100, 200, 500, 1000, 2000, 5000, 10_000, 20_000 })
        {
            var px = X(f);
            dc.DrawLine(grid, new Point(px, rect.Top), new Point(px, rect.Bottom));
            var label = Theme.Label(Format.GridFrequency(f), 9, Theme.GridLabel);
            dc.DrawText(label, new Point(px - label.Width / 2, rect.Bottom + 4));
        }
        for (var db = lo; db <= hi; db += 10)
        {
            var py = Y(db);
            dc.DrawLine(grid, new Point(rect.Left, py), new Point(rect.Right, py));
            var label = Theme.Label(((int)db).ToString(), 9, Theme.GridLabel);
            dc.DrawText(label, new Point(rect.Left - 6 - label.Width, py - label.Height / 2));
        }
        dc.PushClip(new RectangleGeometry(rect));
        Draw(dc, measurement, X, Y, new Pen(Brushes.Gray, 1.2));
        Draw(dc, target, X, Y, new Pen(new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)), 1.2) { DashStyle = new DashStyle([3.5, 2.5], 0) });
        Draw(dc, predicted, X, Y, new Pen(Theme.Accent, 1.8) { LineJoin = PenLineJoin.Round });
        dc.Pop();
    }

    private void Draw(DrawingContext dc, double[] values, Func<double, double> x, Func<double, double> y, Pen pen)
    {
        if (values.Length != frequencies.Length) return;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            for (var i = 0; i < values.Length; i++)
            {
                var p = new Point(x(frequencies[i]), y(values[i]));
                if (i == 0) c.BeginFigure(p, false, false);
                else c.LineTo(p, true, true);
            }
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

/// A short line sample for chart legends.
sealed class LegendSwatch : FrameworkElement
{
    private readonly Brush brush;
    private readonly bool dashed;

    public LegendSwatch(Brush brush, bool dashed = false)
    {
        this.brush = brush;
        this.dashed = dashed;
        Width = 16;
        Height = 6;
        VerticalAlignment = VerticalAlignment.Center;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var pen = new Pen(brush, 2);
        if (dashed) pen.DashStyle = new DashStyle([1.5, 1], 0);
        dc.DrawLine(pen, new Point(0, ActualHeight / 2), new Point(ActualWidth, ActualHeight / 2));
    }
}
