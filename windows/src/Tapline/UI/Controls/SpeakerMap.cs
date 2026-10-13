using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tapline.Audio;
using Tapline.Core;
using Tapline.Infrastructure;

namespace Tapline.UI.Controls;

/// The speakers of a layout seen from above, the listener in the middle and the screen at the
/// top: ear-level speakers on the outer ring, height speakers on the inner one, subwoofers in the
/// front corners. A speaker can be highlighted (playing now), outlined (asked about) and marked as
/// checked; clicking one raises `SpeakerClicked`. As on macOS (SpeakerMap.swift).
sealed class SpeakerMap : FrameworkElement
{
    private const double HeightRing = 0.52;
    /// Segoe Fluent Icons "CheckMark" and "Cancel".
    private const string CheckGlyph = "", CrossGlyph = "";

    private static readonly Brush ScreenBrush = new SolidColorBrush(Color.FromArgb(71, 255, 255, 255)).Frozen();
    private static readonly Pen EarRing = new Pen(new SolidColorBrush(Color.FromArgb(31, 255, 255, 255)), 1) { DashStyle = new DashStyle([3, 3], 0) }.Frozen();
    private static readonly Pen TopRing = new Pen(new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)), 1) { DashStyle = new DashStyle([2, 3], 0) }.Frozen();
    private static readonly Brush ListenerBrush = new SolidColorBrush(Color.FromArgb(77, 255, 255, 255)).Frozen();
    private static readonly Brush ChipFill = new SolidColorBrush(Color.FromArgb(41, 255, 255, 255)).Frozen();
    private static readonly Brush HeightChipFill = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)).Frozen();
    private static readonly Brush ChipText = new SolidColorBrush(Color.FromArgb(217, 255, 255, 255)).Frozen();
    private static readonly Typeface ChipFace = new(Theme.UiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface IconFace = new(Theme.IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private readonly SpeakerLayout layout;
    private int? playing;
    private int? asking;
    private IReadOnlyDictionary<int, bool> marks = new Dictionary<int, bool>();
    private int? hovered;

    public SpeakerMap(SpeakerLayout layout)
    {
        this.layout = layout;
        Cursor = Cursors.Arrow;
        ToolTipService.SetInitialShowDelay(this, 300);
    }

    /// A speaker was clicked: its output channel.
    public event Action<int>? SpeakerClicked;

    public void Set(int? playing, int? asking, IReadOnlyDictionary<int, bool> marks)
    {
        if (this.playing == playing && this.asking == asking && this.marks.Count == marks.Count && this.marks.All(m => marks.TryGetValue(m.Key, out var v) && v == m.Value))
            return;
        this.playing = playing;
        this.asking = asking;
        this.marks = new Dictionary<int, bool>(marks);
        InvalidateVisual();
    }

    private Point Center => new(ActualWidth / 2, ActualHeight * 0.53);
    private (double X, double Y) Radii => (ActualWidth * 0.40, ActualHeight * 0.38);

    private Point PointOf(int channel)
    {
        var p = layout.Positions[channel];
        // Subwoofers sit in the front corners of the room.
        if (p.IsLfe()) return new Point(p == SpeakerPosition.Lfe ? 26 : ActualWidth - 26, 24);
        var c = Center;
        var (rx, ry) = Radii;
        var scale = p.IsHeight() ? HeightRing : 1;
        var a = p.Azimuth() * Math.PI / 180;
        return new Point(c.X + rx * scale * Math.Sin(a), c.Y - ry * scale * Math.Cos(a));
    }

    private FormattedText Label(int channel, Brush brush) =>
        Theme.Label(layout.Positions[channel].Abbreviation(), 9, brush, ChipFace, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private Rect ChipRect(int channel)
    {
        var text = Label(channel, ChipText);
        var w = Math.Max(26, text.Width + 8);
        const double h = 16;
        var p = PointOf(channel);
        return new Rect(p.X - w / 2, p.Y - h / 2, w, h);
    }

    private int? ChipAt(Point point)
    {
        foreach (var channel in layout.SpeakerChannels)
        {
            var r = ChipRect(channel);
            r.Inflate(3, 3);
            if (r.Contains(point)) return channel;
        }
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var chip = ChipAt(e.GetPosition(this));
        Cursor = chip != null && SpeakerClicked != null ? Cursors.Hand : Cursors.Arrow;
        if (chip == hovered) return;
        hovered = chip;
        ToolTip = chip is { } c ? $"{RoomAudio.SpeakerName(layout.Positions[c])} · {L.F("channel %lld", c + 1)}" : null;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        hovered = null;
        ToolTip = null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (ChipAt(e.GetPosition(this)) is { } channel) SpeakerClicked?.Invoke(channel);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRoundedRectangle(Theme.Panel, new Pen(Theme.PanelStroke, 1), new Rect(0, 0, w, h), 8, 8);
        var c = Center;
        var (rx, ry) = Radii;
        // The screen, the ear-level ring and the height ring.
        dc.DrawRoundedRectangle(ScreenBrush, null, new Rect(c.X - rx * 0.45, 5, rx * 0.9, 3), 1.5, 1.5);
        dc.DrawEllipse(null, EarRing, c, rx, ry);
        if (layout.Positions.Any(p => p.IsHeight())) dc.DrawEllipse(null, TopRing, c, rx * HeightRing, ry * HeightRing);
        // The listener.
        dc.DrawEllipse(null, new Pen(ListenerBrush, 1), c, 9, 9);
        dc.DrawEllipse(ListenerBrush, null, new Point(c.X, c.Y - 9), 2, 2);

        foreach (var channel in layout.SpeakerChannels)
        {
            var p = layout.Positions[channel];
            var isPlaying = playing == channel;
            var r = ChipRect(channel);
            var radius = p.IsHeight() ? 8 : 3;
            dc.DrawRoundedRectangle(isPlaying ? Theme.Accent : p.IsHeight() ? HeightChipFill : ChipFill,
                                    asking == channel ? new Pen(Theme.Accent, 1.5) : null, r, radius, radius);
            var text = Label(channel, isPlaying ? Brushes.Black : ChipText);
            dc.DrawText(text, new Point(r.X + (r.Width - text.Width) / 2, r.Y + (r.Height - text.Height) / 2));
            if (marks.TryGetValue(channel, out var ok))
            {
                var badge = new Point(r.Right, r.Top);
                dc.DrawEllipse(Theme.Panel, null, badge, 6, 6);
                dc.DrawEllipse(ok ? Theme.Accent : Theme.Danger, null, badge, 5, 5);
                var glyph = Theme.Label(ok ? CheckGlyph : CrossGlyph, 6, Brushes.Black, IconFace, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(glyph, new Point(badge.X - glyph.Width / 2, badge.Y - glyph.Height / 2));
            }
        }
    }
}
