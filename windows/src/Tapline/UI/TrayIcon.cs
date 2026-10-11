using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tapline.Infrastructure;
using Tapline.Model;
using Tapline.UI.Controls;
using Tapline.UI.Pages;
using Forms = System.Windows.Forms;

namespace Tapline.UI;

/// The notification-area icon. Left click opens a small panel (like the macOS menu bar extra),
/// right click a menu.
sealed class TrayIcon : IDisposable
{
    private readonly AppModel model;
    private readonly MainWindow window;
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ToolStripMenuItem powerItem;
    private TrayPanel? panel;

    public TrayIcon(AppModel model, MainWindow window, Action quit)
    {
        this.model = model;
        this.window = window;
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Tapline.ico"))?.Stream;
        icon = new Forms.NotifyIcon
        {
            Icon = stream != null ? new System.Drawing.Icon(stream, new System.Drawing.Size(16, 16)) : System.Drawing.SystemIcons.Application,
            Text = "Tapline",
            Visible = true,
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(L.T("Open Tapline"), null, (_, _) => window.BringToFront());
        powerItem = new Forms.ToolStripMenuItem(L.T("Turn Processing Off"), null, (_, _) => model.TogglePower());
        menu.Items.Add(powerItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("Quit Tapline"), null, (_, _) => quit());
        menu.Opening += (_, _) => powerItem.Text = L.T(model.PowerOn ? "Turn Processing Off" : "Turn Processing On");
        icon.ContextMenuStrip = menu;
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) TogglePanel();
        };
        model.Changed += UpdateTooltip;
        UpdateTooltip();
        window.HiddenToTray += () =>
        {
            if (model.Prefs.Get("trayHintShown", false)) return;
            model.Prefs.Set("trayHintShown", true);
            icon.ShowBalloonTip(5000, "Tapline", L.T("Tapline keeps running in the notification area. Right-click its icon to quit."), Forms.ToolTipIcon.Info);
        };
    }

    private void UpdateTooltip()
    {
        var text = $"Tapline · {StatusText.Short(model)}";
        icon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void TogglePanel()
    {
        if (panel is { IsVisible: true })
        {
            panel.Hide();
            return;
        }
        panel ??= new TrayPanel(model, () => { panel?.Hide(); window.BringToFront(); });
        panel.ShowNearCursor();
    }

    public void Dispose()
    {
        model.Changed -= UpdateTooltip;
        panel?.Close();
        icon.Visible = false;
        icon.Dispose();
    }
}

/// The panel opened from the tray icon: power, output, volume, EQ and the apps playing now.
sealed class TrayPanel : Window
{
    private readonly AppModel model;
    private readonly UiScope scope = new();
    private DateTime hiddenAt = DateTime.MinValue;

    public TrayPanel(AppModel model, Action openMain)
    {
        this.model = model;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.Height;
        Width = 360;
        FontFamily = Theme.UiFont;
        FontSize = 13;
        Foreground = Theme.Text;

        var power = scope.Switch(() => model.PowerOn, model.SetPower, L.T("Processing"));
        var status = scope.Text(() => StatusText.Short(model), 11.5, null, Theme.Secondary);
        var header = Ui.HStack(10, Ui.Glyph(Glyphs.Equalizer, 16, Theme.Accent), Ui.VStack(0, Ui.Headline("Tapline"), status), Ui.Spacer(), power);

        var device = scope.Combo(() => model.Devices.Outputs, d => d.Name, () => model.CurrentOutput, d => model.Devices.SetDefault(d), 300);
        device.Width = double.NaN;
        var volume = Ui.HStack(8, Ui.Glyph(Glyphs.Volume3, 14, Theme.Secondary).Width(18),
            scope.Slider(0, 1, () => model.Devices.OutputVolume, (v, _) => model.Devices.SetOutputVolume((float)v)).Filling());

        var eq = scope.Switch(() => model.Settings.EqEnabled, model.SetEqEnabled);
        var preset = EqualizerPage.PresetMenuButton(model, scope, small: true);
        var thumbnail = new CurveThumbnail { Height = 56 };
        scope.OnRefresh(() => thumbnail.Set(model.Settings.Bands, model.Settings.Tone, model.Settings.EqEnabled ? Theme.AccentColor : Colors.Gray));
        var thumbHost = Ui.Instrument(thumbnail, 8, 6);
        thumbHost.Cursor = System.Windows.Input.Cursors.Hand;
        thumbHost.MouseLeftButtonUp += (_, _) => openMain();

        var playing = scope.Dynamic(() => string.Join("|", model.Sessions.Apps.Where(a => a.IsPlaying).Select(a => a.Key)), inner =>
        {
            var apps = model.Sessions.Apps.Where(a => a.IsPlaying).Take(6).ToList();
            if (apps.Count == 0) return null;
            var list = Ui.VStack(4, Ui.Text(L.T("Playing"), 11.5, FontWeights.SemiBold, Theme.Secondary));
            foreach (var app in apps) list.Children.Add(MixerPage.Row(model, inner, app, compact: true, null));
            return list;
        });

        var footer = Ui.HStack(8, Ui.Button(L.T("Open Tapline"), openMain), Ui.Spacer(),
            Ui.IconButton(Glyphs.Power, () => ((App)Application.Current).Quit(), L.T("Quit Tapline")));

        var body = Ui.VStack(12,
            header,
            device,
            volume,
            Ui.HStack(8, Ui.Text(L.T("EQ"), 13, FontWeights.SemiBold), eq, Ui.Spacer(), preset),
            thumbHost,
            playing,
            new Border { Height = 1, Background = Theme.Separator },
            footer);
        Content = new Border
        {
            Background = Theme.CardBackground,
            BorderBrush = Theme.CardStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
            Child = body,
        };
        Deactivated += (_, _) => { Hide(); hiddenAt = DateTime.UtcNow; };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                model.Changed += scope.Refresh;
                model.MixerAppeared();
                scope.Refresh();
            }
            else
            {
                model.Changed -= scope.Refresh;
                model.MixerDisappeared();
            }
        };
    }

    public void ShowNearCursor()
    {
        // A click on the tray icon while the panel is open first deactivates (hides) it; don't reopen.
        if ((DateTime.UtcNow - hiddenAt).TotalMilliseconds < 250) return;
        var cursor = Forms.Cursor.Position;
        var screen = Forms.Screen.FromPoint(cursor).WorkingArea;
        // Lay out invisibly first, so the panel never flashes at its default position.
        Opacity = 0;
        Show();
        UpdateLayout();
        var source = PresentationSource.FromVisual(this);
        var scale = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var work = new Rect(scale.Transform(new Point(screen.Left, screen.Top)), scale.Transform(new Point(screen.Right, screen.Bottom)));
        var p = scale.Transform(new Point(cursor.X, cursor.Y));
        Left = Math.Clamp(p.X - ActualWidth / 2, work.Left + 8, work.Right - ActualWidth - 8);
        Top = Math.Clamp(p.Y - ActualHeight - 12, work.Top + 8, work.Bottom - ActualHeight - 8);
        Opacity = 1;
        Activate();
    }
}
