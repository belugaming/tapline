using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tapline.Core;
using Tapline.Infrastructure;
using Tapline.Model;
using Tapline.UI.Controls;

namespace Tapline.UI.Pages;

sealed class AssistantPage : PageBase
{
    private const double Step = 1.5;
    private const string PresetsGlyph = "";
    private const string RenameGlyph = "";

    private static readonly Brush ActiveFill = Theme.Brush(Theme.AccentColor, 0.1);
    private static readonly Brush ActiveStroke = Theme.Brush(Theme.AccentColor, 0.6);
    private static readonly Brush BadgeFill = Theme.Brush(Theme.AccentColor, 0.2);
    private static readonly Brush OverlayFill = Theme.Brush(Colors.Black, 0.55);

    public AssistantPage(AppModel model) : base(model)
    {
        var toneRow = Ui.Columns((QuickToneCard(), Ui.Star()), (null, Ui.Px(16)), (SoundAssistantCard(), Ui.Star()));
        Content = Ui.Page(Ui.VStack(16,
            HeadphoneCorrection.Card(model, Scope),
            toneRow,
            PresetsCard(),
            ImportExportCard()), 980);
    }

    protected override void OnActivated() => HeadphoneCorrection.LoadData(Model);

    // MARK: - Quick tone

    private FrameworkElement QuickToneCard()
    {
        ToneSettings Tone() => Model.Settings.Tone;

        Knob Knob(string title, Func<ToneSettings, double> get, Func<ToneSettings, double, ToneSettings> with, string help) =>
            Scope.Knob(title, ToneSettings.Min, ToneSettings.Max, () => get(Tone()),
                v => Model.Mutate(s => s.Tone = with(s.Tone, Format.Round(v, 1)), undoable: false), Model.Checkpoint,
                0, bipolar: true, color: Theme.AccentColor, size: 50, format: Format.Gain, tip: help);

        var knobs = Ui.HStack(14,
            Knob(L.T("Bass"), t => t.Bass, (t, v) => t with { Bass = v }, L.T("Low shelf at 105 Hz")),
            Knob(L.T("Warmth"), t => t.Warmth, (t, v) => t with { Warmth = v }, L.T("Bell at 250 Hz")),
            Knob(L.T("Clarity"), t => t.Clarity, (t, v) => t with { Clarity = v }, L.T("Bell at 3 kHz")),
            Knob(L.T("Air"), t => t.Air, (t, v) => t with { Air = v }, L.T("High shelf at 10 kHz"))).Center();

        var thumb = new CurveThumbnail { Height = 50 };
        ToneSettings? shown = null;
        Scope.Bind(thumb, t =>
        {
            var tone = Tone();
            if (shown == tone) return;
            shown = tone;
            t.Set([], tone);
        });

        var reset = Ui.Button(L.T("Reset Tone"), () => Model.Mutate(s => s.Tone = new ToneSettings()));
        Scope.Bind(reset, b => b.IsEnabled = !Tone().IsNeutral);

        return Ui.Card(L.T("Quick Tone"), L.T("Broad, musical adjustments layered on top of the EQ."), Glyphs.Dial,
            knobs, Ui.Instrument(thumb, 8, 4), reset);
    }

    // MARK: - Sound assistant

    private static double ClampTone(double v) => Math.Clamp(v, ToneSettings.Min, ToneSettings.Max);

    /// Adjusts an existing bell near `frequency` or adds one.
    private static void Adjust(DspSettings s, double frequency, double q, double delta, double tolerance)
    {
        var i = s.Bands.FindIndex(b => b.Type == FilterType.Peak && Math.Abs(Math.Log10(b.Frequency / frequency)) < tolerance);
        if (i >= 0)
            s.Bands[i] = s.Bands[i] with { Gain = Math.Clamp(s.Bands[i].Gain + delta, EqBand.GainMin, EqBand.GainMax), IsEnabled = true };
        else if (s.Bands.Count < DspSettings.MaxBands)
            s.Bands.Add(new EqBand(FilterType.Peak, frequency, delta, q));
    }

