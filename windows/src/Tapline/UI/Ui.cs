using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Tapline.UI.Controls;

namespace Tapline.UI;

/// Builds pages in code, SwiftUI style: controls read the model through getters, and
/// `Refresh()` re-reads them after the model changed. `Dynamic` regions rebuild when their key changes.
sealed class UiScope
{
    private readonly List<Action> refreshers = [];

    public void OnRefresh(Action refresh) => refreshers.Add(refresh);

    public void Refresh()
    {
        foreach (var r in refreshers.ToArray()) r();
    }

    /// Runs `update` now and on every refresh.
    public T Bind<T>(T element, Action<T> update)
    {
        update(element);
        OnRefresh(() => update(element));
        return element;
    }

    public TextBlock Text(Func<string> get, double size = 13, FontWeight? weight = null, Brush? brush = null, bool wrap = false) =>
        Bind(Ui.Text("", size, weight, brush, wrap), t => { var v = get(); if (t.Text != v) t.Text = v; });

    /// Content that is rebuilt (with a fresh inner scope) whenever `key` changes, and refreshed otherwise.
    public ContentControl Dynamic(Func<object?> key, Func<UiScope, UIElement?> build)
    {
        var host = new ContentControl { Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        object? lastKey = new();
        UiScope? inner = null;
        void Update()
        {
            var k = key();
            if (!Equals(k, lastKey))
            {
                lastKey = k;
                inner = new UiScope();
                host.Content = build(inner);
            }
            else
            {
                inner?.Refresh();
            }
        }
        Update();
        OnRefresh(Update);
        return host;
    }

    public ToggleSwitch Switch(Func<bool> get, Action<bool> set, string? tip = null)
    {
        var s = new ToggleSwitch { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = tip };
        s.Toggled += set;
        return Bind(s, x => x.IsOn = get());
    }

    /// A switch with a label in front, like `Toggle("Enabled", …).toggleStyle(.switch)`.
    public FrameworkElement LabeledSwitch(string label, Func<bool> get, Action<bool> set, string? tip = null) =>
        Ui.HStack(10, Ui.Text(label), Switch(get, set, tip));

    public CheckBox Check(string label, Func<bool> get, Action<bool> set, string? caption = null, string? tip = null)
    {
        var updating = false;
        object content = caption == null
            ? label
            : Ui.VStack(1, Ui.Text(label), Ui.Caption(caption));
        // Fluent's CheckBox has a MinWidth of 120, which clips it in narrow panels.
        var box = new CheckBox { Content = content, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = tip, VerticalContentAlignment = VerticalAlignment.Center, MinWidth = 0 };
        box.Checked += (_, _) => { if (!updating) set(true); };
        box.Unchecked += (_, _) => { if (!updating) set(false); };
        return Bind(box, b => { updating = true; b.IsChecked = get(); updating = false; });
    }

    /// A slider. `set` gets `dragging` = true while the thumb is held, so callers can make one undo step per drag.
    public Slider Slider(double min, double max, Func<double> get, Action<double, bool> set, double step = 0, Action? dragStarted = null)
    {
        var updating = false;
        var dragging = false;
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            IsSnapToTickEnabled = step > 0,
            TickFrequency = step > 0 ? step : (max - min) / 100,
            SmallChange = step > 0 ? step : (max - min) / 100,
            LargeChange = step > 0 ? step * 4 : (max - min) / 10,
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true,
        };
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => { dragging = true; dragStarted?.Invoke(); }));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => dragging = false));
        slider.ValueChanged += (_, e) => { if (!updating) set(e.NewValue, dragging); };
        return Bind(slider, s =>
        {
            if (dragging) return;
            updating = true;
            s.Value = get();
            updating = false;
        });
    }

    /// Title, slider and formatted value in a row.
    public FrameworkElement LabeledSlider(string title, double min, double max, Func<double> get, Action<double, bool> set,
                                          Func<double, string> format, double step = 0, Action? dragStarted = null, double titleWidth = 90)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(titleWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        var label = Ui.Text(title);
        label.VerticalAlignment = VerticalAlignment.Center;
        var slider = Slider(min, max, get, set, step, dragStarted);
        var value = Text(() => format(get()), 13, null, Theme.Secondary);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.VerticalAlignment = VerticalAlignment.Center;
        value.FontFamily = Theme.UiFont;
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        grid.Children.Add(label);
        grid.Children.Add(slider);
        grid.Children.Add(value);
        grid.IsEnabledChanged += (_, _) => grid.Opacity = grid.IsEnabled ? 1 : 0.45;
        return grid;
    }

    public Segmented Segmented<T>(IReadOnlyList<(T Value, string Label)> options, Func<T> get, Action<T> set, double fontSize = 13)
    {
        var seg = new Segmented(options.Select(o => o.Label).ToList(), fontSize) { HorizontalAlignment = HorizontalAlignment.Left };
        seg.SelectionChanged += i => set(options[i].Value);
        return Bind(seg, s =>
        {
            var current = get();
            s.SelectedIndex = options.ToList().FindIndex(o => EqualityComparer<T>.Default.Equals(o.Value, current));
        });
    }

    public ComboBox Combo<T>(Func<IReadOnlyList<T>> items, Func<T, string> label, Func<T?> get, Action<T> set, double width = 220)
    {
        var updating = false;
        var combo = new ComboBox { Width = width, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        IReadOnlyList<T> current = [];
        combo.SelectionChanged += (_, _) =>
        {
            if (updating || combo.SelectedIndex < 0 || combo.SelectedIndex >= current.Count) return;
            set(current[combo.SelectedIndex]);
        };
        return Bind(combo, c =>
        {
            updating = true;
            var list = items();
            var labels = list.Select(label).ToList();
            if (!labels.SequenceEqual(c.Items.Cast<object>().Select(o => o is ComboBoxItem i ? i.Content as string : o as string)))
            {
                c.Items.Clear();
                foreach (var l in labels) c.Items.Add(new ComboBoxItem { Content = l });
            }
            current = list;
            var selected = get();
            var index = selected == null ? -1 : list.ToList().FindIndex(x => EqualityComparer<T>.Default.Equals(x, selected));
            c.SelectedIndex = index;
            updating = false;
        });
    }

    public Knob Knob(string title, double min, double max, Func<double> get, Action<double> set, Action checkpoint,
                     double defaultValue = 0, bool logarithmic = false, bool bipolar = false, Color? color = null, double size = 54,
                     Func<double, string>? format = null, Func<string, double?>? parse = null, string? tip = null)
    {
        var knob = new Knob(title, min, max, defaultValue, logarithmic, bipolar, color, size, format, parse) { ToolTip = tip };
        knob.Editing += on => { if (on) checkpoint(); };
        knob.ValueChanged += set;
        return Bind(knob, k => k.Value = get());
    }
}

