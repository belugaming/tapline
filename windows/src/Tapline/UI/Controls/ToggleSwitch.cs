using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Tapline.UI.Controls;

/// An on/off switch like the macOS one.
sealed class ToggleSwitch : FrameworkElement
{
    private bool isOn;

    public ToggleSwitch(double width = 38, double height = 20)
    {
        Width = width;
        Height = height;
        Focusable = true;
        Cursor = Cursors.Hand;
        IsEnabledChanged += (_, _) => InvalidateVisual();
    }

    public event Action<bool>? Toggled;

    public bool IsOn
    {
        get => isOn;
        set
        {
            if (isOn == value) return;
            isOn = value;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Flip();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Space or Key.Enter)
        {
            Flip();
            e.Handled = true;
        }
    }

    private void Flip()
    {
        if (!IsEnabled) return;
        IsOn = !IsOn;
        Toggled?.Invoke(IsOn);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var opacity = IsEnabled ? 1.0 : 0.4;
        dc.PushOpacity(opacity);
        var track = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(isOn ? Theme.Accent : Theme.ControlFillStrong, isOn ? null : new Pen(Theme.Separator, 1), track, h / 2, h / 2);
        var knob = h - 4;
        var x = isOn ? w - 2 - knob : 2;
        dc.DrawEllipse(Brushes.White, null, new Point(x + knob / 2, h / 2), knob / 2, knob / 2);
        if (IsKeyboardFocused)
            dc.DrawRoundedRectangle(null, new Pen(Brushes.White, 1), new Rect(-2, -2, w + 4, h + 4), h / 2 + 2, h / 2 + 2);
        dc.Pop();
    }
}