    private FrameworkElement SoundAssistantCard()
    {
        (string Title, string Detail, string Glyph, Action<DspSettings> Run)[] actions =
        [
            (L.T("More bass"), L.T("Raises the low shelf"), "", s => s.Tone = s.Tone with { Bass = ClampTone(s.Tone.Bass + Step) }),
            (L.T("Less bass"), L.T("Lowers the low shelf"), "", s => s.Tone = s.Tone with { Bass = ClampTone(s.Tone.Bass - Step) }),
            (L.T("Less boomy"), L.T("Cuts a resonance around 120 Hz"), "", s => Adjust(s, 120, 1.4, -Step, 0.25)),
            (L.T("Less muddy"), L.T("Cuts the 250 Hz region"), "", s => s.Tone = s.Tone with { Warmth = ClampTone(s.Tone.Warmth - Step) }),
            (L.T("Warmer"), L.T("Adds body around 250 Hz"), "", s => s.Tone = s.Tone with { Warmth = ClampTone(s.Tone.Warmth + Step) }),
            (L.T("Clearer vocals"), L.T("Lifts presence at 3 kHz"), "", s => s.Tone = s.Tone with { Clarity = ClampTone(s.Tone.Clarity + Step) }),
            (L.T("Less harsh"), L.T("Softens 3 kHz"), "", s => s.Tone = s.Tone with { Clarity = ClampTone(s.Tone.Clarity - Step) }),
            (L.T("Less sibilance"), L.T("Tames \"s\" sounds near 7 kHz"), "", s => Adjust(s, 7000, 3, -Step, 0.12)),
            (L.T("More air"), L.T("Raises the 10 kHz shelf"), "", s => s.Tone = s.Tone with { Air = ClampTone(s.Tone.Air + Step) }),
            (L.T("Darker"), L.T("Lowers the 10 kHz shelf"), "", s => s.Tone = s.Tone with { Air = ClampTone(s.Tone.Air - Step) }),
        ];

        var grid = new AdaptiveGrid { MinItemWidth = 150, Spacing = 8 };
        foreach (var (title, detail, glyph, run) in actions)
        {
            var button = new Button
            {
                Content = Ui.HStack(8, Ui.Glyph(glyph, 14, Theme.Accent).Width(18), Ui.Text(title).Filling()),
                ToolTip = detail,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 7, 10, 8),
                MinHeight = 0,
            };
            button.Click += (_, _) => Model.Mutate(run);
            grid.Children.Add(button);
        }

        var undo = Ui.Button(L.T("Undo"), Model.Undo);
        Scope.Bind(undo, b => b.IsEnabled = Model.CanUndo);

