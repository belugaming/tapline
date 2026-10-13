using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Tapline.Core;
using Tapline.Core.Services;
using Tapline.Infrastructure;
using Tapline.Model;
using Tapline.UI.Controls;

namespace Tapline.UI.Pages;

/// Headphone correction from three sources: ready-made AutoEQ profiles,
/// raw squig.link measurements fitted on the fly, or the user's own file.
static class HeadphoneCorrection
{
    private const string AutoEqSource = "autoEQ";
    private const string SquigSource = "squig";
    private const string FileSource = "file";

    /// Segoe Fluent Icons "Earbud" (Windows 11).
    internal const string EarbudGlyph = ""; // Headset: MDL2 has no earbud glyph
    internal const string PageGlyph = "";
    internal const string MinusGlyph = "";

    private static string CurrentSource(AppModel model) =>
        model.Prefs.Get("correctionSource", AutoEqSource) switch
        {
            SquigSource => SquigSource,
            FileSource => FileSource,
            _ => AutoEqSource,
        };

    public static FrameworkElement Card(AppModel model, UiScope scope)
    {
        var picker = scope.Segmented<string>(
        [
            (AutoEqSource, L.T("AutoEQ presets")),
            (SquigSource, L.T("squig.link measurements")),
            (FileSource, L.T("My measurement")),
        ], () => CurrentSource(model), v =>
        {
            model.Prefs.Set("correctionSource", v);
            scope.Refresh();
        });
        picker.Width = 520;
        var lifetime = new Lifetime();
        var section = scope.Dynamic(() => CurrentSource(model), inner =>
        {
            var life = lifetime.Next();
            return CurrentSource(model) switch
            {
                SquigSource => new SquigSection(model, inner, life).View,
                FileSource => new FileSection(model, inner, life).View,
                _ => new AutoEqSection(model, inner, life).View,
            };
        });
        return Ui.Card(L.T("Headphone Correction"), L.T("Correct your headphones towards a neutral target using published measurements."), Glyphs.Headphones,
            picker, section);
    }

    /// Starts loading the databases the current source needs (no-op when loaded or loading).
    public static void LoadData(AppModel model)
    {
        if (CurrentSource(model) == AutoEqSource) LoadAutoEq(model, false);
        else LoadSquig(model, false);
    }

    internal static async void LoadAutoEq(AppModel model, bool force)
    {
        var service = model.AutoEq;
        if (service.IsLoading || (!force && service.Entries.Count > 0)) return;
        try { await service.LoadIndexAsync(force); }
        catch (Exception) { /* Errors end up in ErrorMessage. */ }
    }

    internal static async void LoadSquig(AppModel model, bool force)
    {
        // Off the UI thread: with a warm cache the phone books are read and parsed synchronously.
        try { await Task.Run(() => model.Squig.LoadAllAsync(force)); }
        catch (Exception) { /* Errors end up in ErrorMessage. */ }
    }

    // MARK: - Shared pieces

    internal static FrameworkElement KeepBassToggle(UiScope scope, Func<bool> get, Action<bool> set) =>
        scope.Check(L.T("Keep my bass (don't correct below 250 Hz)"), get, set,
            L.T("Useful when you use the headphones differently from how they were measured, e.g. a Bose powered off."));

    /// How much of the correction to apply, 0 … 100 %, like the dry/wet control of a plug-in.
    internal static FrameworkElement StrengthSlider(UiScope scope, Func<double> get, Action<double> set, double titleWidth = 90) =>
        scope.LabeledSlider(L.T("Strength"), 0, 1, get, (v, _) => set(Format.Round(v, 2)),
            v => Math.Round(v * 100).ToString(CultureInfo.InvariantCulture) + "%", 0.05, titleWidth: titleWidth)
            .Tip(L.T("How much of the correction to apply. At full strength the EQ corrects all the way to the target; lower values go part of the way, like a dry/wet control."));

    /// The stored strength, 0 ... 1 (full strength when unset or unreadable).
    internal static double StoredStrength(AppModel model)
    {
        var v = model.Prefs.Get("correctionStrength", 1.0);
        return double.IsNaN(v) ? 1 : Math.Clamp(v, 0, 1);
    }

    internal static FrameworkElement Tips(params string[] lines) =>
        Ui.VStack(8, [Ui.Headline(L.T("Tips")), .. lines.Select(l => Ui.Text(l, 13, wrap: true))]);

    internal static FrameworkElement ErrorRow(UiScope scope, Func<string?> message, Action retry)
    {
        var text = scope.Text(() => message() ?? "", 11.5, null, Theme.Danger);
        text.MaxWidth = 640;
        scope.Bind(text, t => t.ToolTip = message());
        var row = Ui.HStack(8, text, Ui.Button(L.T("Retry"), retry, small: true));
        return scope.Bind(row, r => r.Visibility = message() == null ? Visibility.Collapsed : Visibility.Visible);
    }

