using System.Windows;
using System.Windows.Controls;

namespace Tapline.UI;

/// A stack panel with spacing and "fill" children, closer to SwiftUI's stacks than WPF's StackPanel.
/// Children with `Stack.Fill` share the space left over along the main axis. Across the main axis,
/// a child keeps its own alignment unless it is Stretch, in which case `CrossAlignment` applies.
sealed class Stack : Panel
{
    public static readonly DependencyProperty FillProperty =
        DependencyProperty.RegisterAttached("Fill", typeof(bool), typeof(Stack), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetFill(UIElement e) => (bool)e.GetValue(FillProperty);
    public static void SetFill(UIElement e, bool value) => e.SetValue(FillProperty, value);

    public Orientation Orientation { get; init; } = Orientation.Vertical;
    public double Spacing { get; init; }
    public CrossAlign CrossAlignment { get; init; } = CrossAlign.Stretch;

    public enum CrossAlign { Start, Center, End, Stretch }

    private bool Horizontal => Orientation == Orientation.Horizontal;

    private static double Along(Size s, bool h) => h ? s.Width : s.Height;
    private static double Cross(Size s, bool h) => h ? s.Height : s.Width;
    private static Size Make(double main, double cross, bool h) => h ? new Size(main, cross) : new Size(cross, main);

    protected override Size MeasureOverride(Size available)
    {
        var h = Horizontal;
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        double used = 0, cross = 0;
        var fills = new List<UIElement>();
        foreach (var child in visible)
        {
            if (GetFill(child)) { fills.Add(child); continue; }
            child.Measure(Make(double.PositiveInfinity, Cross(available, h), h));
            used += Along(child.DesiredSize, h);
            cross = Math.Max(cross, Cross(child.DesiredSize, h));
        }
        used += Spacing * Math.Max(0, visible.Count - 1);
        var availableMain = Along(available, h);
        var remaining = double.IsInfinity(availableMain) ? double.PositiveInfinity : Math.Max(0, availableMain - used);
        foreach (var child in fills)
        {
            var share = double.IsInfinity(remaining) ? double.PositiveInfinity : remaining / fills.Count;
            child.Measure(Make(share, Cross(available, h), h));
            used += double.IsInfinity(remaining) ? Along(child.DesiredSize, h) : share;
            cross = Math.Max(cross, Cross(child.DesiredSize, h));
        }
        if (fills.Count > 0 && !double.IsInfinity(availableMain)) used = Math.Max(used, availableMain);
        return Make(used, cross, h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var h = Horizontal;
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        double fixedMain = Spacing * Math.Max(0, visible.Count - 1);
        var fillCount = 0;
        foreach (var child in visible)
        {
            if (GetFill(child)) fillCount++;
            else fixedMain += Along(child.DesiredSize, h);
        }
        var share = fillCount > 0 ? Math.Max(0, Along(final, h) - fixedMain) / fillCount : 0;
        var crossSize = Cross(final, h);
        double offset = 0;
        foreach (var child in visible)
        {
            var main = GetFill(child) ? share : Along(child.DesiredSize, h);
            var desiredCross = Math.Min(Cross(child.DesiredSize, h), crossSize);
            var align = Alignment(child, h);
            var (pos, size) = align switch
            {
                CrossAlign.Start => (0.0, desiredCross),
                CrossAlign.Center => ((crossSize - desiredCross) / 2, desiredCross),
                CrossAlign.End => (crossSize - desiredCross, desiredCross),
                _ => (0.0, crossSize),
            };
            child.Arrange(h ? new Rect(offset, pos, main, size) : new Rect(pos, offset, size, main));
            offset += main + Spacing;
        }
        return final;
    }

    private CrossAlign Alignment(UIElement child, bool h)
    {
        if (child is not FrameworkElement fe) return CrossAlignment;
        if (h)
        {
            return fe.VerticalAlignment switch
            {
                VerticalAlignment.Top => CrossAlign.Start,
                VerticalAlignment.Center => CrossAlign.Center,
                VerticalAlignment.Bottom => CrossAlign.End,
                _ => CrossAlignment,
            };
        }
        return fe.HorizontalAlignment switch
        {
            HorizontalAlignment.Left => CrossAlign.Start,
            HorizontalAlignment.Center => CrossAlign.Center,
            HorizontalAlignment.Right => CrossAlign.End,
            _ => CrossAlignment,
        };
    }
}

/// Lays children out in rows, wrapping like SwiftUI's adaptive LazyVGrid: as many columns of at
/// least `MinItemWidth` as fit, all the same width.
sealed class AdaptiveGrid : Panel
{
    public double MinItemWidth { get; init; } = 160;
    public double Spacing { get; init; } = 8;

    private int Columns(double width) =>
        double.IsInfinity(width) ? Math.Max(1, InternalChildren.Count) : Math.Max(1, (int)((width + Spacing) / (MinItemWidth + Spacing)));

    protected override Size MeasureOverride(Size available)
    {
        var cols = Columns(available.Width);
        var itemWidth = double.IsInfinity(available.Width) ? MinItemWidth : (available.Width - Spacing * (cols - 1)) / cols;
        double height = 0, rowHeight = 0;
        var i = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (++i % cols == 0) { height += rowHeight + Spacing; rowHeight = 0; }
        }
        if (i % cols != 0) height += rowHeight; else if (i > 0) height -= Spacing;
        var width = double.IsInfinity(available.Width) ? cols * MinItemWidth + (cols - 1) * Spacing : available.Width;
        return new Size(width, Math.Max(0, height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        var cols = Columns(final.Width);
        var itemWidth = (final.Width - Spacing * (cols - 1)) / cols;
        var children = InternalChildren.Cast<UIElement>().ToList();
        double y = 0;
        for (var row = 0; row * cols < children.Count; row++)
        {
            var items = children.Skip(row * cols).Take(cols).ToList();
            var rowHeight = items.Max(c => c.DesiredSize.Height);
            for (var c = 0; c < items.Count; c++)
                items[c].Arrange(new Rect(c * (itemWidth + Spacing), y, itemWidth, rowHeight));
            y += rowHeight + Spacing;
        }
        return final;
    }
}
