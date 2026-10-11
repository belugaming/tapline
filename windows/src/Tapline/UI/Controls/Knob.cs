using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Tapline.UI.Controls;

/// Rotary control. Drag up/down to change, Alt for fine steps, double-click to reset,
/// click the value to type a number, mouse wheel to nudge.
sealed class Knob : ContentControl
{
    private const double Sweep = 270;

    private readonly Dial dial;
    private readonly TextBlock valueText;
    private readonly TextBox editor;
    private double value;

    public Knob(string title, double min, double max, double defaultValue, bool logarithmic = false, bool bipolar = false,
                Color? color = null, double size = 54, Func<double, string>? format = null, Func<string, double?>? parse = null)
    {
        Min = min;
        Max = max;
        DefaultValue = defaultValue;
        Logarithmic = logarithmic;
        Bipolar = bipolar;
        Color = color ?? Theme.AccentColor;
        Format = format ?? (v => v.ToString("F1", CultureInfo.InvariantCulture));
        Parse = parse ?? DefaultParse;

        var label = new TextBlock
        {
            Text = title.ToUpper(CultureInfo.CurrentCulture),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Secondary,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        dial = new Dial(this) { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
        valueText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = Theme.Text,
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = Cursors.IBeam,
        };
        valueText.MouseLeftButtonUp += (_, _) => BeginEdit();
        editor = new TextBox
        {
            FontSize = 11,
            Width = size + 14,
            MinHeight = 0,
            Padding = new Thickness(2, 0, 2, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndEdit(); e.Handled = true; }
        };
        editor.LostKeyboardFocus += (_, _) => { if (editor.Visibility == Visibility.Visible) Commit(); };
        var valueHost = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        valueHost.Children.Add(valueText);
        valueHost.Children.Add(editor);

        var stack = new Stack { Spacing = 6 };
        stack.Children.Add(label);
        stack.Children.Add(dial);
        stack.Children.Add(valueHost);
        Content = stack;
        Focusable = false;
        MinWidth = size + 12;
        UpdateText();
    }

    public double Min { get; }
    public double Max { get; }
    public double DefaultValue { get; set; }
    public bool Logarithmic { get; }
    public bool Bipolar { get; }
    public Color Color { get; set; }
    public Func<double, string> Format { get; }
    public Func<string, double?> Parse { get; }

    /// Raised while the user changes the value.
    public event Action<double>? ValueChanged;
    /// Raised when a gesture begins (true) and ends (false), for undo checkpoints.
    public event Action<bool>? Editing;

    public double Value
    {
        get => value;
        set
        {
            if (this.value == value) return;
            this.value = value;
            UpdateText();
            dial.InvalidateVisual();
        }
    }

    private void UpdateText() => valueText.Text = Format(value);

    private double Normalized(double v) => Logarithmic
        ? Math.Log(v / Min) / Math.Log(Max / Min)
        : (v - Min) / (Max - Min);

    private double Denormalized(double n)
    {
        n = Math.Clamp(n, 0, 1);
        return Logarithmic ? Min * Math.Pow(Max / Min, n) : Min + n * (Max - Min);
    }

    private void Set(double v)
    {
        v = Math.Clamp(v, Min, Max);
        Value = v;
        ValueChanged?.Invoke(v);
    }

    private void BeginEdit()
    {
        if (!IsEnabled) return;
        editor.Text = Format(value);
        valueText.Visibility = Visibility.Hidden;
        editor.Visibility = Visibility.Visible;
        editor.Focus();
        editor.SelectAll();
    }

    private void EndEdit()
    {
        editor.Visibility = Visibility.Collapsed;
        valueText.Visibility = Visibility.Visible;
    }

    private void Commit()
    {
        var parsed = Parse(editor.Text);
        EndEdit();
        if (parsed is not double v) return;
        Editing?.Invoke(true);
        Set(v);
        Editing?.Invoke(false);
    }

    private static double? DefaultParse(string s)
    {
        var filtered = new string(s.Replace(',', '.').Where(c => "0123456789.-".Contains(c)).ToArray());
        return double.TryParse(filtered, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// Accepts "1.2k", "1200", "1,2 kHz".
    public static double? ParseFrequency(string s)
    {
        var lower = s.ToLowerInvariant().Replace(',', '.');
        var k = lower.Contains('k');
        var filtered = new string(lower.Where(c => "0123456789.".Contains(c)).ToArray());
        if (!double.TryParse(filtered, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
        return k ? v * 1000 : v;
    }

    private sealed class Dial : FrameworkElement
    {
        private readonly Knob knob;
        private Point? dragStart;
        private double startNormalized;

        public Dial(Knob knob)
        {
            this.knob = knob;
            Cursor = Cursors.SizeNS;
            Focusable = true;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (!knob.IsEnabled) return;
            Focus();
            if (e.ClickCount == 2)
            {
                knob.Editing?.Invoke(true);
                knob.Set(knob.DefaultValue);
                knob.Editing?.Invoke(false);
                e.Handled = true;
                return;
            }
            dragStart = e.GetPosition(this);
            startNormalized = knob.Normalized(knob.value);
            CaptureMouse();
            knob.Editing?.Invoke(true);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragStart is not Point start || !IsMouseCaptured) return;
            var p = e.GetPosition(this);
            var fine = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            var delta = (-(p.Y - start.Y) + (p.X - start.X) * 0.25) / (fine ? 900 : 180);
            knob.Set(knob.Denormalized(startNormalized + delta));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (dragStart == null) return;
            dragStart = null;
            ReleaseMouseCapture();
            knob.Editing?.Invoke(false);
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!knob.IsEnabled) return;
            var step = (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.004 : 0.02) * Math.Sign(e.Delta);
            knob.Editing?.Invoke(true);
            knob.Set(knob.Denormalized(knob.Normalized(knob.value) + step));
            knob.Editing?.Invoke(false);
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            var step = e.Key switch { Key.Up or Key.Right => 0.02, Key.Down or Key.Left => -0.02, _ => 0 };
            if (step == 0) return;
            knob.Editing?.Invoke(true);
            knob.Set(knob.Denormalized(knob.Normalized(knob.value) + step));
            knob.Editing?.Invoke(false);
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var size = Math.Min(ActualWidth, ActualHeight);
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            var radius = size / 2 - 2;
            dc.PushOpacity(knob.IsEnabled ? 1 : 0.4);
            var start = 90 + (360 - Sweep) / 2;
            dc.DrawGeometry(null, new Pen(Theme.ControlFillStrong, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(center, radius, start, start + Sweep));
            var n = Math.Clamp(knob.Normalized(knob.value), 0, 1);
            var origin = knob.Bipolar ? Math.Clamp(knob.Normalized(knob.Min <= 0 && knob.Max >= 0 ? 0 : knob.Min), 0, 1) : 0;
            var from = Math.Min(origin, n);
            var to = Math.Max(origin, n);
            if (to - from > 0.001)
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(knob.Color), 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(center, radius, start + from * Sweep, start + to * Sweep));
            var inner = radius - 7;
            dc.DrawEllipse(Theme.CardBackground, new Pen(Theme.Separator, 1), center, inner, inner);
            var angle = (start + n * Sweep) * Math.PI / 180;
            var a = new Point(center.X + Math.Cos(angle) * inner * 0.25, center.Y + Math.Sin(angle) * inner * 0.25);
            var b = new Point(center.X + Math.Cos(angle) * inner * 0.8, center.Y + Math.Sin(angle) * inner * 0.8);
            dc.DrawLine(new Pen(Theme.Text, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, a, b);
            if (IsKeyboardFocused) dc.DrawEllipse(null, new Pen(Theme.Secondary, 1), center, radius + 1.5, radius + 1.5);
            dc.Pop();
        }

        private static Geometry Arc(Point c, double r, double fromDeg, double toDeg)
        {
            var a0 = fromDeg * Math.PI / 180;
            var a1 = toDeg * Math.PI / 180;
            var p0 = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
            var p1 = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(p0, false, false);
                ctx.ArcTo(p1, new Size(r, r), 0, toDeg - fromDeg > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }
    }
}
