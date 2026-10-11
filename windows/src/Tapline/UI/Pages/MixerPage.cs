using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tapline.Audio;
using Tapline.Infrastructure;
using Tapline.Model;
using Tapline.UI.Controls;

namespace Tapline.UI.Pages;

sealed class MixerPage : PageBase
{
    private readonly Dictionary<string, MeterBar> meters = [];

    public MixerPage(AppModel model) : base(model)
    {
        var playing = Scope.Dynamic(() => Signature(true), inner => Section(inner, L.T("Playing Now"), Apps(true), L.T("Nothing is playing right now")));
        var other = Scope.Dynamic(() => Signature(false), inner => Apps(false).Count == 0 ? null : Section(inner, L.T("Other Apps"), Apps(false), null));
        Content = Ui.Page(Ui.VStack(16,
            OutputVolumeCard(),
            playing,
            other,
            Ui.Caption(L.T("These are Windows' own per-app volumes, the same as in the Volume Mixer, so they stay when Tapline is closed. Apps that use exclusive mode or ASIO, like most music production software, play directly without EQ."))),
            820);
    }

    public override bool ShowsMixer => true;

    private List<AudioApp> Apps(bool playing) => Model.Sessions.Apps.Where(a => a.IsPlaying == playing).ToList();

    private string Signature(bool playing) => string.Join("|", Apps(playing).Select(a => a.Key));

    protected override void OnTick()
    {
        foreach (var app in Model.Sessions.Apps)
            if (meters.TryGetValue(app.Key, out var meter)) meter.Set(app.Peak, app.Peak * 0.7f, 0);
    }

    private FrameworkElement Section(UiScope scope, string title, IReadOnlyList<AudioApp> apps, string? empty)
    {
        var stack = Ui.VStack(8, Ui.Headline(title));
        if (apps.Count == 0 && empty != null)
        {
            stack.Children.Add(new Border
            {
                Background = Theme.CardBackground,
                CornerRadius = new CornerRadius(12),
                MinHeight = 50,
                Child = Ui.Text(empty, 13, null, Theme.Secondary).CenterBoth(),
            });
            return stack;
        }
        var rows = Ui.VStack(6);
        foreach (var app in apps) rows.Children.Add(AppRow(scope, app, compact: false));
        stack.Children.Add(rows);
        return stack;
    }

    /// One app with its meter, mute button and volume. Shared with the tray panel.
    public FrameworkElement AppRow(UiScope scope, AudioApp app, bool compact) => Row(Model, scope, app, compact, meters);

    public static FrameworkElement Row(AppModel model, UiScope scope, AudioApp app, bool compact, Dictionary<string, MeterBar>? meterList)
    {
        AudioApp Current() => model.Sessions.Apps.FirstOrDefault(a => a.Key == app.Key) ?? app;
        var size = compact ? 20.0 : 28.0;
        UIElement icon = app.Icon != null
            ? new Image { Source = app.Icon, Width = size, Height = size }
            : Ui.Glyph(app.IsSystemSounds ? Glyphs.Volume : Glyphs.Audio, compact ? 14 : 18, Theme.Secondary).Width(size);
        var name = Ui.Text(app.Name, compact ? 12.5 : 13.5, compact ? FontWeights.Normal : FontWeights.Medium);
        var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = Theme.Accent, ToolTip = L.T("Playing") };
        scope.Bind(dot, d => d.Visibility = Current().IsPlaying ? Visibility.Visible : Visibility.Collapsed);
        var titleRow = Ui.HStack(6, name, dot);
        FrameworkElement info;
        if (compact)
        {
            info = titleRow.Width(110);
        }
        else
        {
            var meter = new MeterBar { Horizontal = true, Height = 4, MaxWidth = 200, HorizontalAlignment = HorizontalAlignment.Left, Width = 200 };
            if (meterList != null) meterList[app.Key] = meter;
            info = Ui.VStack(5, titleRow, meter).Width(230);
        }
        var mute = Ui.IconButton(Glyphs.Volume3, () => model.Sessions.SetMuted(Current(), !Current().Muted), null, compact ? 12 : 14);
        scope.Bind(mute, m =>
        {
            var muted = Current().Muted;
            ((TextBlock)m.Content).Text = muted ? Glyphs.Mute : Glyphs.Volume3;
            ((TextBlock)m.Content).Foreground = muted ? Theme.Danger : Theme.Secondary;
            m.ToolTip = L.T(muted ? "Unmute" : "Mute");
        });
        var slider = scope.Slider(0, 1, () => Current().Volume, (v, _) =>
        {
            model.Sessions.SetVolume(Current(), (float)v);
            model.NotifyChanged();
        });
        slider.MinWidth = compact ? 60 : 120;
        var percent = scope.Text(() => Format.Percent(Current().Volume), compact ? 12 : 13, null, Theme.Secondary);
        percent.TextAlignment = TextAlignment.Right;
        percent.Width = compact ? 38 : 44;
        var row = Ui.HStack(compact ? 8 : 12, icon, info, mute, slider.Filling(), percent);
        return new Border
        {
            Background = compact ? Brushes.Transparent : Theme.ControlFill,
            CornerRadius = new CornerRadius(10),
            Padding = compact ? new Thickness(2, 2, 2, 2) : new Thickness(12, 8, 12, 8),
            Child = row,
        };
    }

    private FrameworkElement OutputVolumeCard()
    {
        var icon = Ui.Glyph(Glyphs.Speakers, 20, Theme.Secondary).Width(28);
        var name = Ui.Text("", 13.5, FontWeights.SemiBold);
        Scope.OnRefresh(() =>
        {
            name.Text = Model.CurrentOutput?.Name ?? L.T("No Output");
            icon.Text = Model.CurrentOutput?.Glyph ?? Glyphs.Mute;
        });
        var mute = Ui.IconButton(Glyphs.Volume3, () => Model.Devices.SetOutputMuted(!Model.Devices.OutputMuted));
        Scope.Bind(mute, m =>
        {
            ((TextBlock)m.Content).Text = Model.Devices.OutputMuted ? Glyphs.Mute : Glyphs.Volume3;
            m.IsEnabled = Model.Devices.HasOutputVolume;
        });
        var slider = Scope.Slider(0, 1, () => Model.Devices.OutputVolume, (v, _) => Model.Devices.SetOutputVolume((float)v));
        Scope.Bind(slider, s => s.IsEnabled = Model.Devices.HasOutputVolume);
        var percent = Scope.Text(() => Model.Devices.HasOutputVolume ? Format.Percent(Model.Devices.OutputVolume) : "—", 13, null, Theme.Secondary);
        percent.Width = 44;
        percent.TextAlignment = TextAlignment.Right;
        var row = Ui.HStack(12, icon, Ui.VStack(2, name, Ui.Caption(L.T("Output volume"))).Width(230), mute, slider.Filling(), percent);
        return new Border
        {
            Background = Theme.CardBackground,
            BorderBrush = Theme.CardStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            Child = row,
            ToolTip = L.T("System volume of the current output"),
        };
    }
}
