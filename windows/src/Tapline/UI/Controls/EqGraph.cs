using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tapline.Core;
using Tapline.Infrastructure;
using Tapline.Model;

namespace Tapline.UI.Controls;

/// The interactive EQ graph: grid, live spectrum, response curve and draggable band nodes.
/// Drag a node to move it, scroll for Q, Alt-click to delete, double-click empty space to add,
/// arrow keys to nudge the selected band (Alt for fine steps), Delete to remove it.
sealed class EqGraph : FrameworkElement
{
    private const double MinFrequency = 20;
    private const double MaxFrequency = 20_000;
    private const double SpectrumFloor = -90;
    private const double SpectrumCeiling = 6;
    private static readonly double[] MinorFrequencies = [30, 40, 60, 70, 80, 90, 300, 400, 600, 700, 800, 900, 3000, 4000, 6000, 7000, 8000, 9000];
    private static readonly double[] MajorFrequencies = [20, 50, 100, 200, 500, 1000, 2000, 5000, 10_000, 20_000];
    private static readonly Thickness Insets = new(36, 12, 12, 22);

    private readonly AppModel model;
    private Point? hover;
    private Guid? hoveredBand;
    private Guid? dragBand;
    private Point dragOrigin;
    private Point dragNodeStart;
    private DateTime lastWheel = DateTime.MinValue;

    // Cached curves.
    private DspSettings? cachedSettings;
    private Guid? cachedSelected;
    private Size cachedSize;
    private double[] curveFrequencies = [];
    private double[][] totals = [];
    private double[] selectedCurve = [];

    public EqGraph(AppModel model)
    {
        this.model = model;
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
    }

    public bool Interactive { get; init; } = true;

    public double DbRange
    {
        get => dbRange;
        set
        {
            if (dbRange == value) return;
            dbRange = value;
            InvalidateVisual();
        }
    }
    private double dbRange = 12;

    // MARK: - Mapping

    private Rect Plot => new(Insets.Left, Insets.Top,
                             Math.Max(1, ActualWidth - Insets.Left - Insets.Right),
                             Math.Max(1, ActualHeight - Insets.Top - Insets.Bottom));

    private double X(double f)
    {
        var p = Plot;
        return p.Left + Math.Log10(f / MinFrequency) / Math.Log10(MaxFrequency / MinFrequency) * p.Width;
    }

    private double FrequencyAt(double x)
    {
        var p = Plot;
        var n = Math.Clamp((x - p.Left) / p.Width, 0, 1);
        return MinFrequency * Math.Pow(MaxFrequency / MinFrequency, n);
    }

    private double Y(double db)
    {
        var p = Plot;
        return p.Top + p.Height / 2 - db / dbRange * p.Height / 2;
    }

    private double DbAt(double y)
    {
        var p = Plot;
        return (p.Top + p.Height / 2 - y) / (p.Height / 2) * dbRange;
    }

    private double SpectrumY(float level)
    {
        var p = Plot;
        var n = Math.Clamp((level - SpectrumFloor) / (SpectrumCeiling - SpectrumFloor), 0, 1);
        return p.Bottom - n * p.Height;
    }

    private Point NodePosition(EqBand band) =>
        new(X(band.Frequency), band.Type.UsesGain() ? Y(Math.Clamp(band.Gain, -dbRange, dbRange)) : Y(0));

