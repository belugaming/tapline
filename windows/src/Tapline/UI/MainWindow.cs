using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tapline.Audio;
using Tapline.Infrastructure;
using Tapline.Model;
using Tapline.UI.Controls;
using Tapline.UI.Pages;

namespace Tapline.UI;

enum PageKind { Equalizer, Mixer, Assistant, Room, Effects, Devices, Settings }

sealed class MainWindow : DarkWindow
{
    private readonly AppModel model;
    private readonly UiScope scope = new();
    private readonly Dictionary<PageKind, PageBase> pages = [];
    private readonly ContentControl pageHost = new() { Focusable = false };
    private readonly ContentControl bannerHost = new() { Focusable = false };
    private readonly Dictionary<PageKind, Border> navItems = [];
    private readonly MeterBar sideMeterL = new() { Horizontal = true, Height = 4 };
    private readonly MeterBar sideMeterR = new() { Horizontal = true, Height = 4 };
    private PageKind page;
    private PageBase? current;

    public MainWindow(AppModel model)
    {
        this.model = model;
        Title = "Tapline";
        Width = 1180;
        Height = 820;
        MinWidth = 900;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/Tapline.ico"));

        var sidebar = Sidebar();
        var toolbar = Toolbar();
        var detail = new Grid();
        detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(bannerHost, 1);
        Grid.SetRow(pageHost, 2);
        detail.Children.Add(toolbar);
        detail.Children.Add(bannerHost);
        detail.Children.Add(pageHost);
        var root = Ui.Columns((sidebar, Ui.Px(224)), (detail, Ui.Star()));
        root.Background = Theme.WindowBackground;
        Content = root;

        page = Enum.TryParse<PageKind>(model.Prefs.Get("page", "Equalizer"), out var p) ? p : PageKind.Equalizer;
        Show(page);

        model.Changed += Refresh;
        model.Tick += OnTick;
        IsVisibleChanged += (_, _) => UpdateActivation();
        StateChanged += (_, _) => UpdateActivation();
        Refresh();

        InputBindings.Add(new KeyBinding(new Command(model.Undo), Key.Z, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Command(model.Redo), Key.Y, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Command(model.Redo), Key.Z, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(new Command(() => model.SetEqEnabled(!model.Settings.EqEnabled)), Key.B, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Command(model.ToggleCompare), Key.K, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Command(model.TogglePower), Key.P, ModifierKeys.Control | ModifierKeys.Alt));
    }

    public PageBase? CurrentPage => current;

    /// Set when the user chose Quit, so closing really closes.
    public bool AllowClose { get; set; }

    /// Raised when closing the window only hid it.
    public event Action? HiddenToTray;

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (AllowClose) return;
        e.Cancel = true;
        if (model.CloseToTray)
        {
            Hide();
            HiddenToTray?.Invoke();
        }
        else
        {
            Dispatcher.BeginInvoke(() => ((App)Application.Current).Quit());
        }
    }

    private void UpdateActivation()
    {
        var visible = IsVisible && WindowState != WindowState.Minimized;
        if (visible) current?.Activate();
        else current?.Deactivate();
        if (visible == countedVisible) return;
        countedVisible = visible;
        // The sidebar shows the output meters.
        if (visible) model.MetersAppeared(); else model.MetersDisappeared();
    }

    private bool countedVisible;

    private void Refresh()
    {
        scope.Refresh();
        bannerHost.Content = Banner();
    }

    private void OnTick()
    {
        var m = model.Meters;
        sideMeterL.Set(m.PeakL, m.RmsL, m.HoldL);
        sideMeterR.Set(m.PeakR, m.RmsR, m.HoldR);
    }

    public void Show(PageKind kind)
    {
        page = kind;
        model.Prefs.Set("page", kind.ToString());
        current?.Deactivate();
        if (!pages.TryGetValue(kind, out var next))
        {
            next = kind switch
            {
                PageKind.Equalizer => new EqualizerPage(model),
                PageKind.Mixer => new MixerPage(model),
                PageKind.Assistant => new AssistantPage(model),
                PageKind.Room => new RoomPage(model),
                PageKind.Effects => new EffectsPage(model),
                PageKind.Devices => new DevicesPage(model),
                _ => new SettingsPage(model),
            };
            pages[kind] = next;
        }
        current = next;
        pageHost.Content = next;
        if (IsVisible && WindowState != WindowState.Minimized) next.Activate();
        foreach (var (k, item) in navItems) StyleNav(item, k == kind);
        bannerHost.Content = Banner();
    }

    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    // MARK: - Sidebar

    private static readonly (PageKind Kind, string Title, string Glyph)[] Pages =
    [
        (PageKind.Equalizer, "Equalizer", Glyphs.Equalizer),
        (PageKind.Mixer, "Mixer", Glyphs.Volume),
        (PageKind.Assistant, "Assistant", Glyphs.Wand),
        (PageKind.Room, "Room", Glyphs.Microphone),
        (PageKind.Effects, "Effects", Glyphs.Audio),
        (PageKind.Devices, "Devices", Glyphs.Speakers),
        (PageKind.Settings, "Settings", Glyphs.Settings),
    ];