/// Stateless builders.
static class Ui
{
    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null, Brush? brush = null, bool wrap = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = brush ?? Theme.Text,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    public static TextBlock Headline(string text) => Text(text, 14, FontWeights.SemiBold);
    public static TextBlock Title(string text) => Text(text, 20, FontWeights.SemiBold);
    public static TextBlock Secondary(string text, double size = 13) => Text(text, size, null, Theme.Secondary, wrap: true);
    public static TextBlock Caption(string text) => Text(text, 11.5, null, Theme.Secondary, wrap: true);
    public static TextBlock Tertiary(string text, double size = 11.5) => Text(text, size, null, Theme.Tertiary, wrap: true);

    public static TextBlock Glyph(string glyph, double size = 14, Brush? brush = null) => new()
    {
        Text = glyph,
        FontFamily = Theme.IconFont,
        FontSize = size,
        Foreground = brush ?? Theme.Secondary,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    public static Stack VStack(double spacing, params UIElement?[] children) => Fill(new Stack { Orientation = Orientation.Vertical, Spacing = spacing }, children);
    public static Stack HStack(double spacing, params UIElement?[] children) => Fill(new Stack { Orientation = Orientation.Horizontal, Spacing = spacing, CrossAlignment = Stack.CrossAlign.Center }, children);

    private static Stack Fill(Stack stack, UIElement?[] children)
    {
        foreach (var c in children)
            if (c != null) stack.Children.Add(c);
        return stack;
    }

    /// Flexible space in an HStack/VStack.
    public static FrameworkElement Spacer(double min = 0)
    {
        var e = new FrameworkElement { MinWidth = min };
        Stack.SetFill(e, true);
        return e;
    }

    public static T Filling<T>(this T e) where T : UIElement
    {
        Stack.SetFill(e, true);
        return e;
    }

    public static T Width<T>(this T e, double width) where T : FrameworkElement
    {
        e.Width = width;
        return e;
    }

    public static T MaxW<T>(this T e, double width) where T : FrameworkElement
    {
        e.MaxWidth = width;
        return e;
    }

    public static T Height<T>(this T e, double height) where T : FrameworkElement
    {
        e.Height = height;
        return e;
    }

    public static T Margin<T>(this T e, double l, double t, double r, double b) where T : FrameworkElement
    {
        e.Margin = new Thickness(l, t, r, b);
        return e;
    }

    public static T Left<T>(this T e) where T : FrameworkElement
    {
        e.HorizontalAlignment = HorizontalAlignment.Left;
        return e;
    }

    public static T Right<T>(this T e) where T : FrameworkElement
    {
        e.HorizontalAlignment = HorizontalAlignment.Right;
        return e;
    }

    public static T Center<T>(this T e) where T : FrameworkElement
    {
        e.HorizontalAlignment = HorizontalAlignment.Center;
        if (e is TextBlock t) t.TextAlignment = TextAlignment.Center;
        return e;
    }

    public static T CenterBoth<T>(this T e) where T : FrameworkElement
    {
        e.HorizontalAlignment = HorizontalAlignment.Center;
        e.VerticalAlignment = VerticalAlignment.Center;
        return e;
    }

    public static T Top<T>(this T e) where T : FrameworkElement
    {
        e.VerticalAlignment = VerticalAlignment.Top;
        return e;
    }

    public static T Tip<T>(this T e, string? tip) where T : FrameworkElement
    {
        if (tip != null) e.ToolTip = tip;
        return e;
    }

    public static T Disabled<T>(this T e, bool disabled) where T : UIElement
    {
        e.IsEnabled = !disabled;
        return e;
    }

    public static Button Button(string text, Action click, string? glyph = null, string? tip = null, bool accent = false, bool small = false)
    {
        object content = glyph == null
            ? text
            : HStack(6, Glyph(glyph, small ? 12 : 13, accent ? Brushes.Black : Theme.Text), new TextBlock { Text = text });
        var b = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tip,
            Padding = small ? new Thickness(8, 2, 8, 3) : new Thickness(12, 5, 12, 6),
            FontSize = small ? 12 : 13,
            MinHeight = 0,
        };
        if (accent)
        {
            b.Background = Theme.Accent;
            b.Foreground = Brushes.Black;
            b.BorderBrush = Theme.Accent;
            b.FontWeight = FontWeights.SemiBold;
        }
        b.Click += (_, _) => click();
        return b;
    }