    private Guid? NodeAt(Point p)
    {
        Guid? best = null;
        var bestDistance = 11.0;
        foreach (var band in model.Settings.Bands)
        {
            var d = (NodePosition(band) - p).Length;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = band.Id;
            }
        }
        return best;
    }

    // MARK: - Rendering

    protected override void OnRender(DrawingContext dc)
    {
        var plot = Plot;
        dc.DrawRoundedRectangle(Theme.Panel, new Pen(Theme.PanelStroke, 1), new Rect(0, 0, ActualWidth, ActualHeight), 12, 12);
        DrawGrid(dc, plot);
        DrawSpectrum(dc, plot);
        DrawCurves(dc, plot);
        if (Interactive)
        {
            DrawNodes(dc);
            DrawReadout(dc, plot);
        }
    }

    private void DrawGrid(DrawingContext dc, Rect plot)
    {
        var minor = new Pen(Theme.GridMinor, 0.5);
        var major = new Pen(Theme.GridMajor, 0.5);
        foreach (var f in MinorFrequencies)
            dc.DrawLine(minor, new Point(X(f), plot.Top), new Point(X(f), plot.Bottom));
        foreach (var f in MajorFrequencies)
        {
            var x = X(f);
            dc.DrawLine(major, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var label = Theme.Label(Format.GridFrequency(f), 9.5, Theme.GridLabel);
            var lx = f == 20 ? x : (f == 20_000 ? x - label.Width : x - label.Width / 2);
            dc.DrawText(label, new Point(lx, plot.Bottom + 5));
        }
        var step = dbRange <= 6 ? 1 : (dbRange <= 12 ? 3 : 6);
        var zeroPen = new Pen(new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)), 1);
        for (var db = -dbRange; db <= dbRange + 0.001; db += step)
        {
            var y = Y(db);
            var isZero = Math.Abs(db) < 0.001;
            dc.DrawLine(isZero ? zeroPen : minor, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Theme.Label(db > 0 ? $"+{(int)db}" : $"{(int)db}", 9.5, Theme.GridLabel);
            dc.DrawText(label, new Point(plot.Left - 6 - label.Width, y - label.Height / 2));
        }
    }

    private void DrawSpectrum(DrawingContext dc, Rect plot)
    {
        var levels = model.Meters.Spectrum;
        var freqs = model.Meters.SpectrumFrequencies;
        if (levels.Length != freqs.Length || levels.Length < 2) return;
        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var c = line.Open())
        using (var f = fill.Open())
        {
            f.BeginFigure(new Point(X(freqs[0]), plot.Bottom), true, true);
            for (var i = 0; i < levels.Length; i++)
            {
                var p = new Point(X(freqs[i]), SpectrumY(levels[i]));
                if (i == 0) c.BeginFigure(p, false, false);
                else c.LineTo(p, true, true);
                f.LineTo(p, false, false);
            }
            f.LineTo(new Point(X(freqs[^1]), plot.Bottom), false, false);
        }
        var gradient = new LinearGradientBrush(
            Color.FromArgb(87, Theme.SpectrumColor.R, Theme.SpectrumColor.G, Theme.SpectrumColor.B),
            Color.FromArgb(8, Theme.SpectrumColor.R, Theme.SpectrumColor.G, Theme.SpectrumColor.B),
            new Point(0, 0), new Point(0, 1));
        dc.PushClip(new RectangleGeometry(plot));
        dc.DrawGeometry(gradient, null, fill);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(191, Theme.SpectrumColor.R, Theme.SpectrumColor.G, Theme.SpectrumColor.B)), 1) { LineJoin = PenLineJoin.Round }, line);
        dc.Pop();
    }

    private void UpdateCurves(Rect plot)
    {
        var settings = model.Settings;
        var size = new Size(ActualWidth, ActualHeight);
        if (ReferenceEquals(settings, cachedSettings) && cachedSelected == model.SelectedBandId && size == cachedSize && totals.Length > 0) return;
        cachedSettings = settings;
        cachedSelected = model.SelectedBandId;
        cachedSize = size;
        var count = Math.Max(2, (int)(plot.Width / 2));
        curveFrequencies = Enumerable.Range(0, count).Select(i => FrequencyAt(plot.Left + plot.Width * i / (count - 1))).ToArray();
        var rate = model.SampleRate;
        if (settings.HasChannelSpecificBands)
        {
            totals =
            [
                ResponseCurve.ResponseDb(settings.ActiveBands, curveFrequencies, rate, EqChannels.Left),
                ResponseCurve.ResponseDb(settings.ActiveBands, curveFrequencies, rate, EqChannels.Right),
            ];
        }
        else
        {
            totals = [ResponseCurve.ResponseDb(settings.ActiveBands, curveFrequencies, rate)];
        }
        selectedCurve = model.SelectedBand is EqBand b ? ResponseCurve.ResponseDb(b, curveFrequencies, rate) : [];
    }

    private Geometry CurveGeometry(double[] response, bool closed)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            var zero = Y(0);
            for (var i = 0; i < response.Length; i++)
            {
                var p = new Point(X(curveFrequencies[i]), Y(Math.Clamp(response[i], -dbRange * 1.5, dbRange * 1.5)));
                if (i == 0)
                {
                    if (closed)
                    {
                        c.BeginFigure(new Point(p.X, zero), true, true);
                        c.LineTo(p, false, false);
                    }
                    else
                    {
                        c.BeginFigure(p, false, false);
                    }
                }
                else
                {
                    c.LineTo(p, !closed, true);
                }
            }
            if (closed && response.Length > 0) c.LineTo(new Point(X(curveFrequencies[^1]), zero), false, false);
        }
        g.Freeze();
        return g;
    }

    private void DrawCurves(DrawingContext dc, Rect plot)
    {
        UpdateCurves(plot);
        var settings = model.Settings;
        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - 2, plot.Width, plot.Height + 4)));

        // Selected band in its own colour.
        if (model.SelectedBand is EqBand band && selectedCurve.Length == curveFrequencies.Length)
        {
            var index = settings.Bands.FindIndex(b => b.Id == band.Id);
            var color = Theme.BandColor(index);
            dc.DrawGeometry(Theme.Brush(color, band.IsEnabled ? 0.22 : 0.08), null, CurveGeometry(selectedCurve, true));
            var pen = new Pen(Theme.Brush(color, band.IsEnabled ? 0.85 : 0.35), 1.2);
            if (!band.IsEnabled) pen.DashStyle = new DashStyle([3, 3], 0);
            dc.DrawGeometry(null, pen, CurveGeometry(selectedCurve, false));
        }

        // Total response, including tone macros: one curve, or one per channel.
        for (var i = 0; i < totals.Length; i++)
        {
            var color = totals.Length > 1 && i == 1 ? ((SolidColorBrush)Theme.CurveRight).Color : Colors.White;
            var line = CurveGeometry(totals[i], false);
            if (settings.EqEnabled)
            {
                if (totals.Length == 1) dc.DrawGeometry(Theme.Brush(Colors.White, 0.05), null, CurveGeometry(totals[i], true));
                dc.DrawGeometry(null, new Pen(Theme.Brush(color, 0.18), 6) { LineJoin = PenLineJoin.Round }, line);
                dc.DrawGeometry(null, new Pen(Theme.Brush(color, 0.35), 3) { LineJoin = PenLineJoin.Round }, line);
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1.8) { LineJoin = PenLineJoin.Round }, line);
            }
            else
            {
                dc.DrawGeometry(null, new Pen(Theme.Brush(color, 0.35), 1.5) { DashStyle = new DashStyle([3.3, 2.7], 0) }, line);
            }
            if (totals.Length > 1 && totals[i].Length > 0)
            {
                var label = Theme.Label(i == 0 ? "L" : "R", 10, new SolidColorBrush(color), Theme.BoldFace);
                dc.DrawText(label, new Point(plot.Right - 12, Y(Math.Clamp(totals[i][^1], -dbRange, dbRange)) - 16));
            }
        }
        dc.Pop();
    }

    private void DrawNodes(DrawingContext dc)
    {
        var bands = model.Settings.Bands;
        for (var index = 0; index < bands.Count; index++)
        {
            var band = bands[index];
            var selected = model.SelectedBandId == band.Id;
            var hot = selected || hoveredBand == band.Id;
            var color = Theme.BandColor(index);
            var r = hot ? 9.0 : 7.5;
            var p = NodePosition(band);
            if (selected) dc.DrawEllipse(Theme.Brush(color, 0.35), null, p, r + 4, r + 4);
            var fill = band.IsEnabled ? new SolidColorBrush(color) : Theme.PanelRaised;
            var stroke = selected ? new Pen(Brushes.White, 2) : new Pen(band.IsEnabled ? Theme.Brush(Colors.Black, 0.35) : new SolidColorBrush(color), 1.2);
            dc.DrawEllipse(fill, stroke, p, r, r);
            var number = Theme.Label((index + 1).ToString(), 8.5, band.IsEnabled ? Theme.Brush(Colors.Black, 0.75) : new SolidColorBrush(color), Theme.BoldFace);
            dc.DrawText(number, new Point(p.X - number.Width / 2, p.Y - number.Height / 2));
            if (hot) DrawNodeLabel(dc, band, p, color);
        }
    }

    private void DrawNodeLabel(DrawingContext dc, EqBand band, Point p, Color color)
    {
        var plot = Plot;
        var text = band.Type.UsesGain()
            ? $"{Format.Frequency(band.Frequency)}  {Format.Gain(band.Gain)}  Q {Format.Q(band.Q)}"
            : $"{Format.Frequency(band.Frequency)}  Q {Format.Q(band.Q)}";
        var label = Theme.Label(text, 10, Brushes.White, Theme.BoldFace);
        var w = label.Width + 12;
        var h = label.Height + 6;
        var x = Math.Clamp(p.X - w / 2, plot.Left + 2, Math.Max(plot.Left + 2, plot.Right - w - 2));
        var y = p.Y > plot.Top + 30 ? p.Y - 22 - h / 2 : p.Y + 24 - h / 2;
        dc.DrawRoundedRectangle(Theme.Brush(color, 0.85), null, new Rect(x, y, w, h), 4, 4);
        dc.DrawText(label, new Point(x + 6, y + 3));
    }

    private void DrawReadout(DrawingContext dc, Rect plot)
    {
        if (hover is not Point p || !plot.Contains(p) || hoveredBand != null || dragBand != null) return;
        var label = Theme.Label($"{Format.Frequency(FrequencyAt(p.X))}   {Format.Gain(DbAt(p.Y))}", 10, Theme.GridLabel);
        var rect = new Rect(plot.Left + 6, plot.Top + 4, label.Width + 12, label.Height + 6);
        dc.DrawRoundedRectangle(Theme.Brush(((SolidColorBrush)Theme.PanelRaised).Color, 0.9), null, rect, 4, 4);
        dc.DrawText(label, new Point(rect.Left + 6, rect.Top + 3));
    }

    // MARK: - Interaction

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!Interactive) return;
        var p = e.GetPosition(this);
        hover = p;
        if (dragBand is Guid id && IsMouseCaptured)
        {
            var fine = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.2 : 1;
            var x = dragNodeStart.X + (p.X - dragOrigin.X) * fine;
            var y = dragNodeStart.Y + (p.Y - dragOrigin.Y) * fine;
            var f = FrequencyAt(x);
            var gain = Format.Round(DbAt(y), 1);
            model.UpdateBand(id, b => b with { Frequency = f, Gain = b.Type.UsesGain() ? gain : b.Gain }, undoable: false);
        }
        else
        {
            hoveredBand = NodeAt(p);
            Cursor = hoveredBand != null ? Cursors.Hand : Cursors.Cross;
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        hover = null;
        if (dragBand == null) hoveredBand = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!Interactive) return;
        Focus();
        var p = e.GetPosition(this);
        var hit = NodeAt(p);
        if (hit is Guid id)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                model.RemoveBand(id);
                e.Handled = true;
                return;
            }
            model.SelectedBandId = id;
            model.Checkpoint();
            dragBand = id;
            dragOrigin = p;
            dragNodeStart = NodePosition(model.Settings.Bands.First(b => b.Id == id));
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2 && Plot.Contains(p))
        {
            model.AddBand(FrequencyAt(p.X), Format.Round(DbAt(p.Y), 1));
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (dragBand == null) return;
        dragBand = null;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (!Interactive || NodeAt(e.GetPosition(this)) is not Guid id) return;
        var band = model.Settings.Bands.First(b => b.Id == id);
        model.SelectedBandId = id;
        var menu = new ContextMenu { PlacementTarget = this };
        menu.Items.Add(Ui.Submenu(L.T("Filter Type"), Enum.GetValues<FilterType>().Select(t =>
            (MenuItem?)Ui.Item(Format.TypeName(t), () => model.UpdateBand(id, b => b with { Type = t }), isChecked: band.Type == t))));
        menu.Items.Add(Ui.Item(L.T(band.IsEnabled ? "Disable Band" : "Enable Band"), () => model.UpdateBand(id, b => b with { IsEnabled = !b.IsEnabled })));
        menu.Items.Add(Ui.Item(L.T("Reset Gain"), () => model.UpdateBand(id, b => b with { Gain = 0 })));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item(L.T("Delete Band"), () => model.RemoveBand(id)));
        menu.IsOpen = true;
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!Interactive || (hoveredBand ?? model.SelectedBandId) is not Guid id) return;
        if ((DateTime.UtcNow - lastWheel).TotalMilliseconds > 600) model.Checkpoint();
        lastWheel = DateTime.UtcNow;
        var factor = Math.Exp(e.Delta / 120.0 * 0.1);
        model.UpdateBand(id, b => b with { Q = b.Q * factor }, undoable: false);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Interactive || model.SelectedBandId is not Guid id) return;
        var fine = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var gainStep = fine ? 0.1 : 0.5;
        var freqFactor = fine ? 1.01 : 1.06;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Up: model.UpdateBand(id, b => b with { Gain = b.Gain + gainStep }); break;
            case Key.Down: model.UpdateBand(id, b => b with { Gain = b.Gain - gainStep }); break;
            case Key.Right: model.UpdateBand(id, b => b with { Frequency = b.Frequency * freqFactor }); break;
            case Key.Left: model.UpdateBand(id, b => b with { Frequency = b.Frequency / freqFactor }); break;
            case Key.Delete or Key.Back: model.RemoveBand(id); break;
            default: return;
        }
        e.Handled = true;
    }
}
