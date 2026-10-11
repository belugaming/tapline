using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Tapline.UI.Controls;

/// A segmented control (one of a few options), drawn like the macOS one.
sealed class Segmented : Border
{
    private readonly List<Border> segments = [];
    private int selected = -1;

    public Segmented(IReadOnlyList<string> labels, double fontSize = 13)
    {
        Background = Theme.ControlFill;
        CornerRadius = new CornerRadius(7);
        Padding = new Thickness(2);
        BorderBrush = Theme.Separator;
        BorderThickness = new Thickness(1);
        var grid = new Grid();
        for (var i = 0; i < labels.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var index = i;
            var segment = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 3, 10, 4),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = labels[i],
                    FontSize = fontSize,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = Theme.Text,
                },
            };
            segment.MouseLeftButtonUp += (_, e) =>
            {
                if (!IsEnabled) return;
                e.Handled = true;
                if (selected == index) return;
                SelectedIndex = index;
                SelectionChanged?.Invoke(index);
            };
            Grid.SetColumn(segment, i);
            grid.Children.Add(segment);
            segments.Add(segment);
        }
        Child = grid;
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.45;
    }

    public event Action<int>? SelectionChanged;

    public int SelectedIndex
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value;
            for (var i = 0; i < segments.Count; i++)
            {
                segments[i].Background = i == selected ? Theme.ControlFillStrong : Brushes.Transparent;
                if (segments[i].Child is TextBlock t) t.FontWeight = i == selected ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
    }
}