    internal static ProgressBar Spinner(double width = 60) => new()
    {
        IsIndeterminate = true,
        Width = width,
        Height = 4,
        MinHeight = 0,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    internal static FrameworkElement Legend(Brush brush, bool dashed, string text) =>
        Ui.HStack(5, new LegendSwatch(brush, dashed), Ui.Text(text, 11.5, null, Theme.Secondary));

    internal static TextBlock Title(string text)
    {
        var t = Ui.Text(text, 14, FontWeights.SemiBold, wrap: true);
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        t.MaxHeight = 40;
        return t;
    }

    /// Cancels the previous lifetime each time a new one starts, so work of replaced views stops.
    private sealed class Lifetime
    {
        private CancellationTokenSource? current;

        public CancellationToken Next(CancellationToken parent = default)
        {
            current?.Cancel();
            current = CancellationTokenSource.CreateLinkedTokenSource(parent);
            return current.Token;
        }
    }

    /// A search field with a placeholder, a busy indicator and a ~150 ms debounce.
    private sealed class SearchBox
    {
        private readonly ProgressBar busy = HeadphoneCorrection.Spinner(36);

        public SearchBox(string prompt, Action<string> changed)
        {
            Box = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                MinHeight = 0,
                Foreground = Theme.Text,
                CaretBrush = Theme.Text,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var placeholder = Ui.Text(prompt, 13, null, Theme.Tertiary);
            placeholder.IsHitTestVisible = false;
            placeholder.VerticalAlignment = VerticalAlignment.Center;
            placeholder.Margin = new Thickness(2, 0, 0, 0);
            var field = new Grid();
            field.Children.Add(Box);
            field.Children.Add(placeholder);
            busy.Visibility = Visibility.Collapsed;

            var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            debounce.Tick += (_, _) =>
            {
                debounce.Stop();
                changed(Box.Text);
            };
            Box.TextChanged += (_, _) =>
            {
                placeholder.Visibility = Box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                debounce.Stop();
                debounce.Start();
            };
            View = new Border
            {
                Background = Theme.ControlFill,
                BorderBrush = Theme.Separator,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 5, 8, 5),
                Child = Ui.HStack(8, Ui.Glyph(Glyphs.Search, 13), field.Filling(), busy),
            };
        }

        public TextBox Box { get; }
        public Border View { get; }

        public bool Busy
        {
            set => busy.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// A bordered list of search results with an overlay when it is empty.
    private sealed class ResultList<T> where T : class
    {
        private readonly ListBox list;
        private readonly TextBlock empty;
        private readonly Func<T, (string Glyph, string Title, string Subtitle)> describe;
        private IReadOnlyList<T> items = [];
        private bool updating;

        public ResultList(double width, double height, Func<T, (string Glyph, string Title, string Subtitle)> describe, Action<T?> selected, string emptyText)
        {
            this.describe = describe;
            list = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                SelectionMode = SelectionMode.Single,
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            list.SelectionChanged += (_, _) =>
            {
                if (updating) return;
                var i = list.SelectedIndex;
                selected(i >= 0 && i < items.Count ? items[i] : null);
            };
            empty = Ui.Text(emptyText, 13, null, Theme.Secondary, wrap: true).CenterBoth();
            empty.TextAlignment = TextAlignment.Center;
            empty.Margin = new Thickness(16);
            empty.IsHitTestVisible = false;
            var grid = new Grid();
            grid.Children.Add(list);
            grid.Children.Add(empty);
            View = new Border
            {
                Width = width,
                Height = height,
                Background = Theme.ControlFill,
                BorderBrush = Theme.CardStroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(2),
                VerticalAlignment = VerticalAlignment.Top,
                Child = grid,
            };
        }

        public Border View { get; }

        public void Set(IReadOnlyList<T> newItems, T? selection, string emptyText)
        {
            updating = true;
            try
            {
                items = newItems;
                list.Items.Clear();
                var selectedIndex = -1;
                for (var i = 0; i < newItems.Count; i++)
                {
                    var (glyph, title, subtitle) = describe(newItems[i]);
                    list.Items.Add(new ListBoxItem { Content = Row(glyph, title, subtitle), ToolTip = title });
                    if (selection != null && selectedIndex < 0 && EqualityComparer<T>.Default.Equals(newItems[i], selection)) selectedIndex = i;
                }
                list.SelectedIndex = selectedIndex;
            }
            finally
            {
                updating = false;
            }
            empty.Text = emptyText;
            empty.Visibility = newItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static FrameworkElement Row(string glyph, string title, string subtitle) =>
            Ui.HStack(8,
                Ui.Glyph(glyph, 14, Theme.Secondary).Width(18),
                Ui.VStack(1, Ui.Text(title), Ui.Text(subtitle, 11.5, null, Theme.Secondary)).Filling());
    }

    /// The accent "Apply Correction" button that turns into "Applied ✓".
    private sealed class ApplyButton
    {
        private readonly TextBlock glyph = Ui.Glyph(Glyphs.Wand, 13, Brushes.Black);
        private readonly TextBlock label = new() { Text = L.T("Apply Correction") };

        public ApplyButton(Action click)
        {
            Button = Ui.Button("", click, accent: true);
            Button.Content = Ui.HStack(6, glyph, label);
            Button.IsEnabledChanged += (_, _) => Button.Opacity = Button.IsEnabled ? 1 : 0.5;
        }

        public Button Button { get; }

        public void Update(bool applied, bool enabled)
        {
            glyph.Text = applied ? Glyphs.Check : Glyphs.Wand;
            label.Text = applied ? L.T("Applied") : L.T("Apply Correction");
            Button.IsEnabled = enabled;
        }
    }

    // MARK: - AutoEQ

    private sealed class AutoEqSection
    {
        private readonly AppModel model;
        private readonly UiScope scope;
        private readonly CancellationToken life;
        private readonly ResultList<AutoEqService.Entry> list;
        private string query = "";
        private IReadOnlyList<AutoEqService.Entry>? searchedEntries;
        private int searchGeneration;
        private AutoEqService.Entry? selected;
        private ParametricEqFormat.Parsed? parsed;
        private bool loading;
        private bool failed;
        private bool applied;
        private bool keepBass;
        private double strength;

        public AutoEqSection(AppModel model, UiScope scope, CancellationToken life)
        {
            this.model = model;
            this.scope = scope;
            this.life = life;
            keepBass = model.Prefs.Get("correctionKeepBass", false);
            strength = HeadphoneCorrection.StoredStrength(model);

            var intro = Ui.Secondary(L.T("Ready-made correction curves for 5,000+ headphones, computed by the AutoEQ project. The database was last updated in July 2025."));
            var search = new SearchBox(L.T("Headphone model, e.g. Bose 700, Sony WH-1000XM5"), q =>
            {
                query = q;
                Search();
            });
            scope.Bind(search, s => s.Busy = model.AutoEq.IsLoading);
            var error = HeadphoneCorrection.ErrorRow(scope,
                () => model.AutoEq.ErrorMessage is { } e ? L.F("Could not load the database: %@", e) : null,
                () => HeadphoneCorrection.LoadAutoEq(model, true));
            list = new ResultList<AutoEqService.Entry>(330, 280,
                e => (e.FormFactor is AutoEqService.FormFactor.InEar or AutoEqService.FormFactor.Earbud ? HeadphoneCorrection.EarbudGlyph : Glyphs.Headphones, e.Name, Subtitle(e)),
                Select, L.T("Type a model name to search"));
            var detail = scope.Dynamic(() => (selected?.Id, parsed != null, loading, failed), BuildDetail).Top();
            View = Ui.VStack(10, intro, search.View, error, Ui.HStack(14, list.View, detail.Filling()));

            scope.OnRefresh(() =>
            {
                if (!ReferenceEquals(searchedEntries, model.AutoEq.Entries)) Search();
            });
            Search();
            HeadphoneCorrection.LoadAutoEq(model, false);
        }

        public FrameworkElement View { get; }

        private static string Subtitle(AutoEqService.Entry e) => e.Rig is { } rig ? $"{e.Source} · {rig}" : e.Source;

        /// A ready-made curve can only be scaled: each band's gain times the strength.
        private IReadOnlyList<EqBand> PreviewBands()
        {
            if (parsed == null) return [];
            var bands = keepBass ? parsed.Bands.Where(b => b.Frequency >= 250) : parsed.Bands;
            return bands.Select(b => b.Scaled(strength)).ToList();
        }

        private async void Search()
        {
            var service = model.AutoEq;
            searchedEntries = service.Entries;
            var q = query;
            var generation = ++searchGeneration;
            List<AutoEqService.Entry> results;
            try { results = await Task.Run(() => service.Search(q), life); }
            catch (Exception) { return; }
            if (generation != searchGeneration || life.IsCancellationRequested) return;
            list.Set(results, selected, string.IsNullOrWhiteSpace(q) ? L.T("Type a model name to search") : L.T("No matches"));
        }

        private async void Select(AutoEqService.Entry? entry)
        {
            selected = entry;
            parsed = null;
            failed = false;
            applied = false;
            loading = entry != null;
            scope.Refresh();
            if (entry == null) return;
            try
            {
                var text = await Task.Run(() => model.AutoEq.FetchProfileAsync(entry, life), life);
                if (!Equals(selected, entry)) return;
                parsed = ParametricEqFormat.Parse(text);
            }
            catch (Exception)
            {
                if (Equals(selected, entry)) failed = true;
            }
            finally
            {
                if (Equals(selected, entry))
                {
                    loading = false;
                    if (!life.IsCancellationRequested) scope.Refresh();
                }
            }
        }

        private UIElement BuildDetail(UiScope inner)
        {
            if (selected is not { } entry)
            {
                return HeadphoneCorrection.Tips(
                    L.T("• Pick the variant that matches how you use the headphones: \"ANC off\", \"passive\" or \"wired\" when they are powered off or used with a cable."),
                    L.T("• Results from oratory1990 and crinacle are listed first when available."),
                    L.T("• The curve is saved as a preset, so you can tweak it afterwards."));
            }
            var children = new List<UIElement?>
            {
                HeadphoneCorrection.Title(entry.Name),
                Ui.Caption(Subtitle(entry)),
            };
            if (parsed is { } p)
            {
                var thumb = new CurveThumbnail { Height = 100 };
                (bool KeepBass, double Strength)? shown = null;
                inner.Bind(thumb, t =>
                {
                    if (shown is { } s && s == (keepBass, strength)) return;
                    shown = (keepBass, strength);
                    t.Set(PreviewBands(), new ToneSettings());
                });
                children.Add(Ui.Instrument(thumb, 8, 6));
                children.Add(inner.Text(() => L.F("%lld of %lld filters", PreviewBands().Count, p.Bands.Count), 11.5, null, Theme.Secondary));
            }
            else if (loading)
            {
                var spinner = HeadphoneCorrection.Spinner(120);
                spinner.HorizontalAlignment = HorizontalAlignment.Center;
                children.Add(new Grid { Height = 100, Children = { spinner } });
            }
            else if (failed)
            {
                children.Add(Ui.Text(L.T("No parametric EQ available for this entry."), 11.5, null, Theme.Danger, wrap: true));
            }
            children.Add(HeadphoneCorrection.StrengthSlider(inner, () => strength, v =>
            {
                strength = v;
                model.Prefs.Set("correctionStrength", v);
                applied = false;
                scope.Refresh();
            }));
            children.Add(HeadphoneCorrection.KeepBassToggle(inner, () => keepBass, v =>
            {
                keepBass = v;
                model.Prefs.Set("correctionKeepBass", v);
                applied = false;
                scope.Refresh();
            }));
            var apply = new ApplyButton(() =>
            {
                var bands = PreviewBands();
                if (bands.Count == 0) return;
                model.ApplyCorrection(bands, $"AutoEQ · {entry.Name}", entry.Source);
                applied = true;
                scope.Refresh();
            });
            inner.Bind(apply, a => a.Update(applied, PreviewBands().Count > 0));
            var link = entry.PageUrl is { } url
                ? Ui.IconButton(Glyphs.OpenExternal, () => Ui.OpenUrl(url.AbsoluteUri), L.T("Open the measurement on GitHub"), 14, Theme.Accent)
                : null;
            children.Add(Ui.HStack(8, apply.Button, link));
            return Ui.VStack(10, [.. children]);
        }
    }

    // MARK: - squig.link

    private sealed class SquigSection
    {
        private static readonly IReadOnlySet<SquigService.Kind> AllKinds = Enum.GetValues<SquigService.Kind>().ToHashSet();
        private static readonly IReadOnlyList<string> KindFilters = ["all", .. Enum.GetValues<SquigService.Kind>().Select(SquigService.RawValue)];

        private readonly AppModel model;
        private readonly UiScope scope;
        private readonly CancellationToken life;
        private readonly ResultList<SquigService.Phone> list;
        private readonly Lifetime workbenchLife = new();
        private string query = "";
        private string kindFilter;
        private IReadOnlyList<SquigService.Phone>? searchedPhones;
        private int searchGeneration;
        private SquigService.Phone? selected;

        public SquigSection(AppModel model, UiScope scope, CancellationToken life)
        {
            this.model = model;
            this.scope = scope;
            this.life = life;
            kindFilter = model.Prefs.Get("squigKind", "all");
            if (!KindFilters.Contains(kindFilter)) kindFilter = "all";
            var squig = model.Squig;

            var intro = scope.Text(() => L.F("Raw measurements from %lld reviewer databases on squig.link, updated continuously. Tapline computes the EQ for the target you choose.", squig.Databases.Count),
                13, null, Theme.Secondary, wrap: true);
            var search = new SearchBox(L.T("Model, e.g. AirPods Pro 3, Bose NC700, HD 600"), q =>
            {
                query = q;
                Search();
            });
            scope.Bind(search, s => s.Busy = squig.IsLoading);
            var kind = scope.Combo<string>(() => KindFilters, KindTitle, () => kindFilter, v =>
            {
                kindFilter = v;
                model.Prefs.Set("squigKind", v);
                Search();
            }, 140);

            var progressText = scope.Text(() => L.F("Loading databases… %lld of %lld", squig.LoadedDatabases, squig.Databases.Count), 11.5, null, Theme.Secondary);
            var progress = new ProgressBar { Width = 360, Height = 4, MinHeight = 0, HorizontalAlignment = HorizontalAlignment.Left };
            scope.Bind(progress, p =>
            {
                p.Maximum = Math.Max(squig.Databases.Count, 1);
                p.Value = Math.Min(squig.LoadedDatabases, p.Maximum);
            });
            var progressBox = Ui.VStack(4, progressText, progress).Left();
            scope.Bind(progressBox, b => b.Visibility = squig.IsLoading ? Visibility.Visible : Visibility.Collapsed);

            var error = HeadphoneCorrection.ErrorRow(scope,
                () => squig.ErrorMessage is { } e ? L.F("Could not load squig.link: %@", e) : null,
                () => HeadphoneCorrection.LoadSquig(model, true));

            list = new ResultList<SquigService.Phone>(330, 420,
                p => (p.Database.Kind == SquigService.Kind.Headphones ? Glyphs.Headphones : HeadphoneCorrection.EarbudGlyph,
                      p.DisplayName, $"{p.Database.SiteName} · {L.T(SquigService.Title(p.Database.Kind))}"),
                p =>
                {
                    selected = p;
                    scope.Refresh();
                }, L.T("Type a model name to search"));

            var detail = scope.Dynamic(() => selected?.Id, inner =>
            {
                if (selected is not { } phone)
                {
                    return HeadphoneCorrection.Tips(
                        L.T("• Pick the mode you actually use: ANC on or off, wired, passive…"),
                        L.T("• Each database comes with its own target curves that match its measurement rig. Harman targets are a good start."),
                        L.T("• Measurements belong to their reviewers. Visit their sites to support them."));
                }
                return new CorrectionWorkbench(model, inner, workbenchLife.Next(life),
                    phone.DisplayName, phone.Database.SiteName, phone.Database.BaseUrl, phone.Database,
                    $"{phone.File} · {phone.Database.SiteName}",
                    token => Task.Run(() => model.Squig.MeasurementAsync(phone, token), token)).View;
            }).Top();

            View = Ui.VStack(10, intro, Ui.HStack(10, search.View.Filling(), kind), progressBox, error,
                Ui.HStack(14, list.View, detail.Filling()));

            scope.OnRefresh(() =>
            {
                if (!ReferenceEquals(searchedPhones, squig.Phones)) Search();
            });
            Search();
            HeadphoneCorrection.LoadSquig(model, false);
        }

        public FrameworkElement View { get; }

        private static string KindTitle(string filter) =>
            filter == "all" ? L.T("All types")
            : SquigService.KindFromRawValue(filter) is { } kind ? L.T(SquigService.Title(kind)) : filter;

        private IReadOnlySet<SquigService.Kind> Kinds =>
            SquigService.KindFromRawValue(kindFilter) is { } kind ? new HashSet<SquigService.Kind> { kind } : AllKinds;

        private async void Search()
        {
            var service = model.Squig;
            searchedPhones = service.Phones;
            var q = query;
            var kinds = Kinds;
            var generation = ++searchGeneration;
            List<SquigService.Phone> results;
            try { results = await Task.Run(() => service.Search(q, kinds), life); }
            catch (Exception) { return; }
            if (generation != searchGeneration || life.IsCancellationRequested) return;
            list.Set(results, selected, string.IsNullOrWhiteSpace(q) ? L.T("Type a model name to search") : L.T("No matches"));
        }
    }

    // MARK: - File

    private sealed class FileSection
    {
        private readonly AppModel model;
        private readonly UiScope scope;
        private readonly Lifetime workbenchLife = new();
        private readonly CancellationToken life;
        private FrequencyResponse? measurement;
        private int measurementVersion;
        private string fileName = "";
        private string? targetDatabaseId;
        private string? error;

        public FileSection(AppModel model, UiScope scope, CancellationToken life)
        {
            this.model = model;
            this.scope = scope;
            this.life = life;

            var intro = Ui.Secondary(L.T("Measured your headphones yourself (miniDSP EARS, REW…)? Import the frequency response as text or CSV and pick a target from a database that uses a similar rig."));
            var choose = Ui.Button(L.T("Choose Measurement File…"), Choose);
            var fileText = scope.Text(() => fileName, 13);
            fileText.MaxWidth = 240;
            var fileLabel = Ui.HStack(5, Ui.Glyph(HeadphoneCorrection.PageGlyph, 13, Theme.Secondary), fileText);
            scope.Bind(fileLabel, f => f.Visibility = fileName.Length == 0 ? Visibility.Collapsed : Visibility.Visible);
            var targets = Ui.HStack(8, Ui.Text(L.T("Targets from")), TargetDatabasePicker());
            var errorText = scope.Text(() => error ?? "", 11.5, null, Theme.Danger, wrap: true);
            scope.Bind(errorText, t => t.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible);

            var workbench = scope.Dynamic(() => measurement != null && TargetDatabase is { } db ? $"{measurementVersion}|{fileName}|{db.Id}" : null, inner =>
            {
                if (measurement is not { } fr || TargetDatabase is not { } db) return null;
                return new CorrectionWorkbench(model, inner, workbenchLife.Next(life), fileName, null, null, db, fileName,
                    _ => Task.FromResult(fr)).View;
            });

            View = Ui.VStack(10, intro, Ui.HStack(10, choose, fileLabel, Ui.Spacer(), targets), errorText, workbench);
            HeadphoneCorrection.LoadSquig(model, false);
        }

        public FrameworkElement View { get; }

        private SquigService.Database? TargetDatabase
        {
            get
            {
                var databases = model.Squig.Databases;
                return databases.FirstOrDefault(d => d.Id == targetDatabaseId) ?? databases.FirstOrDefault(d => d.Kind == SquigService.Kind.Headphones);
            }
        }

        /// Target databases grouped by kind, like a sectioned macOS picker.
        private ComboBox TargetDatabasePicker()
        {
            var combo = new ComboBox { Width = 260, VerticalAlignment = VerticalAlignment.Center };
            IReadOnlyList<SquigService.Database>? shown = null;
            var updating = false;
            combo.SelectionChanged += (_, _) =>
            {
                if (updating || combo.SelectedItem is not ComboBoxItem { Tag: string id }) return;
                targetDatabaseId = id;
                scope.Refresh();
            };
            return scope.Bind(combo, c =>
            {
                updating = true;
                try
                {
                    var databases = model.Squig.Databases;
                    if (!ReferenceEquals(shown, databases))
                    {
                        shown = databases;
                        c.Items.Clear();
                        foreach (var kind in Enum.GetValues<SquigService.Kind>())
                        {
                            var ofKind = databases.Where(d => d.Kind == kind).ToList();
                            if (ofKind.Count == 0) continue;
                            c.Items.Add(new ComboBoxItem
                            {
                                Content = L.T(SquigService.Title(kind)),
                                IsEnabled = false,
                                FontWeight = FontWeights.SemiBold,
                                FontSize = 11.5,
                            });
                            foreach (var db in ofKind)
                                c.Items.Add(new ComboBoxItem { Content = db.SiteName, Tag = db.Id });
                        }
                    }
                    var id = TargetDatabase?.Id;
                    var item = id == null ? null : c.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is string tag && tag == id);
                    if (!ReferenceEquals(c.SelectedItem, item)) c.SelectedItem = item;
                }
                finally
                {
                    updating = false;
                }
            });
        }

        private void Choose()
        {
            var path = Dialogs.OpenFile(L.T("Measurement files") + " (*.txt;*.csv;*.tsv;*.frd)|*.txt;*.csv;*.tsv;*.frd|" + L.T("All files") + " (*.*)|*.*");
            if (path == null) return;
            if (Dialogs.ReadText(path) is not { } text)
            {
                error = L.T("Could not read that file.");
                scope.Refresh();
                return;
            }
            FrequencyResponse? fr;
            try { fr = FrequencyResponse.Parse(text); }
            catch (Exception) { fr = null; }
            if (fr != null)
            {
                measurement = fr;
                measurementVersion++;
                fileName = Path.GetFileNameWithoutExtension(path);
                error = null;
            }
            else
            {
                error = L.T("No frequency response found in that file.");
            }
            scope.Refresh();
        }
    }

    // MARK: - Workbench

    /// Target selection, preferences, preview and apply for a raw measurement.
    private sealed class CorrectionWorkbench
    {
        private static readonly IReadOnlyList<string> NoTargets = [""];

        private readonly AppModel model;
        private readonly UiScope scope;
        private readonly CancellationToken life;
        private readonly SquigService.Database database;
        private readonly string presetName;
        private readonly string? credit;
        private readonly Func<CancellationToken, Task<FrequencyResponse>> loadMeasurement;

        private double bass;
        private double tilt;
        private int filters;
        private bool keepBass;
        private double strength;

        private FrequencyResponse? measurement;
        /// Where the targets come from: the measurement's own database, or a fallback of the same kind.
        private SquigService.Database? targetDatabase;
        private IReadOnlyList<string> targets = [];
        private string? targetName;
        private EqFitter.Result? result;
        private bool computing;
        private string? error;
        private bool applied;
        private FitKey lastKey;
        private CancellationTokenSource? fitCancellation;

        private readonly record struct FitKey(string? Target, double Bass, double Tilt, int Filters, bool KeepBass, double Strength, bool Ready);

        public CorrectionWorkbench(AppModel model, UiScope scope, CancellationToken life, string title, string? credit, Uri? creditUrl,
                                   SquigService.Database database, string presetName, Func<CancellationToken, Task<FrequencyResponse>> loadMeasurement)
        {
            this.model = model;
            this.scope = scope;
            this.life = life;
            this.database = database;
            this.presetName = presetName;
            this.credit = credit;
            this.loadMeasurement = loadMeasurement;
            bass = Math.Clamp(model.Prefs.Get("fitBass", 0.0), -6, 10);
            tilt = Math.Clamp(model.Prefs.Get("fitTilt", 0.0), -1, 1);
            filters = Math.Clamp(model.Prefs.Get("fitFilters", 10), 3, 16);
            keepBass = model.Prefs.Get("correctionKeepBass", false);
            strength = HeadphoneCorrection.StoredStrength(model);

            var header = Ui.VStack(2,
                HeadphoneCorrection.Title(title),
                credit == null ? null : Ui.HStack(4,
                    Ui.Text(L.F("Measurement by %@", credit), 11.5, null, Theme.Secondary),
                    creditUrl == null ? null : Ui.IconButton(Glyphs.OpenExternal, () => Ui.OpenUrl(creditUrl.AbsoluteUri), L.T("Open the reviewer's database"), 12, Theme.Accent)));

            var targetPicker = scope.Combo<string>(() => targets.Count == 0 ? NoTargets : targets, n => n.Length == 0 ? L.T("Loading…") : n,
                () => targetName ?? "", v =>
                {
                    if (v.Length == 0) return;
                    targetName = v;
                    Refresh();
                }, 320);
            var spinner = HeadphoneCorrection.Spinner();
            scope.Bind(spinner, s => s.Visibility = computing ? Visibility.Visible : Visibility.Collapsed);
            var targetRow = Ui.HStack(8, Ui.Text(L.T("Target")), targetPicker, spinner);

            var fallback = scope.Text(() => targetDatabase is { } t && t != database
                ? L.F("This database has no target curves; using targets from %@.", t.SiteName) : "", 11.5, null, Theme.Secondary, wrap: true);
            scope.Bind(fallback, f => f.Visibility = f.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible);

            var preview = scope.Dynamic(() => result != null ? "result" : error is { } e ? "error|" + e : "loading", BuildPreview);

            var strengthSlider = HeadphoneCorrection.StrengthSlider(scope, () => strength, v =>
            {
                strength = v;
                model.Prefs.Set("correctionStrength", v);
                Refresh();
            }, titleWidth: 70);
            var bassSlider = scope.LabeledSlider(L.T("Bass"), -6, 10, () => bass, (v, _) =>
            {
                bass = Format.Round(v, 1);
                model.Prefs.Set("fitBass", bass);
                Refresh();
            }, Format.Gain, 0.5, titleWidth: 70);
            var tiltSlider = scope.LabeledSlider(L.T("Tilt"), -1, 1, () => tilt, (v, _) =>
            {
                tilt = Format.Round(v, 1);
                model.Prefs.Set("fitTilt", tilt);
                Refresh();
            }, v => Printf.Format("%+.1f dB/oct", [Math.Abs(v) < 0.05 ? 0.0 : v]), 0.1, titleWidth: 70);
            var count = scope.Text(() => filters.ToString(CultureInfo.InvariantCulture), 13);
            count.Width = 24;
            count.TextAlignment = TextAlignment.Center;
            var minus = StepButton(HeadphoneCorrection.MinusGlyph, -1);
            var plus = StepButton(Glyphs.Add, 1);
            scope.Bind(minus, b => b.IsEnabled = filters > 3);
            scope.Bind(plus, b => b.IsEnabled = filters < 16);
            var filtersRow = Ui.HStack(4, Ui.Text(L.T("Filters")).Width(70), count, minus, plus);

            var keep = HeadphoneCorrection.KeepBassToggle(scope, () => keepBass, v =>
            {
                keepBass = v;
                model.Prefs.Set("correctionKeepBass", v);
                Refresh();
            });

            var apply = new ApplyButton(() =>
            {
                if (result is not { } r || computing) return;
                model.ApplyCorrection(r.Bands, presetName, this.credit ?? L.T("My measurement"));
                applied = true;
                Refresh();
            });
            scope.Bind(apply, a => a.Update(applied, result != null && !computing));

            View = Ui.VStack(10, header, targetRow, fallback, preview, Ui.VStack(6, strengthSlider, bassSlider, tiltSlider, filtersRow), keep, apply.Button);

            scope.OnRefresh(CheckFitKey);
            CheckFitKey();
            Load();
        }

        public FrameworkElement View { get; }

        private void Refresh()
        {
            if (!life.IsCancellationRequested) scope.Refresh();
        }

        private Button StepButton(string glyph, int delta)
        {
            var b = Ui.IconButton(glyph, () =>
            {
                filters = Math.Clamp(filters + delta, 3, 16);
                model.Prefs.Set("fitFilters", filters);
                Refresh();
            }, null, 11);
            b.Background = Theme.ControlFill;
            b.IsEnabledChanged += (_, _) => b.Opacity = b.IsEnabled ? 1 : 0.4;
            return b;
        }

        private UIElement BuildPreview(UiScope inner)
        {
            if (result == null)
            {
                if (error is { } message) return Ui.Text(message, 11.5, null, Theme.Danger, wrap: true);
                var bar = HeadphoneCorrection.Spinner(120);
                bar.HorizontalAlignment = HorizontalAlignment.Center;
                return new Grid { Height = 170, Children = { bar } };
            }
            var chart = new MeasurementChart { Height = 170 };
            EqFitter.Result? shown = null;
            inner.Bind(chart, c =>
            {
                if (result is not { } r || ReferenceEquals(r, shown)) return;
                shown = r;
                c.Set(r.Frequencies, r.Measurement, r.Target, r.Predicted);
            });
            var deviation = inner.Text(() => result is { } r ? L.F("Deviation %.1f → %.1f dB", r.ErrorBeforeDb, r.ErrorAfterDb) : "", 11.5, null, Theme.Secondary);
            var legend = Ui.HStack(14,
                HeadphoneCorrection.Legend(Brushes.Gray, false, L.T("Measurement")),
                HeadphoneCorrection.Legend(Theme.Brush(Colors.White, 0.8), true, L.T("Target")),
                HeadphoneCorrection.Legend(Theme.Accent, false, L.T("With EQ")),
                Ui.Spacer(),
                deviation);
            return Ui.VStack(8, chart, legend);
        }

        private async void Load()
        {
            try
            {
                measurement = await loadMeasurement(life);
            }
            catch (Exception e)
            {
                if (life.IsCancellationRequested) return;
                error = L.T(e.Message);
            }
            try
            {
                // Cached files are read synchronously by the service, so keep it off the UI thread.
                var squig = model.Squig;
                var db = database;
                var token = life;
                var (from, names) = await Task.Run(async () =>
                {
                    var own = await squig.TargetsAsync(db, token);
                    if (own.Count == 0 && await squig.FallbackTargetDatabaseAsync(db.Kind, token) is { } other)
                        return (other, await squig.TargetsAsync(other, token));
                    return (db, own);
                }, token);
                targets = names;
                targetDatabase = from;
            }
            catch (Exception e)
            {
                if (life.IsCancellationRequested) return;
                error ??= L.T(e.Message);
            }
            targetName ??= targets.FirstOrDefault(t => t.Contains("harman", StringComparison.OrdinalIgnoreCase)) ?? targets.FirstOrDefault();
            if (targets.Count == 0) error = L.T("This database does not publish target curves.");
            Refresh();
        }

        private void CheckFitKey()
        {
            var key = new FitKey(targetName, bass, tilt, filters, keepBass, strength, measurement != null);
            if (key == lastKey) return;
            lastKey = key;
            Refit();
        }

        /// Fits in the background ~200 ms after the last change; a newer change cancels a pending fit.
        private async void Refit()
        {
            fitCancellation?.Cancel();
            fitCancellation = null;
            if (measurement is not { } m || string.IsNullOrEmpty(targetName) || life.IsCancellationRequested) return;
            var name = targetName;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(life);
            fitCancellation = cancellation;
            var token = cancellation.Token;
            applied = false;
            try
            {
                await Task.Delay(200, token);
                computing = true;
                Refresh();
                var squig = model.Squig;
                var targetDb = targetDatabase ?? database;
                var target = await Task.Run(() => squig.TargetAsync(name, targetDb, token), token);
                var options = new EqFitter.Options(maxFilters: filters, bassAdjustDb: bass, tiltDbPerOctave: tilt, keepBassBelow: keepBass ? 250.0 : (double?)null)
                {
                    Strength = strength,
                };
                var fitted = await Task.Run(() => EqFitter.Fit(m, target, options), token);
                if (token.IsCancellationRequested) return;
                result = fitted;
                error = null;
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                // Superseded by a newer fit, or the workbench went away.
            }
            catch (Exception e)
            {
                error = L.T(e.Message);
            }
            finally
            {
                if (ReferenceEquals(fitCancellation, cancellation))
                {
                    fitCancellation = null;
                    computing = false;
                    Refresh();
                }
            }
        }
    }
}
