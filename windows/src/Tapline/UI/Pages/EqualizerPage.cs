using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tapline.Core;
using Tapline.Infrastructure;
using Tapline.Model;
using Tapline.UI.Controls;

namespace Tapline.UI.Pages;

sealed class EqualizerPage : PageBase
{
    private readonly EqGraph graph;
    private readonly MeterBar meterL = new() { Width = 7 };
    private readonly MeterBar meterR = new() { Width = 7 };
    private readonly Border clipLed = new() { Width = 7, Height = 7, CornerRadius = new CornerRadius(4) };
    private readonly TextBlock meterText = Ui.Text("", 9.5, FontWeights.Medium, Theme.GridLabel);

    public EqualizerPage(AppModel model) : base(model)
    {
        graph = new EqGraph(model) { DbRange = model.Prefs.Get("graphRange", 12.0) };
        var header = Header();
        var meters = MeterColumn();
        var graphRow = Ui.Columns((graph, Ui.Star()), (meters, Ui.Px(56)));
        meters.Margin = new Thickness(10, 0, 0, 0);
        var strip = BandStrip();
        var inspector = Ui.Columns((BandInspector(), Ui.Star()), (PreampPanel(), Ui.Px(132)));
        ((FrameworkElement)inspector.Children[1]).Margin = new Thickness(12, 0, 0, 0);

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 160 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(158) });
        Grid.SetRow(graphRow, 1);
        Grid.SetRow(strip, 2);
        Grid.SetRow(inspector, 3);
        graphRow.Margin = new Thickness(0, 12, 0, 12);
        strip.Margin = new Thickness(0, 0, 0, 12);
        grid.Children.Add(header);
        grid.Children.Add(graphRow);
        grid.Children.Add(strip);
        grid.Children.Add(inspector);
        Content = grid;
    }

    public override bool ShowsMeters => true;

    protected override void OnTick()
    {
        graph.InvalidateVisual();
        var m = Model.Meters;
        meterL.Set(m.PeakL, m.RmsL, m.HoldL);
        meterR.Set(m.PeakR, m.RmsR, m.HoldR);
        clipLed.Background = m.Clipping ? Theme.Danger : Theme.ControlFillStrong;
        meterText.Text = Format.MeterDb(Math.Max(m.HoldL, m.HoldR));
    }

    protected override void OnModelChanged() => graph.InvalidateVisual();

    private FrameworkElement Header()
    {
        var eqLabel = Ui.Headline(L.T("EQ"));
        var eqSwitch = Scope.Switch(() => Model.Settings.EqEnabled, Model.SetEqEnabled, L.T("Turn the equalizer on or off for this output device"));
        var presetMenu = PresetMenuButton(Model, Scope);
        var save = Ui.IconButton(Glyphs.Save, SavePreset, L.T("Save the current curve as a preset"));
        var compare = CompareButton();
        var range = Scope.Segmented<double>([(6, "±6"), (12, "±12"), (18, "±18"), (24, "±24")],
            () => graph.DbRange, v => { graph.DbRange = v; Model.Prefs.Set("graphRange", v); Scope.Refresh(); }, 12).Tip(L.T("Vertical range of the graph"));
        var more = Ui.MenuButton(Ui.Glyph(Glyphs.More, 14, Theme.Text), () =>
        [
            Ui.Item(L.T("Reset to Flat"), Model.ResetBands),
            Ui.Item(L.T("Copy A to B"), Model.CopyToOtherSlot),
            null,
            Ui.Item(L.T("Copy as Equalizer APO Text"), () => Ui.Copy(Model.ExportParametricEq())),
        ], L.T("More"));
        more.Margin = new Thickness(10, 0, 0, 0);
        return Ui.HStack(10, eqLabel, eqSwitch, presetMenu.MaxW(260), save, compare, Ui.Spacer(16), range, more);
    }

    private FrameworkElement MeterColumn()
    {
        clipLed.ToolTip = L.T("Clipping: lower the preamp");
        clipLed.HorizontalAlignment = HorizontalAlignment.Center;
        meterText.HorizontalAlignment = HorizontalAlignment.Center;
        var bars = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
        bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
        Grid.SetColumn(meterR, 2);
        bars.Children.Add(meterL);
        bars.Children.Add(meterR);
        var grid = new Grid { Margin = new Thickness(8, 12, 8, 12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(bars, 1);
        Grid.SetRow(meterText, 2);
        bars.Margin = new Thickness(0, 6, 0, 6);
        grid.Children.Add(clipLed);
        grid.Children.Add(bars);
        grid.Children.Add(meterText);
        return Ui.Instrument(grid);
    }

    private Button CompareButton()
    {
        var a = Ui.Text("A", 13, FontWeights.Bold);
        var slash = Ui.Text("/", 13, null, Theme.Tertiary);
        var b = Ui.Text("B", 13, FontWeights.Bold);
        foreach (var t in new[] { a, slash, b }) t.FontFamily = Theme.MonoFont;
        var button = new Button
        {
            Content = Ui.HStack(2, a, slash, b),
            ToolTip = L.T("Switch between two EQ settings to compare them"),
            Padding = new Thickness(10, 5, 10, 6),
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => Model.ToggleCompare();
        Scope.OnRefresh(() =>
        {
            var isA = Model.CompareSlot == CompareSlot.A;
            a.Foreground = isA ? Theme.Accent : Theme.Secondary;
            a.FontWeight = isA ? FontWeights.Bold : FontWeights.Normal;
            b.Foreground = isA ? Theme.Secondary : Theme.Accent;
            b.FontWeight = isA ? FontWeights.Normal : FontWeights.Bold;
        });
        return button;
    }

    private void SavePreset()
    {
        var name = Model.MatchingPreset is EqPreset p ? AppModel.DisplayName(p) : "";
        var (ok, text, includeEffects) = Dialogs.SavePreset(Window.GetWindow(this), name, offerEffects: true);
        if (ok) Model.SaveCurrentAsPreset(text, includeEffects: includeEffects);
    }

    /// The preset picker used here and in the tray panel.
    public static Button PresetMenuButton(AppModel model, UiScope scope, bool small = false)
    {
        var label = Ui.Text("", small ? 12 : 13);
        var content = Ui.HStack(7, Ui.Glyph(Glyphs.Library, small ? 12 : 13, Theme.Secondary), label, Ui.Glyph(Glyphs.ChevronDown, 9, Theme.Secondary));
        var button = Ui.MenuButton(content, () =>
        {
            var items = new List<MenuItem?> { Ui.SectionHeader(L.T("Built-in")) };
            var matching = model.MatchingPreset;
            items.AddRange(BuiltInPresets.All.Select(p => (MenuItem?)Ui.Item(AppModel.DisplayName(p), () => model.Apply(p), isChecked: matching?.Id == p.Id)));
            if (model.UserPresets.Count > 0)
            {
                items.Add(null);
                items.Add(Ui.SectionHeader(L.T("My Presets")));
                items.AddRange(model.UserPresets.Select(p => (MenuItem?)Ui.Item(p.Name, () => model.Apply(p), isChecked: matching?.Id == p.Id)));
                items.Add(null);
                items.Add(Ui.Submenu(L.T("Delete Preset"), model.UserPresets.Select(p => (MenuItem?)Ui.Item(p.Name, () => model.DeletePreset(p.Id))).ToList()));
            }
            return items;
        }, L.T("Choose a preset"));
        if (small) button.Padding = new Thickness(8, 3, 8, 4);
        scope.OnRefresh(() => label.Text = model.MatchingPreset is EqPreset p ? AppModel.DisplayName(p) : L.T("Custom"));
        label.Text = model.MatchingPreset is EqPreset p0 ? AppModel.DisplayName(p0) : L.T("Custom");
        return button;
    }

    // MARK: - Band strip

    private FrameworkElement BandStrip()
    {
        var region = Scope.Dynamic(() => string.Join(",", Model.Settings.Bands.Select(b => b.Id)) + "|" + Model.SelectedBandId, inner =>
        {
            var row = Ui.HStack(6);
            var bands = Model.Settings.Bands;
            for (var i = 0; i < bands.Count; i++) row.Children.Add(BandChip(inner, bands[i].Id, i));
            var add = Ui.Button(L.T("Add Band"), () => Model.AddBand(NextFreeFrequency()), Glyphs.Add, L.T("Add a band. You can also double-click the graph."));
            add.IsEnabled = bands.Count < DspSettings.MaxBands;
            row.Children.Add(add);
            return new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = row,
                Focusable = false,
                Padding = new Thickness(0, 0, 0, 4),
            };
        });
        return region;
    }

    private FrameworkElement BandChip(UiScope scope, Guid id, int index)
    {
        var color = Theme.BandColor(index);
        var selected = Model.SelectedBandId == id;
        var number = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Child = Ui.Text((index + 1).ToString(), 9.5, FontWeights.Bold, Theme.Brush(Colors.Black, 0.75)),
        };
        ((TextBlock)number.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((TextBlock)number.Child).VerticalAlignment = VerticalAlignment.Center;
        var glyph = new FilterGlyph(FilterType.Peak, 18, 11, 1.3);
        var side = Ui.Text("", 9.5, FontWeights.Bold);
        var freq = Ui.Text("", 11.5, FontWeights.SemiBold);
        var gain = Ui.Text("", 10.5, null, Theme.Secondary);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 5, 10, 5),
            BorderThickness = new Thickness(1.2),
            Cursor = Cursors.Hand,
            Child = Ui.HStack(6, number, glyph, side, Ui.VStack(0, freq, gain)),
        };
        scope.OnRefresh(() => Update());
        Update();
        chip.MouseLeftButtonUp += (_, _) => Model.SelectedBandId = id;
        chip.MouseRightButtonUp += (_, e) =>
        {
            if (Band() is not EqBand band) return;
            var menu = new ContextMenu { PlacementTarget = chip };
            menu.Items.Add(Ui.Item(L.T(band.IsEnabled ? "Disable Band" : "Enable Band"), () => Model.UpdateBand(id, b => b with { IsEnabled = !b.IsEnabled })));
            menu.Items.Add(Ui.Submenu(L.T("Channels"), Enum.GetValues<EqChannels>().Select(c =>
                (MenuItem?)Ui.Item(Format.ChannelsTitle(c), () => Model.UpdateBand(id, b => b with { Channels = c }), isChecked: band.Channels == c))));
            menu.Items.Add(new Separator());
            menu.Items.Add(Ui.Item(L.T("Delete Band"), () => Model.RemoveBand(id)));
            menu.IsOpen = true;
            e.Handled = true;
        };
        return chip;

        EqBand? Band() => Model.Settings.Bands.FirstOrDefault(b => b.Id == id) is var b && b.Id == id ? b : null;

        void Update()
        {
            if (Band() is not EqBand band) return;
            number.Background = Theme.Brush(color, band.IsEnabled ? 1 : 0.3);
            glyph.Type = band.Type;
            side.Text = band.Channels switch { EqChannels.Left => "L", EqChannels.Right => "R", _ => "" };
            side.Foreground = band.Channels == EqChannels.Right ? Theme.CurveRight : Theme.Curve;
            side.Visibility = band.Channels == EqChannels.Both ? Visibility.Collapsed : Visibility.Visible;
            freq.Text = Format.Frequency(band.Frequency);
            gain.Text = Format.Gain(band.Gain);
            gain.Visibility = band.Type.UsesGain() ? Visibility.Visible : Visibility.Collapsed;
            chip.Background = selected ? Theme.Brush(color, 0.18) : Theme.ControlFill;
            chip.BorderBrush = selected ? new SolidColorBrush(color) : Brushes.Transparent;
            chip.Opacity = band.IsEnabled ? 1 : 0.55;
        }
    }

    private double NextFreeFrequency()
    {
        var used = Model.Settings.Bands.Select(b => Math.Log10(b.Frequency)).ToList();
        var best = 3.0;
        var bestDistance = -1.0;
        for (var c = Math.Log10(40); c <= Math.Log10(16_000) + 1e-9; c += 0.1)
        {
            var d = used.Count == 0 ? 9 : used.Min(u => Math.Abs(u - c));
            if (d > bestDistance)
            {
                bestDistance = d;
                best = c;
            }
        }
        return Math.Pow(10, best);
    }

    // MARK: - Inspector

    private FrameworkElement BandInspector()
    {
        var region = Scope.Dynamic(() => Model.SelectedBand is EqBand b ? (object)b.Id : null, inner =>
        {
            if (Model.SelectedBand is not EqBand band)
            {
                return Ui.VStack(8,
                    Ui.Glyph(Glyphs.Pointer, 22, Theme.Secondary),
                    Ui.Text(L.T("Select a band to edit it"), 13, null, Theme.Secondary).Center(),
                    Ui.Text(L.T("Drag the points on the graph · scroll for Q · double-click to add"), 11.5, null, Theme.Tertiary).Center())
                    .CenterBoth();
            }
            var id = band.Id;
            int Index() => Math.Max(0, Model.Settings.Bands.FindIndex(b => b.Id == id));
            EqBand Current() => Model.Settings.Bands.FirstOrDefault(b => b.Id == id);
            var color = Theme.BandColor(Index());

            var number = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(color),
                Child = Ui.Text((Index() + 1).ToString(), 11, FontWeights.Bold, Theme.Brush(Colors.Black, 0.75)).CenterBoth(),
            };
            var title = inner.Text(() => Format.TypeName(Current().Type), 14, FontWeights.SemiBold);
            var enabled = inner.Switch(() => Current().IsEnabled, v => Model.UpdateBand(id, b => b with { IsEnabled = v }), L.T("Enable or bypass this band"));
            var delete = Ui.IconButton(Glyphs.Delete, () => Model.RemoveBand(id), L.T("Delete band"));
            var types = Ui.HStack(4);
            foreach (var t in Enum.GetValues<FilterType>())
            {
                var glyph = new FilterGlyph(t, 22, 13, 1.4) { Stroke = Theme.Text };
                var cell = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 5, 6, 5),
                    BorderThickness = new Thickness(1),
                    Child = glyph,
                    Cursor = Cursors.Hand,
                    ToolTip = Format.TypeName(t),
                };
                cell.MouseLeftButtonUp += (_, _) => Model.UpdateBand(id, b => b with { Type = t });
                inner.Bind(cell, c =>
                {
                    var on = Current().Type == t;
                    c.Background = on ? Theme.Brush(color, 0.25) : Theme.ControlFill;
                    c.BorderBrush = on ? new SolidColorBrush(color) : Brushes.Transparent;
                });
                types.Children.Add(cell);
            }
            var channels = inner.Segmented<EqChannels>(Enum.GetValues<EqChannels>().Select(c => (c, Format.ChannelsTitle(c))).ToList(),
                () => Current().Channels, v => Model.UpdateBand(id, b => b with { Channels = v }), 12).Tip(L.T("Which speaker or earpiece this band applies to"));
            channels.Width = 190;

            var left = Ui.VStack(10, Ui.HStack(8, number, title, Ui.Spacer(), enabled, delete), types, channels);
            left.MinWidth = 270;

            var frequency = inner.Knob(L.T("Frequency"), EqBand.FrequencyMin, EqBand.FrequencyMax, () => Current().Frequency,
                v => Model.UpdateBand(id, b => b with { Frequency = v }, undoable: false), Model.Checkpoint,
                1000, logarithmic: true, color: color, format: v => Format.Frequency(v), parse: Knob.ParseFrequency);
            var gain = inner.Knob(L.T("Gain"), EqBand.GainMin, EqBand.GainMax, () => Current().Gain,
                v => Model.UpdateBand(id, b => b with { Gain = Format.Round(v, 1) }, undoable: false), Model.Checkpoint,
                0, bipolar: true, color: color, format: Format.Gain);
            inner.Bind(gain, g => { g.IsEnabled = Current().Type.UsesGain(); g.Opacity = g.IsEnabled ? 1 : 0.4; });
            var q = inner.Knob(L.T("Q"), EqBand.QMin, EqBand.QMax, () => Current().Q,
                v => Model.UpdateBand(id, b => b with { Q = v }, undoable: false), Model.Checkpoint,
                band.Type == FilterType.Peak ? 1 : 0.71, logarithmic: true, color: color, format: Format.Q);

            var divider = new Border { Width = 1, Height = 80, Background = Theme.Separator, Margin = new Thickness(4, 0, 4, 0) };
            return Ui.HStack(18, left, divider, frequency, gain, q, Ui.Spacer());
        });
        return Panel(region);
    }

    private FrameworkElement PreampPanel()
    {
        double Effective() => Model.Settings.EffectivePreampDb(Model.SampleRate);
        var knob = Scope.Knob(L.T("Preamp"), -24, 12, () => Model.Settings.AutoPreamp ? Effective() : Model.Settings.PreampDb,
            v => Model.Mutate(s => { s.PreampDb = Format.Round(v, 1); s.AutoPreamp = false; }, undoable: false), Model.Checkpoint,
            0, bipolar: true, color: Theme.WarningColor, format: Format.Gain);
        knob.HorizontalAlignment = HorizontalAlignment.Center;
        var auto = Scope.Check(L.T("Auto"), () => Model.Settings.AutoPreamp, v =>
        {
            var effective = Format.Round(Effective(), 1);
            Model.Mutate(s => { s.AutoPreamp = v; if (!v) s.PreampDb = effective; });
        }, tip: L.T("Automatically lower the level by the largest boost so the EQ never clips"));
        auto.HorizontalAlignment = HorizontalAlignment.Center;
        return Panel(Ui.VStack(8, knob, auto).CenterBoth());
    }

    private static Border Panel(UIElement child) => new()
    {
        Background = Theme.CardBackground,
        BorderBrush = Theme.CardStroke,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(14),
        Child = child,
    };
}
