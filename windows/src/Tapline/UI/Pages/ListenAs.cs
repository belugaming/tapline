using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tapline.Core;
using Tapline.Core.Services;
using Tapline.Infrastructure;
using Tapline.Model;
using Tapline.UI.Controls;

namespace Tapline.UI.Pages;

/// "Listen as": the music as other headphones, earbuds or everyday speakers would play it, heard
/// through the user's own headphones, like Sonarworks SoundID's Translation Check. Measured headphones
/// come from squig.link; speakers are built-in approximations. As on macOS (ListenAsCard.swift).
static class ListenAs
{
    private const string HeadphonesSection = "headphones";
    private const string SpeakersSection = "speakers";

    /// Segoe MDL2 Assets "CellPhone", "Car" and "Stop".
    private const string PhoneGlyph = "";
    private const string CarGlyph = "";
    private const string StopGlyph = "";

    private static readonly Brush PlayingFill = Theme.Brush(Theme.AccentColor, 0.12);
    private static readonly Brush ChosenStroke = Theme.Brush(Theme.AccentColor, 0.3);
    private static readonly Brush PlayingStroke = Theme.Brush(Theme.AccentColor, 0.6);

    /// The name to show: built-in profiles in the user's language.
    public static string Title(DeviceSimulation simulation) => simulation.Profile is { } p ? L.T(p.Name()) : simulation.Name;

    /// "Approximation · Mono", or who measured it.
    public static string Detail(DeviceSimulation simulation) =>
        simulation.Profile is { } p ? L.F("Approximation · %@", L.T(ChannelsTitle(p)))
        : simulation.Source is { } source ? L.F("Measurement by %@", source) : "";

    /// How the device's speakers handle the two channels.
    private static string ChannelsTitle(SpeakerProfile profile) =>
        profile.Width() == 0 ? "Mono" : profile.Width() < 0.5 ? "Narrow stereo" : "Narrower stereo";

    private static string Glyph(SpeakerProfile profile) => profile switch
    {
        SpeakerProfile.Phone => PhoneGlyph,
        SpeakerProfile.Laptop => Glyphs.Laptop,
        SpeakerProfile.Tv => Glyphs.Monitor,
        SpeakerProfile.BluetoothSpeaker => Glyphs.Speakers,
        _ => CarGlyph,
    };

    private static string Glyph(DeviceSimulation? simulation) => simulation?.Profile is { } p ? Glyph(p) : Glyphs.Headphones;

    public static FrameworkElement Card(AppModel model, UiScope scope)
    {
        // Opens on the kind of device chosen last.
        string? section = null;
        string Shown() => section ?? (model.Simulation?.Kind == SimulationKind.Speaker ? SpeakersSection : HeadphonesSection);
        var picker = scope.Segmented<string>(
        [
            (HeadphonesSection, L.T("Headphones & earbuds")),
            (SpeakersSection, L.T("Speakers in devices")),
        ], Shown, v =>
        {
            section = v;
            scope.Refresh();
        });
        picker.Width = 420;
        var lifetime = new HeadphoneCorrection.Lifetime();
        var content = scope.Dynamic(Shown, inner => Shown() == SpeakersSection
            ? SpeakerTiles(model, inner)
            : new Headphones(model, inner, lifetime.Next()).View);
        return Ui.Card(L.T("Listen As"),
            L.T("Hear how your music would sound on other headphones, earbuds and speakers, through your own headphones. Your EQ and correction stay as they are, and switching is level-matched."),
            Glyphs.Swap,
            SwitchRow(model, scope), Shortfall(model, scope), picker, content);
    }

    // MARK: - Switch

    /// The device that plays and the switch.
    private static FrameworkElement SwitchRow(AppModel model, UiScope scope)
    {
        var glyph = Ui.Glyph(Glyph(model.Simulation), 18).Width(26);
        var title = scope.Text(() => model.Simulation is { } s ? L.F("Listen as %@", Title(s)) : L.T("Choose a device below."), 13, FontWeights.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var detail = scope.Text(() => model.Simulation is { } s ? Detail(s) : "", 11.5, null, Theme.Secondary);
        var shortcut = Ui.Text("Ctrl+Alt+Shift+L", 11.5, null, Theme.Secondary).Tip(L.T("Global shortcut: switches the simulation on or off from any app"));
        var toggle = scope.Switch(() => model.Simulating, model.SetSimulating);
        var row = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Child = Ui.HStack(12, glyph, Ui.VStack(1, title, detail).Filling(), shortcut, toggle),
        };
        return scope.Bind(row, r =>
        {
            r.Background = model.Simulating ? PlayingFill : Theme.ControlFill;
            glyph.Text = Glyph(model.Simulation);
            glyph.Foreground = model.Simulating ? Theme.Accent : Theme.Secondary;
            toggle.IsEnabled = model.Simulation != null;
            detail.Visibility = model.Simulation == null ? Visibility.Collapsed : Visibility.Visible;
        });
    }

