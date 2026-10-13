using System.Reflection;
using System.Windows;
using Tapline.Audio;
using Tapline.Infrastructure;
using Tapline.Model;

namespace Tapline.UI.Pages;

sealed class SettingsPage : PageBase
{
    public SettingsPage(AppModel model) : base(model)
    {
        var general = Ui.Card(L.T("General"), null, Glyphs.Settings,
            Scope.Check(L.T("Open Tapline at login"), () => LaunchAtLogin.Enabled, v =>
            {
                try { LaunchAtLogin.Enabled = v; } catch { }
                Scope.Refresh();
            }),
            Scope.Check(L.T("Process audio"), () => Model.PowerOn, Model.SetPower,
                L.T("Equalizer APO keeps applying your EQ while Tapline is closed. Turn this off to hear the original sound.")),
            Scope.Check(L.T("Keep Tapline in the notification area when the window is closed"), () => Model.CloseToTray, v => Model.CloseToTray = v),
            Ui.Caption(L.T("The interface language follows your system language (English or Simplified Chinese).")));

        var bypass = Ui.Card(L.T("EQ Bypass"), L.T("What you hear when the EQ is switched off (EQ switch, Ctrl+B, Ctrl+Alt+Shift+B, notification area)."), Glyphs.Power,
            Scope.Check(L.T("Match loudness when bypassing"), () => Model.LevelMatchedBypass, v => Model.LevelMatchedBypass = v),
            Ui.Caption(L.T("A correction usually lowers the level a few dB to make room for its boosts, so without it the music plays louder, and louder tends to sound better. With this on, the bypassed sound plays at the loudness of the EQ (measured on pink noise, like a LUFS meter), so the comparison is fair. It never raises the level.")));

        var conflicts = Scope.Text(() => Model.ShortcutConflicts.Count == 0 ? "" : L.F("Already used by another app: %@", string.Join(", ", Model.ShortcutConflicts)), 12, null, Theme.Warning, wrap: true);
        Scope.Bind(conflicts, c => c.Visibility = Model.ShortcutConflicts.Count == 0 ? Visibility.Collapsed : Visibility.Visible);
        var shortcuts = Ui.Card(L.T("Keyboard Shortcuts"), L.T("Work in every app, even when Tapline is in the background."), Glyphs.Keyboard,
            Scope.Check(L.T("Enable global shortcuts"), () => Model.GlobalShortcutsEnabled, v => Model.GlobalShortcutsEnabled = v),
            Ui.Form(170,
                ("Ctrl+Alt+Shift+P", Ui.Text(L.T("Turn processing on or off"))),
                ("Ctrl+Alt+Shift+B", Ui.Text(L.T("Bypass EQ (compare with the original sound)"))),
                ("Ctrl+Alt+Shift+K", Ui.Text(L.T("Compare A/B"))),
                ("Ctrl+Alt+Shift+L", Ui.Text(L.T("Listen as another device (Assistant → Listen As)")))),
            conflicts,
            Ui.Caption(L.T("In the Tapline window: Ctrl+Z undo, Ctrl+Y redo, Ctrl+B bypass EQ, Ctrl+K compare A/B.")));

        var analyzer = Ui.Card(L.T("Analyzer"), null, Glyphs.Spectrum,
            Scope.LabeledSlider(L.T("Tilt"), 0, 6, () => Model.AnalyzerTilt, (v, _) => Model.AnalyzerTilt = Format.Round(v, 1), v => Printf.Format("%.1f dB/oct", [v]), 0.5),
            Ui.Caption(L.T("Tilt raises high frequencies in the display only, so typical music looks flat. 3 dB/oct is a good default.")),
            Ui.Caption(L.T("The spectrum shows what the output device plays, after the EQ. It is measured on this PC; nothing is recorded or sent anywhere.")));

        var reset = Ui.Card(L.T("Reset"), null, Glyphs.Refresh,
            Ui.HStack(12, Ui.Text(L.T("Remove all device EQs and presets.")).Filling(),
                Ui.Button(L.T("Reset Everything…"), () =>
                {
                    if (Ui.Confirm(L.T("Reset everything?"), L.T("This cannot be undone."), L.T("Reset"))) Model.ResetEverything();
                })));

        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "?";
        var links = Ui.HStack(16,
            Ui.Link("GitHub", "https://github.com/belugaming/tapline"),
            Ui.Link("AutoEQ", "https://github.com/jaakkopasanen/AutoEq"),
            Ui.Link("Equalizer APO", EqualizerApo.DownloadPage),
            Ui.Link(L.T("License (GPL-3.0)"), "https://www.gnu.org/licenses/gpl-3.0.html"));
        var about = Ui.Card(L.T("About"), null, Glyphs.Info,
            Ui.Headline(L.F("Tapline %@ for Windows", version)),
            Ui.Secondary(L.T("Open-source system-wide EQ, headphone and room correction for Windows, built on Equalizer APO.")),
            Ui.Caption(L.T("Copyright © 2026 Tapline contributors. Free software under the GNU General Public License v3.0, with ABSOLUTELY NO WARRANTY.")),
            Ui.Caption(L.T("Headphone measurements are provided by the AutoEQ project by Jaakko Pasanen and its contributors.")),
            Ui.Caption(L.T("Audio processing by Equalizer APO by Jonas Thedering (GPL).")),
            links);

        Content = Ui.Page(Ui.VStack(16, general, bypass, shortcuts, analyzer, reset, about), 760);
    }

}