    public static Button IconButton(string glyph, Action click, string? tip = null, double size = 14, Brush? brush = null)
    {
        var b = new Button
        {
            Content = Glyph(glyph, size, brush ?? Theme.Text),
            ToolTip = tip,
            Padding = new Thickness(7, 5, 7, 5),
            MinHeight = 0,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
        };
        b.Click += (_, _) => click();
        return b;
    }

    /// A button that opens a menu of `items` (null = separator).
    public static Button MenuButton(object content, Func<IEnumerable<MenuItem?>> items, string? tip = null)
    {
        var b = new Button { Content = content, ToolTip = tip, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 5, 10, 6), MinHeight = 0 };
        b.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
            foreach (var item in items())
                menu.Items.Add(item == null ? new Separator() : item);
            menu.IsOpen = true;
        };
        return b;
    }

    public static MenuItem Item(string header, Action click, bool enabled = true, bool isChecked = false, string? glyph = null)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled, IsChecked = isChecked };
        if (glyph != null) item.Icon = Glyph(glyph, 13, Theme.Text);
        item.Click += (_, _) => click();
        return item;
    }

    public static MenuItem Submenu(string header, IEnumerable<MenuItem?> items)
    {
        var item = new MenuItem { Header = header };
        foreach (var i in items) item.Items.Add(i == null ? new Separator() : i);
        return item;
    }

    public static MenuItem SectionHeader(string header) => new() { Header = header, IsEnabled = false, FontSize = 11.5, FontWeight = FontWeights.SemiBold };

    public static TextBlock Link(string text, string url)
    {
        var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url), Foreground = Theme.Accent };
        link.RequestNavigate += (_, e) => OpenUrl(e.Uri.ToString());
        return new TextBlock(link) { VerticalAlignment = VerticalAlignment.Center };
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* No browser or blocked: nothing useful to do. */ }
    }

    /// A titled card used on the settings-style pages.
    public static Border Card(string title, string? subtitle, string? glyph, params UIElement?[] content)
    {
        var header = Fill(new Stack { Orientation = Orientation.Horizontal, Spacing = 10, CrossAlignment = Stack.CrossAlign.Start },
        [
            glyph == null ? null : Glyph(glyph, 15, Theme.Secondary).Width(20).Top().Margin(0, 2, 0, 0),
            VStack(2, Headline(title), subtitle == null ? null : Secondary(subtitle)).Filling(),
        ]);
        var body = VStack(12, [header, .. content]);
        return new Border
        {
            Background = Theme.CardBackground,
            BorderBrush = Theme.CardStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Child = body,
        };
    }

    /// The dark "instrument" container behind graphs.
    public static Border Instrument(UIElement child, double radius = 12, double padding = 0) => new()
    {
        Background = Theme.Panel,
        BorderBrush = Theme.PanelStroke,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(radius),
        Padding = new Thickness(padding),
        Child = child,
        ClipToBounds = true,
    };

    public static Border Pill(string text, Brush background, Brush? foreground = null) => new()
    {
        Background = background,
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(6, 1, 6, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = Text(text, 10.5, FontWeights.SemiBold, foreground ?? Theme.Text),
    };

    public static Border Banner(UIElement content, Brush background) => new()
    {
        Background = background,
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(12),
        Child = content,
    };

    /// A scrolling page body with a centred column of at most `maxWidth`.
    public static ScrollViewer Page(UIElement content, double maxWidth = 980) => new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = new Border
        {
            Padding = new Thickness(20),
            MaxWidth = maxWidth + 40,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = content,
        },
        Focusable = false,
    };

    public static Grid Columns(params (UIElement? Element, GridLength Width)[] columns)
    {
        var grid = new Grid();
        var i = 0;
        foreach (var (element, width) in columns)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            if (element != null)
            {
                Grid.SetColumn(element, i);
                grid.Children.Add(element);
            }
            i++;
        }
        return grid;
    }

    public static GridLength Star(double v = 1) => new(v, GridUnitType.Star);
    public static GridLength Px(double v) => new(v);
    public static GridLength Auto => GridLength.Auto;

    /// A two-column form grid: label, control.
    public static Grid Form(double labelWidth, params (string Label, UIElement Control)[] rows)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < rows.Length; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Text(rows[r].Label, 13, null, Theme.Secondary);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 4, 8, 4);
            Grid.SetRow(label, r);
            var control = rows[r].Control;
            if (control is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
            Grid.SetRow(control, r);
            Grid.SetColumn(control, 1);
            grid.Children.Add(label);
            grid.Children.Add(control);
        }
        return grid;
    }

    public static void Copy(string text)
    {
        try { Clipboard.SetText(text); } catch { /* Clipboard busy: ignore. */ }
    }

    public static bool Confirm(string title, string message, string confirm)
    {
        var owner = Application.Current?.MainWindow;
        var result = MessageBox.Show(owner!, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        _ = confirm;
        return result == MessageBoxResult.OK;
    }
}