    /// Without a limiter the make-up gain keeps the EQ's headroom: says how far that falls short of a level match.
    private static FrameworkElement Shortfall(AppModel model, UiScope scope)
    {
        var text = scope.Text(() => L.F("Equalizer APO has no limiter, so the simulation plays %.1f dB below the loudness of your sound so that it cannot clip.",
            model.SimulationLevelShortfallDb), 11.5, null, Theme.Secondary, wrap: true);
        return scope.Bind(text, t => t.Visibility = model.Simulation != null && model.SimulationLevelShortfallDb >= 0.5 ? Visibility.Visible : Visibility.Collapsed);
    }

    // MARK: - Speakers

    private static FrameworkElement SpeakerTiles(AppModel model, UiScope scope)
    {
        var intro = Ui.Secondary(L.T("Approximations of typical devices, written from general knowledge of how such speakers behave: small drivers lose the bass, small boxes resonate, a car cabin lifts the bass. They are not measurements of any product."));
        var grid = new AdaptiveGrid { MinItemWidth = 160, Spacing = 10 };
        foreach (var profile in SpeakerProfiles.All) grid.Children.Add(Tile(model, scope, profile));
        return Ui.VStack(10, intro, grid);
    }

    private static FrameworkElement Tile(AppModel model, UiScope scope, SpeakerProfile profile)
    {
        var thumb = new CurveThumbnail { Height = 44, Range = 24 };
        thumb.Set(profile.Bands(), new ToneSettings(), Theme.SpectrumColor);
        var playingGlyph = Ui.Glyph(Glyphs.Volume3, 13, Theme.Accent);
        var title = Ui.HStack(6, Ui.Glyph(Glyph(profile), 14, Theme.Secondary).Width(16), Ui.Text(L.T(profile.Name()), 12.5).Filling(), playingGlyph);
        var caption = Ui.Text(L.T(ChannelsTitle(profile)), 10.5, null, Theme.Secondary);
        var body = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = Ui.VStack(6, Ui.Instrument(thumb, 6, 4), title, caption),
        };
        bool Chosen() => model.Simulation?.Profile == profile;
        bool Playing() => Chosen() && model.Simulating;
        var hover = false;
        void Paint()
        {
            var playing = Playing();
            body.Background = playing ? PlayingFill : hover ? Theme.ControlFillStrong : Theme.ControlFill;
            body.BorderBrush = Chosen() ? (playing ? PlayingStroke : ChosenStroke) : Brushes.Transparent;
            body.ToolTip = playing ? L.T("Click to stop listening") : L.T("Click to listen as this device");
            playingGlyph.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
            var color = playing ? Theme.AccentColor : Theme.SpectrumColor;
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
        body.MouseLeftButtonUp += (_, _) =>
        {
            if (Playing()) model.SetSimulating(false);
            else model.ChooseSimulation(profile.Simulation());
        };
        return body;
    }

    // MARK: - Headphones and earbuds

    private sealed class Headphones
    {
        private static readonly IReadOnlySet<SquigService.Kind> AllKinds = Enum.GetValues<SquigService.Kind>().ToHashSet();
        private static readonly IReadOnlyList<string> KindFilters = ["all", .. Enum.GetValues<SquigService.Kind>().Select(SquigService.RawValue)];

        private readonly AppModel model;
        private readonly UiScope scope;
        private readonly CancellationToken life;
        private readonly HeadphoneCorrection.ResultList<SquigService.Phone> list;
        private readonly HeadphoneCorrection.Lifetime workbenchLife = new();
        private string query = "";
        private string kindFilter;
        private IReadOnlyList<SquigService.Phone>? searchedPhones;
        private int searchGeneration;
        private SquigService.Phone? selected;