        return Ui.Card(L.T("Sound Assistant"), L.T("Describe what you hear. Each click makes a small change you can undo (Ctrl+Z)."), Glyphs.Wand,
            grid, undo);
    }

    // MARK: - Presets

    private FrameworkElement PresetsCard()
    {
        // Computed once per refresh for all tiles.
        Guid? active = Model.MatchingPreset?.Id;
        Scope.OnRefresh(() => active = Model.MatchingPreset?.Id);
        // User presets are replaced when a correction of the same name is applied, and renamed in place.
        var grid = Scope.Dynamic(() => string.Join("|", Model.UserPresets.Select(p => $"{RuntimeHelpers.GetHashCode(p)}:{p.Name}")), inner =>
        {
            var tiles = new AdaptiveGrid { MinItemWidth = 170, Spacing = 10 };
            foreach (var preset in Model.AllPresets) tiles.Children.Add(PresetTile(inner, preset, () => active));
            return tiles;
        });
        return Ui.Card(L.T("Presets"), L.T("Click a preset to apply it to the current output device."), PresetsGlyph, grid);
    }

    private FrameworkElement PresetTile(UiScope scope, EqPreset preset, Func<Guid?> active)
    {
        var thumb = new CurveThumbnail { Height = 44 };
        thumb.Set(preset.Bands, preset.Tone, Theme.SpectrumColor);
        var check = Ui.Glyph(Glyphs.Check, 11, Theme.Accent);
        FrameworkElement? fx = preset.Effects == null ? null : new Border
        {
            Background = BadgeFill,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4, 0, 4, 1),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = L.T("Includes effects"),
            Child = Ui.Text("FX", 9, FontWeights.Bold),
        };
        var title = Ui.HStack(4, Ui.Text(AppModel.DisplayName(preset), 12.5).Filling(), check, fx);
        var caption = Ui.Text(preset.IsBuiltIn ? L.T("Built-in") : preset.Source ?? L.T("My preset"), 10.5, null, Theme.Secondary);
        var body = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = AppModel.DisplayName(preset),
            Child = Ui.VStack(6, Ui.Instrument(thumb, 6, 4), title, caption),
        };
        var hover = false;
        void Paint()
        {
            var on = active() == preset.Id;
            body.Background = on ? ActiveFill : hover ? Theme.ControlFillStrong : Theme.ControlFill;
            body.BorderBrush = on ? ActiveStroke : Brushes.Transparent;
            check.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            var color = on ? Theme.AccentColor : Theme.SpectrumColor;
            if (thumb.Color != color)
            {
                thumb.Color = color;
                thumb.InvalidateVisual();
            }
        }
        Paint();
        scope.OnRefresh(Paint);
        body.MouseEnter += (_, _) => { hover = true; Paint(); };
        body.MouseLeave += (_, _) => { hover = false; Paint(); };
        body.MouseLeftButtonUp += (_, _) => Model.Apply(preset);
        if (preset.IsBuiltIn) return body;

        var rename = Ui.IconButton(RenameGlyph, () => Rename(preset), L.T("Rename…"), 12);
        var delete = Ui.IconButton(Glyphs.Delete, () => Delete(preset), L.T("Delete"), 12);
        rename.Padding = delete.Padding = new Thickness(5, 3, 5, 3);
        var overlay = new Border
        {
            Background = OverlayFill,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(2),
            Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Child = Ui.HStack(2, rename, delete),
        };
        var host = new Grid();
        host.Children.Add(body);
        host.Children.Add(overlay);
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Item(L.T("Rename…"), () => Rename(preset)));
        menu.Items.Add(Ui.Item(L.T("Delete"), () => Delete(preset)));
        host.ContextMenu = menu;
        return host;
    }

    private void Rename(EqPreset preset)
    {
        var (ok, name, _) = Dialogs.SavePreset(Window.GetWindow(this), preset.Name, offerEffects: false, title: L.T("Rename…"));
        if (ok && name.Length > 0) Model.RenamePreset(preset.Id, name);
    }

    private void Delete(EqPreset preset)
    {
        if (Ui.Confirm(L.F("Delete \"%@\"?", preset.Name), L.T("This cannot be undone."), L.T("Delete"))) Model.DeletePreset(preset.Id);
    }

    // MARK: - Import / export

    private FrameworkElement ImportExportCard()
    {
        var editor = new TextBox
        {
            FontFamily = Theme.MonoFont,
            FontSize = 11,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
            Height = 90,
            Padding = new Thickness(6),
        };
        var placeholder = new TextBlock
        {
            Text = L.T("Paste text such as\nPreamp: -6.4 dB\nFilter 1: ON LSC Fc 105 Hz Gain 4.2 dB Q 0.70"),
            FontFamily = Theme.MonoFont,
            FontSize = 11,
            Foreground = Theme.Tertiary,
            Margin = new Thickness(9, 7, 8, 8),
            IsHitTestVisible = false,
        };
        var editorHost = new Grid { VerticalAlignment = VerticalAlignment.Top };
        editorHost.Children.Add(editor);
        editorHost.Children.Add(placeholder);

        var message = Ui.Caption("");
        message.Visibility = Visibility.Collapsed;
        void Say(string text)
        {
            message.Text = text;
            message.Visibility = Visibility.Visible;
        }

        void ImportText(string text, string name)
        {
            try
            {
                Model.ImportEqFile(text, name);
                Say(L.T("Imported and saved as a preset."));
                editor.Text = "";
            }
            catch (Exception)
            {
                Say(L.T("No filters found in that text."));
            }
        }

        var applyPasted = Ui.Button(L.T("Apply Pasted Text"), () => ImportText(editor.Text, L.T("Imported preset")));
        applyPasted.IsEnabled = false;
        editor.TextChanged += (_, _) =>
        {
            placeholder.Visibility = editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            applyPasted.IsEnabled = !string.IsNullOrWhiteSpace(editor.Text);
        };

        var importFile = Ui.Button(L.T("Import File…"), () =>
        {
            string? path;
            try { path = Dialogs.OpenFile(L.T("EQ files") + $" (*.txt;*.{Adi2Preset.FileExtension})|*.txt;*.{Adi2Preset.FileExtension}|" + L.T("All files") + " (*.*)|*.*"); }
            catch (Exception e) { Say(e.Message); return; }
            if (path == null) return;
            if (Dialogs.ReadText(path) is not { } text)
            {
                Say(L.T("Could not read that file."));
                return;
            }
            ImportText(text, Path.GetFileNameWithoutExtension(path));
        });

        var exportCurrent = Ui.Button(L.T("Export Current EQ…"), () =>
        {
            try
            {
                var path = Dialogs.SaveFile(L.T("Text files") + " (*.txt)|*.txt", Adi2Export.SafeFileName($"{CurrentPresetName()} ParametricEQ") + ".txt");
                if (path == null) return;
                File.WriteAllText(path, Model.ExportParametricEq(), new UTF8Encoding(false));
                Say(L.T("Exported."));
            }
            catch (Exception e)
            {
                Say(e.Message);
            }
        });

        var exportAdi2 = Ui.Button(L.T("Export for RME ADI-2…"), () =>
        {
            var name = CurrentPresetName();
            try
            {
                if (Adi2Export.Save(Model.ExportAdi2Preset(name), name)) Say(Adi2Export.LoadHint);
            }
            catch (Exception e)
            {
                Say(Adi2Export.Message(e));
            }
        }, tip: L.T("Saves an .adieqpr preset for RME's ADI-2 Remote app, so the ADI-2 DAC runs this EQ itself"));

        var column = Ui.VStack(8, applyPasted, importFile, exportCurrent, exportAdi2, message).Top();
        return Ui.Card(L.T("Import & Export"),
            L.T("Compatible with Equalizer APO, AutoEQ, Peace, Wavelet and other apps that use the ParametricEQ.txt format, and with RME ADI-2 presets (.adieqpr)."),
            Glyphs.Import,
            Ui.Columns((editorHost, Ui.Star()), (null, Ui.Px(12)), (column, Ui.Px(200))));
    }

    private string CurrentPresetName() => Model.MatchingPreset is { } p ? AppModel.DisplayName(p) : "Tapline";
}