    private FrameworkElement Sidebar()
    {
        var list = Ui.VStack(2);
        foreach (var (kind, title, glyph) in Pages)
        {
            var icon = Ui.Glyph(glyph, 15, Theme.Accent).Width(22);
            var item = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 7, 10, 7),
                Cursor = Cursors.Hand,
                Child = Ui.HStack(10, icon, Ui.Text(L.T(title), 13.5)),
                Tag = icon,
            };
            item.MouseLeftButtonUp += (_, _) => Show(kind);
            item.MouseEnter += (_, _) => { if (kind != page) item.Background = Theme.ControlFill; };
            item.MouseLeave += (_, _) => StyleNav(item, kind == page);
            navItems[kind] = item;
            list.Children.Add(item);
        }

        var power = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Cursor = Cursors.Hand,
            Child = Ui.Glyph(Glyphs.Power, 15),
        };
        power.MouseLeftButtonUp += (_, _) => model.TogglePower();
        scope.Bind(power, p =>
        {
            var on = model.PowerOn;
            p.Background = on ? Theme.Accent : Theme.ControlFillStrong;
            ((TextBlock)p.Child).Foreground = on ? Theme.Brush(Colors.Black, 0.8) : Theme.Secondary;
            p.ToolTip = L.T(on ? "Turn processing off" : "Turn processing on");
        });
        var status = scope.Text(() => StatusText.Short(model), 11.5, null, Theme.Secondary);
        scope.Bind(status, s => s.Foreground = model.Status == EngineStatus.Running ? Theme.Accent : (model.Status is EngineStatus.Off ? Theme.Secondary : Theme.Warning));
        var meters = Ui.VStack(3, sideMeterL, sideMeterR);
        var card = new Border
        {
            Background = Theme.ControlFill,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            Child = Ui.VStack(10, Ui.HStack(10, power, Ui.VStack(1, Ui.Headline("Tapline"), status)), meters),
        };

        var grid = new Grid { Background = Theme.SidebarBackground };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        list.Margin = new Thickness(10, 14, 10, 10);
        card.Margin = new Thickness(10);
        Grid.SetRow(card, 1);
        grid.Children.Add(list);
        grid.Children.Add(card);
        return grid;
    }

    private static void StyleNav(Border item, bool selected)
    {
        item.Background = selected ? Theme.Brush(Theme.AccentColor, 0.85) : Brushes.Transparent;
        if (item.Child is Stack s && s.Children.Count > 1 && s.Children[1] is TextBlock t)
            t.Foreground = selected ? Brushes.Black : Theme.Text;
        if (item.Tag is TextBlock icon) icon.Foreground = selected ? Brushes.Black : Theme.Accent;
    }

    // MARK: - Toolbar

    private FrameworkElement Toolbar()
    {
        var deviceLabel = Ui.Text("", 13.5, FontWeights.Medium);
        var deviceIcon = Ui.Glyph(Glyphs.Speakers, 15, Theme.Text);
        var deviceMenu = Ui.MenuButton(Ui.HStack(8, deviceIcon, deviceLabel, Ui.Glyph(Glyphs.ChevronDown, 9, Theme.Secondary)), () =>
            model.Devices.Outputs.Select(d => (MenuItem?)Ui.Item(d.Name, () => model.Devices.SetDefault(d), isChecked: d.Id == model.CurrentOutput?.Id)).ToList(),
            L.T("Output device"));
        scope.OnRefresh(() =>
        {
            deviceLabel.Text = model.CurrentOutput?.Name ?? L.T("No Output");
            deviceIcon.Text = model.CurrentOutput?.Glyph ?? Glyphs.Mute;
        });
        var undo = Ui.IconButton(Glyphs.Undo, model.Undo, L.T("Undo (Ctrl+Z)"));
        var redo = Ui.IconButton(Glyphs.Redo, model.Redo, L.T("Redo (Ctrl+Y)"));
        scope.OnRefresh(() =>
        {
            undo.IsEnabled = model.CanUndo;
            redo.IsEnabled = model.CanRedo;
            undo.Opacity = model.CanUndo ? 1 : 0.4;
            redo.Opacity = model.CanRedo ? 1 : 0.4;
        });
        var bar = Ui.HStack(8, deviceMenu, Ui.HStack(0, undo, redo), Ui.Spacer());
        return new Border
        {
            BorderBrush = Theme.Separator,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 10, 14, 10),
            Child = bar,
        };
    }

    // MARK: - Banners

    private UIElement? Banner()
    {
        if (page is PageKind.Settings) return null;
        var status = model.Status;
        if (status == EngineStatus.Running) return null;
        if (status == EngineStatus.Off && page is PageKind.Mixer or PageKind.Devices) return null;
        if (status == EngineStatus.NotOnDevice && page is PageKind.Mixer) return null;
        var view = StatusBanner(model, status);
        if (view is FrameworkElement fe) fe.Margin = new Thickness(16, 12, 16, 0);
        return view;
    }

    /// Explains what keeps Tapline from processing and offers the fix.
    public static UIElement? StatusBanner(AppModel model, EngineStatus status)
    {
        var device = model.CurrentOutput?.Name ?? L.T("this device");
        (string Glyph, Brush Color, string Title, string? Detail, Button[] Buttons) content = status switch
        {
            EngineStatus.Off => (Glyphs.Power, Theme.Secondary, L.T("Tapline is off. Audio plays normally without processing."), null,
                [Ui.Button(L.T("Turn On"), () => model.SetPower(true), accent: true)]),
            EngineStatus.NeedsEqualizerApo => (Glyphs.Download, Theme.Warning, L.T("Tapline needs Equalizer APO"),
                L.T("On Windows, Tapline uses the free, open-source Equalizer APO as its audio engine. Install it, tick your output devices in the Configurator that opens at the end, and restart Windows."),
                [Ui.Button(L.T("Download Equalizer APO"), () => Ui.OpenUrl(EqualizerApo.DownloadPage), Glyphs.OpenExternal, accent: true), Ui.Button(L.T("Check Again"), model.Recheck)]),
            EngineStatus.NeedsTakeover => (Glyphs.Info, Theme.Accent, L.T("Tapline will take over Equalizer APO"),
                L.F("Equalizer APO already has a configuration. Tapline replaces it with its own and keeps a copy as %@, which you can restore under Devices › Audio Engine.", EqualizerApo.BackupName),
                [Ui.Button(L.T("Use Tapline"), () => model.TakeOverApoConfig(false), accent: true), Ui.Button(L.T("Import Its EQ as a Preset and Use Tapline"), () => model.TakeOverApoConfig(true))]),
            EngineStatus.NotOnDevice => (Glyphs.Warning, Theme.Warning, L.F("Equalizer APO is not installed on %@", device),
                L.T("Open Equalizer APO's Configurator, tick this device, and restart Windows (or the device). Until then this output plays without Tapline."),
                [Ui.Button(L.T("Open Configurator"), () => { if (!EqualizerApo.OpenConfigurator()) Ui.OpenUrl(EqualizerApo.DownloadPage); }, accent: true), Ui.Button(L.T("Check Again"), model.Recheck)]),
            EngineStatus.EnhancementsDisabled => (Glyphs.Warning, Theme.Warning, L.F("Audio enhancements are off for %@", device),
                L.T("Windows then skips Equalizer APO. Turn them on in Settings › System › Sound › the device › Audio enhancements."),
                [Ui.Button(L.T("Open Sound Settings"), () => Ui.OpenUrl("ms-settings:sound")), Ui.Button(L.T("Check Again"), model.Recheck)]),
            EngineStatus.WriteFailed => (Glyphs.Warning, Theme.Danger, L.T("Tapline could not write Equalizer APO's configuration"),
                (model.LastError ?? "") + " " + L.T("Fixing the folder permissions needs administrator rights once."),
                [Ui.Button(L.T("Fix Permissions"), () => { EqualizerApo.RepairPermissions(); model.Recheck(); }, accent: true), Ui.Button(L.T("Try Again"), model.Recheck)]),
            EngineStatus.NoOutput => (Glyphs.Mute, Theme.Secondary, L.T("No output device available."), null, []),
            _ => (Glyphs.Info, Theme.Secondary, "", null, []),
        };
        var text = Ui.VStack(2, Ui.Text(content.Title, 13, FontWeights.SemiBold, wrap: true), content.Detail == null ? null : Ui.Caption(content.Detail));
        var row = Ui.HStack(12, Ui.Glyph(content.Glyph, 16, content.Color), text.Filling());
        foreach (var b in content.Buttons) row.Children.Add(b);
        var background = status switch
        {
            EngineStatus.Off or EngineStatus.NoOutput => Theme.ControlFill,
            EngineStatus.NeedsTakeover => Theme.Brush(Theme.AccentColor, 0.1),
            _ => Theme.Brush(Theme.WarningColor, 0.12),
        };
        return Ui.Banner(row, background);
    }

    private sealed class Command(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)
        {
            // Text fields keep their own undo.
            if (Keyboard.FocusedElement is TextBox) return;
            action();
        }
    }
}

static class StatusText
{
    public static string Short(AppModel model) => L.T(model.Status switch
    {
        EngineStatus.Off => "Off",
        EngineStatus.Running => "Active",
        EngineStatus.NoOutput => "No output",
        EngineStatus.NeedsEqualizerApo => "Needs Equalizer APO",
        EngineStatus.NeedsTakeover => "Waiting for your go-ahead",
        EngineStatus.NotOnDevice => "Not on this device",
        EngineStatus.EnhancementsDisabled => "Enhancements off",
        _ => "Error",
    });
}
