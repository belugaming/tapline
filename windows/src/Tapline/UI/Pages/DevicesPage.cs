using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Tapline.Audio;
using Tapline.Infrastructure;
using Tapline.Model;

namespace Tapline.UI.Pages;

sealed class DevicesPage : PageBase
{
    public DevicesPage(AppModel model) : base(model)
    {
        var outputs = Scope.Dynamic(() => string.Join("|", Model.Devices.Outputs.Select(d => d.Id)) + Model.CurrentOutput?.Id, inner =>
        {
            var list = Ui.VStack(6);
            foreach (var d in Model.Devices.Outputs) list.Children.Add(DeviceRow(inner, d, output: true));
            return list;
        });
        var inputs = Scope.Dynamic(() => string.Join("|", Model.Devices.Inputs.Select(d => d.Id)) + Model.Devices.DefaultInput?.Id, inner =>
        {
            var list = Ui.VStack(6);
            foreach (var d in Model.Devices.Inputs) list.Children.Add(DeviceRow(inner, d, output: false));
            return list;
        });
        Content = Ui.Page(Ui.VStack(16,
            Ui.Card(L.T("Output"), L.T("Tapline follows the system output. Each device keeps its own EQ, so plugging in your headphones switches the curve automatically."), Glyphs.Speakers, outputs),
            Ui.Card(L.T("Input"), null, Glyphs.Microphone, inputs),
            EngineCard()), 900);
    }

    private FrameworkElement DeviceRow(UiScope scope, AudioDevice device, bool output)
    {
        var isDefault = output ? device.Id == Model.CurrentOutput?.Id : device.Id == Model.Devices.DefaultInput?.Id;
        var title = Ui.HStack(6, Ui.Text(device.Name, 13.5, isDefault ? FontWeights.SemiBold : FontWeights.Normal),
            isDefault ? Ui.Pill(L.T("Current"), Theme.Brush(Theme.AccentColor, 0.2)) : null);
        var details = new List<string>();
        if (!string.IsNullOrEmpty(device.Adapter) && !device.Name.Contains(device.Adapter, StringComparison.OrdinalIgnoreCase)) details.Add(device.Adapter);
        details.Add(L.F("%d ch", device.Channels));
        details.Add(Printf.Format("%g kHz", [device.SampleRate / 1000.0]));
        var info = Ui.VStack(2, title, Ui.Caption(string.Join(" · ", details)));

        var row = Ui.HStack(12, Ui.Glyph(device.Glyph, 18, isDefault ? Theme.Accent : Theme.Secondary).Width(28), info.Filling());
        if (output)
        {
            var profile = scope.Text(() =>
            {
                var s = Model.SettingsFor(device.Id);
                if (!s.EqEnabled) return L.T("EQ off");
                return Model.MatchingPresetFor(s) is { } p ? AppModel.DisplayName(p) : L.T("Custom EQ");
            }, 12, null, Theme.Secondary);
            profile.MaxWidth = 170;
            row.Children.Add(profile);
            if (EqualizerApo.IsInstalled && !EqualizerApo.IsInstalledOn(device))
                row.Children.Add(Ui.Pill(L.T("No Equalizer APO"), Theme.Brush(Theme.WarningColor, 0.25)).Tip(L.T("Equalizer APO is not installed on this device. Open its Configurator to add it.")));
        }
        var use = Ui.Button(L.T(isDefault ? "In Use" : "Use"), () =>
        {
            if (!Model.Devices.SetDefault(device))
                MessageBox.Show(Window.GetWindow(this)!, L.T("Windows did not accept the change. Choose the device in the Sound settings instead."), "Tapline");
        });
        use.IsEnabled = !isDefault;
        use.Width = 84;
        row.Children.Add(use);
        return new Border
        {
            Background = isDefault ? Theme.Brush(Theme.AccentColor, 0.07) : Theme.ControlFill,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Child = row,
        };
    }

    private FrameworkElement EngineCard()
    {
        var status = Scope.Text(() => EqualizerApo.IsInstalled
            ? L.F("Installed · configuration in %@", EqualizerApo.ConfigPath ?? "?")
            : L.T("Not installed"), 13, null, Theme.Secondary, wrap: true);
        var openConfigurator = Ui.Button(L.T("Open Configurator"), () =>
        {
            if (!EqualizerApo.OpenConfigurator()) Ui.OpenUrl(EqualizerApo.DownloadPage);
        }, tip: L.T("Choose the devices Equalizer APO runs on. Needs administrator rights and a restart of the device."));
        var openFolder = Ui.Button(L.T("Show Configuration File"), () =>
        {
            if (EqualizerApo.ConfigFile is string f && File.Exists(f))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{f}\"") { UseShellExecute = true });
        });
        var restore = Ui.Button(L.T("Restore Previous Configuration…"), () =>
        {
            if (!Ui.Confirm(L.T("Restore Previous Configuration…"), L.T("Puts back the Equalizer APO configuration Tapline replaced and turns Tapline's processing off."), L.T("Restore"))) return;
            Model.RestoreApoConfig();
        }, tip: L.T("Tapline saved your own Equalizer APO configuration before it took over."));
        Scope.Bind(restore, b => b.IsEnabled = EqualizerApo.HasBackup);
        Scope.Bind(openFolder, b => b.IsEnabled = EqualizerApo.IsInstalled);
        var download = Ui.Button(L.T("Download Equalizer APO"), () => Ui.OpenUrl(EqualizerApo.DownloadPage), Glyphs.OpenExternal);
        Scope.Bind(download, b => b.Visibility = EqualizerApo.IsInstalled ? Visibility.Collapsed : Visibility.Visible);
        var buttons = new WrapPanel();
        foreach (var b in new Button[] { download, openConfigurator, openFolder, restore, Ui.Button(L.T("Check Again"), Model.Recheck) })
        {
            b.Margin = new Thickness(0, 0, 8, 8);
            buttons.Children.Add(b);
        }
        return Ui.Card(L.T("Audio Engine"), L.T("Tapline writes its EQ into Equalizer APO, which processes the sound inside Windows. The EQ keeps working while Tapline is closed; switch processing off to stop it."), Glyphs.Diagnostic,
            Ui.Form(110,
                (L.T("Equalizer APO"), status),
                (L.T("Status"), Scope.Text(() => StatusText.Short(Model), 13))),
            buttons);
    }
}