        public Headphones(AppModel model, UiScope scope, CancellationToken life)
        {
            this.model = model;
            this.scope = scope;
            this.life = life;
            kindFilter = model.Prefs.Get("listenAsKind", "all");
            if (!KindFilters.Contains(kindFilter)) kindFilter = "all";
            var squig = model.Squig;

            var intro = Ui.Secondary(L.T("Measurements from squig.link. Tapline fits an EQ that turns the sound your headphone correction aims at into the sound of the device you pick."));
            var search = new HeadphoneCorrection.SearchBox(L.T("Device, e.g. AirPods Pro 2, Apple EarPods, Galaxy Buds"), q =>
            {
                query = q;
                Search();
            });
            scope.Bind(search, s => s.Busy = squig.IsLoading);
            var kind = scope.Combo<string>(() => KindFilters, KindTitle, () => kindFilter, v =>
            {
                kindFilter = v;
                model.Prefs.Set("listenAsKind", v);
                Search();
            }, 140);
            var error = HeadphoneCorrection.ErrorRow(scope,
                () => squig.ErrorMessage is { } e ? L.F("Could not load squig.link: %@", e) : null,
                () => HeadphoneCorrection.LoadSquig(model, true));

            list = new HeadphoneCorrection.ResultList<SquigService.Phone>(330, 330,
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
                        L.T("• Correct your own headphones first (Headphone Correction above): the simulation starts from the target that correction aims at."),
                        L.T("• Pick the mode people actually use, e.g. noise cancelling on."),
                        L.T("• Above 10 kHz only the broad trend is simulated, because measurement rigs disagree up there."));
                }
                return new Workbench(model, inner, workbenchLife.Next(life), phone).View;
            }).Top();

            View = Ui.VStack(10, intro, Ui.HStack(10, search.View.Filling(), kind), error, Ui.HStack(14, list.View, detail.Filling()));

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

    /// The comparison, the fitted curve and the button for one measured device.
    private sealed class Workbench
    {
        private static readonly IReadOnlyList<string> NoTargets = [""];

        private readonly AppModel model;
        private readonly UiScope scope;
        private readonly CancellationToken life;
        private readonly SquigService.Phone phone;

        private FrequencyResponse? device;
        private SimulationComparison comparison = SimulationComparison.CrossRig;
        /// Where the reference target comes from: the correction's database on the same rig, else
        /// the device's own database, or a fallback of the same kind when it has no targets.
        private SquigService.Database? referenceDatabase;
        private IReadOnlyList<string> targets = [];
        private string? targetName;
        private EqFitter.Result? result;
        private bool computing;
        private string? error;
        private (string? Target, bool Ready) lastKey;
        private CancellationTokenSource? fitCancellation;

        public Workbench(AppModel model, UiScope scope, CancellationToken life, SquigService.Phone phone)
        {
            this.model = model;
            this.scope = scope;
            this.life = life;
            this.phone = phone;

            var header = Ui.VStack(2,
                HeadphoneCorrection.Title(phone.DisplayName),
                Ui.HStack(4,
                    Ui.Text(L.F("Measurement by %@", phone.Database.SiteName), 11.5, null, Theme.Secondary),
                    Ui.IconButton(Glyphs.OpenExternal, () => Ui.OpenUrl(phone.Database.BaseUrl.AbsoluteUri), L.T("Open the reviewer's database"), 12, Theme.Accent)));

            var targetPicker = scope.Combo<string>(() => targets.Count == 0 ? NoTargets : targets, n => n.Length == 0 ? L.T("Loading…") : n,
                () => targetName ?? "", v =>
                {
                    if (v.Length == 0) return;
                    targetName = v;
                    Refresh();
                }, 260);
            var spinner = HeadphoneCorrection.Spinner();
            scope.Bind(spinner, s => s.Visibility = computing ? Visibility.Visible : Visibility.Collapsed);
            var targetRow = Ui.HStack(8, Ui.Text(L.T("Compared with")), targetPicker, spinner);

            var note = scope.Text(Note, 11.5, null, Theme.Secondary, wrap: true);
            scope.Bind(note, n => n.Visibility = n.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible);

            var preview = scope.Dynamic(() => result != null ? "result" : error is { } e ? "error|" + e : "loading", BuildPreview);

            var glyph = Ui.Glyph(Glyphs.Play, 13, Brushes.Black);
            var label = new TextBlock();
            var button = Ui.Button("", Toggle, accent: true);
            button.Content = Ui.HStack(6, glyph, label);
            button.IsEnabledChanged += (_, _) => button.Opacity = button.IsEnabled ? 1 : 0.5;
            scope.Bind(button, b =>
            {
                var playing = PlayingThis;
                glyph.Text = playing ? StopGlyph : Glyphs.Play;
                label.Text = playing ? L.T("Stop Listening") : L.T("Listen as This Device");
                b.IsEnabled = result != null && !computing;
            });

            View = Ui.VStack(10, header, targetRow, note, preview, button);

            scope.OnRefresh(CheckFitKey);
            CheckFitKey();
            Load();
        }

        public FrameworkElement View { get; }

        private bool PlayingThis => model.Simulating && model.Simulation?.Id == phone.Id;

        private void Refresh()
        {
            if (!life.IsCancellationRequested) scope.Refresh();
        }

        private DeviceSimulation Simulation(EqFitter.Result r) =>
            new(phone.Id, phone.DisplayName, SimulationKind.Measured, r.Bands, 1, phone.Database.SiteName);

        private void Toggle()
        {
            if (PlayingThis) model.SetSimulating(false);
            else if (result is { } r) model.ChooseSimulation(Simulation(r));
        }

        /// Which comparison applies, in a sentence.
        private string Note()
        {
            if (referenceDatabase is not { } db || string.IsNullOrEmpty(targetName)) return "";
            if (comparison == SimulationComparison.SameRig)
                return L.T("Same database as your headphone correction, so Tapline uses the raw difference between this measurement and your target.");
            return model.CorrectionReference != null
                ? L.F("Measured on another rig than your headphones: Tapline compares it with %@'s own %@ target, so the two rigs' differences cancel.", db.SiteName, targetName)
                : L.F("Tapline compares this measurement with %@'s own %@ target, so the rig's colouring cancels. Most accurate when your headphones are corrected to the same kind of target.", db.SiteName, targetName);
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
            var legend = Ui.HStack(14,
                HeadphoneCorrection.Legend(Brushes.Gray, false, L.T("Your sound")),
                HeadphoneCorrection.Legend(Theme.Brush(Colors.White, 0.8), true, L.T("Device")),
                HeadphoneCorrection.Legend(Theme.Accent, false, L.T("Simulated")));
            return Ui.VStack(8, chart, legend);
        }

        private async void Load()
        {
            var squig = model.Squig;
            var token = life;
            try
            {
                device = await Task.Run(() => squig.MeasurementAsync(phone, token), token);
            }
            catch (Exception e)
            {
                if (life.IsCancellationRequested) return;
                error = L.T(e.Message);
            }
            var correction = model.CorrectionReference;
            comparison = DeviceSimulation.Comparison(phone.Database.Id, correction);
            try
            {
                // On the same rig, the targets of the database the correction's target came from.
                var start = comparison == SimulationComparison.SameRig && correction != null
                    ? squig.Databases.FirstOrDefault(d => d.Id == correction.TargetDatabase) ?? phone.Database
                    : phone.Database;
                // Cached files are read synchronously by the service, so keep it off the UI thread.
                var (from, names) = await Task.Run(async () =>
                {
                    var own = await squig.TargetsAsync(start, token);
                    if (own.Count == 0 && await squig.FallbackTargetDatabaseAsync(start.Kind, token) is { } other)
                        return (other, await squig.TargetsAsync(other, token));
                    return (start, own);
                }, token);
                targets = names;
                referenceDatabase = from;
                targetName = DeviceSimulation.Plan(phone.Database.Id, from.Id, names, correction)?.ReferenceTarget;
            }
            catch (Exception e)
            {
                if (life.IsCancellationRequested) return;
                error ??= L.T(e.Message);
            }
            if (targets.Count == 0) error = L.T("This database does not publish target curves.");
            Refresh();
        }

        private void CheckFitKey()
        {
            var key = (targetName, device != null && referenceDatabase != null);
            if (key == lastKey) return;
            lastKey = key;
            Refit();
        }

        /// Fits in the background ~150 ms after the last change; a newer change cancels a pending fit.
        private async void Refit()
        {
            fitCancellation?.Cancel();
            fitCancellation = null;
            if (device is not { } d || referenceDatabase is not { } db || string.IsNullOrEmpty(targetName) || life.IsCancellationRequested) return;
            var name = targetName;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(life);
            fitCancellation = cancellation;
            var token = cancellation.Token;
            try
            {
                await Task.Delay(150, token);
                computing = true;
                Refresh();
                var squig = model.Squig;
                var reference = await Task.Run(() => squig.TargetAsync(name, db, token), token);
                var fitted = await Task.Run(() => DeviceSimulation.Fit(d, reference), token);
                if (token.IsCancellationRequested) return;
                result = fitted;
                error = null;
                // Already listening to this device: follow the new reference right away.
                if (PlayingThis) model.ChooseSimulation(Simulation(fitted));
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
