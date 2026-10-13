using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tapline.Audio;
using Tapline.Core;
using Tapline.Infrastructure;
using Tapline.Model;
using Tapline.UI.Controls;

namespace Tapline.UI.Pages;

/// The Room page's steps on a multichannel output (5.1 … 7.1.4): the speaker layout with a
/// wiring check, a measurement of every speaker, their levels, distances and polarity with the
/// trims and delays that align them at the listening position, and a room correction for each.
/// As on macOS (SpeakerRoomCards.swift). Built in `scope`, which the Room page refreshes.
sealed class SpeakerRoomCards
{
    /// Surrounds and heights are mostly small speakers near walls or the ceiling, crossed over to
    /// the subwoofer by the receiver, and the microphone hears them off-axis: correct their room
    /// modes and boundary bumps, not their treble.
    private const double DefaultSurroundCorrectBelow = 300;

    private readonly RoomPage page;
    private readonly AppModel model;
    private readonly RoomAudio room;
    private readonly SpeakerLayout layout;
    private readonly UiScope scope;
    private readonly IReadOnlyList<int> speakers;

    // Persisted in Model.Prefs under the macOS @AppStorage names.
    private double surroundCorrectBelow;
    private bool alignSpeakers;

    private Fits? fits;
    private int? chartChannel;
    private bool showPositions;
    private bool applied;
    private string? correctionMessage;
    private FitKey? lastKey;
    private CancellationTokenSource? fitCancel;

    /// Wiring check: the speaker (index into the layout's speakers) being asked about, the answers by channel.
    private int? wiringStep;
    private readonly Dictionary<int, bool> wiring = [];
    private CancellationTokenSource? playCancel;
    private Task? playTask;

    public SpeakerRoomCards(RoomPage page, AppModel model, RoomAudio room, SpeakerLayout layout, UiScope scope)
    {
        this.page = page;
        this.model = model;
        this.room = room;
        this.layout = layout;
        this.scope = scope;
        speakers = layout.SpeakerChannels;
        surroundCorrectBelow = Math.Clamp(model.Prefs.Get("roomSurroundCorrectBelow", DefaultSurroundCorrectBelow), RoomPage.CorrectBelowMin, RoomPage.CorrectBelowMax);
        alignSpeakers = model.Prefs.Get("roomAlignSpeakers", true);
        View = Ui.VStack(16, SpeakersCard(), MeasureCard(), ResultsCard(), CorrectionCard());
    }

    public FrameworkElement View { get; }

    private string OutputName => model.CurrentOutput?.Name ?? "";

    private IReadOnlyList<RoomAudio.SpeakerRun> Runs => room.SpeakerRuns(layout);

    private void Refresh() => page.Refresh();

    /// Stops the wiring check's noise (the page is hidden or the output changed).
    public void Stop()
    {
        wiringStep = null;
        playCancel?.Cancel();
    }

    // MARK: - Step 2: layout and wiring

    private FrameworkElement SpeakersCard()
    {
        var map = new SpeakerMap(layout) { Width = 260, Height = 210, VerticalAlignment = VerticalAlignment.Top };
        map.SpeakerClicked += channel =>
        {
            if (room.IsBusy || wiringStep != null) return;
            Identify(channel);
        };
        scope.Bind(map, m => m.Set(room.PlayingChannel, wiringStep is { } step ? speakers[step] : null, wiring));

        var info = Ui.VStack(8,
            scope.Text(() => $"{OutputName} · {layout.Name}", 13, FontWeights.SemiBold, null, wrap: true),
            Ui.Caption(L.F("%d speakers on %d channels. Click a speaker to hear it.", speakers.Count, layout.ChannelCount)),
            scope.Dynamic(() => (wiringStep, string.Join(",", wiring.Select(w => $"{w.Key}:{w.Value}"))), WiringControls));
        info.VerticalAlignment = VerticalAlignment.Top;

        return Ui.Card(L.T("2. Speakers"),
            L.T("Tapline measures and corrects each speaker of this layout. Check first that every speaker plays from its place."),
            Glyphs.Speakers,
            Ui.HStack(16, map, info.Filling()),
            Ui.Caption(L.T("Windows renders surround sound to the speakers set for this output in the Sound control panel (your output › Configure); Tapline reads the same layout. A Dolby or DTS bitstream passed through to a receiver cannot be processed.")));
    }

    private UIElement WiringControls(UiScope inner)
    {
        if (wiringStep is { } step)
        {
            var position = layout.Positions[speakers[step]];
            var yes = Ui.Button(L.T("Yes"), () => Answer(true), accent: true, small: true);
            yes.IsDefault = true;
            var again = inner.Bind(Ui.Button(L.T("Play Again"), () => Identify(speakers[step]), small: true), b => b.IsEnabled = !room.IsBusy);
            return Ui.VStack(8,
                Ui.Text(L.F("Playing: %@ (%d of %d). Does the noise come from that speaker?", RoomAudio.SpeakerName(position), step + 1, speakers.Count),
                        13, null, null, wrap: true),
                Ui.HStack(8, yes, Ui.Button(L.T("No"), () => Answer(false), small: true), again, Ui.Button(L.T("Stop"), StopWiring, small: true)));
        }
        var check = inner.Bind(Ui.Button(L.T("Check Wiring"), StartWiring, Glyphs.Volume3, small: true),
            b => b.IsEnabled = !room.IsBusy && model.CurrentOutput != null);
        var clear = wiring.Count > 0 ? Ui.Button(L.T("Clear"), () =>
        {
            wiring.Clear();
            Refresh();
        }, small: true) : null;
        return Ui.VStack(8,
            Ui.HStack(8, check, clear),
            Ui.Caption(L.T("Plays pink noise on each speaker in turn and asks where you hear it.")),
            WiringSummary());
    }

    private UIElement? WiringSummary()
    {
        var wrong = speakers.Where(c => wiring.TryGetValue(c, out var ok) && !ok).ToList();
        if (wrong.Count == 0 && wiring.Count == speakers.Count)
        {
            return Ui.HStack(8, Ui.Glyph(Glyphs.Completed, 13, Theme.Accent),
                Ui.Text(L.T("Every speaker plays from its place."), 13, null, Theme.Accent, wrap: true).Filling());
        }
        if (wrong.Count == 0) return null;
        var names = string.Join(", ", wrong.Select(c => layout.Positions[c].Abbreviation()));
        return Ui.HStack(8, Ui.Glyph(Glyphs.Warning, 13, Theme.Warning).Top().Margin(0, 2, 0, 0),
            Ui.Text(L.F("Not from its place: %@. Check the speaker cables, the receiver's speaker setup and the speakers set for this output in Windows' Sound control panel, then check again.", names),
                    13, null, null, wrap: true).Filling());
    }

    /// Plays pink noise on one speaker, after stopping the one playing.
    private void Identify(int channel)
    {
        if (model.CurrentOutput is not { } output) return;
        var previous = playTask;
        playCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        playCancel = cancel;
        playTask = Play();
        async Task Play()
        {
            if (previous != null) await previous;
            if (cancel.IsCancellationRequested) return;
            await room.IdentifySpeaker(output, channel, cancel.Token);
        }
    }

    private void StartWiring()
    {
        wiring.Clear();
        wiringStep = 0;
        Identify(speakers[0]);
        Refresh();
    }

    private void Answer(bool correct)
    {
        if (wiringStep is not { } step) return;
        wiring[speakers[step]] = correct;
        if (step + 1 < speakers.Count)
        {
            wiringStep = step + 1;
            Identify(speakers[step + 1]);
        }
        else
        {
            wiringStep = null;
            playCancel?.Cancel();
        }
        Refresh();
    }

    private void StopWiring()
    {
        Stop();
        Refresh();
    }

    // MARK: - Step 3: measure

    private FrameworkElement MeasureCard()
    {
        var measure = Ui.Button(L.T("Measure All Speakers"), Measure, Glyphs.Play, accent: true);
        scope.Bind(measure, b => b.IsEnabled = !room.IsBusy && page.Input != null && model.CurrentOutput != null);
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Width = 140, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        scope.Bind(progress, p =>
        {
            p.Visibility = room.Phase == RoomPhase.Measuring ? Visibility.Visible : Visibility.Collapsed;
            p.Value = room.Progress;
        });
        var spinner = new ProgressBar { Width = 60, Height = 4, VerticalAlignment = VerticalAlignment.Center };
        var analyzing = Ui.HStack(6, spinner, Ui.Text(L.T("Analyzing…"), 11.5, null, Theme.Secondary));
        scope.Bind(analyzing, a =>
        {
            var on = room.Phase == RoomPhase.Analyzing;
            a.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (spinner.IsIndeterminate != on) spinner.IsIndeterminate = on;
        });
        var clear = RoomPage.Visible(scope, Ui.Button(L.T("Clear"), () =>
        {
            room.ClearSpeakerRuns(layout);
            fits = null;
            Refresh();
        }), () => Runs.Count > 0);

        var liveMeter = new MeterBar { Horizontal = true, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        scope.Bind(liveMeter, m => m.Set((float)Math.Pow(10, room.InputPeakDb / 20), (float)Math.Pow(10, room.InputRmsDb / 20), 0));
        var status = RoomPage.Visible(scope, Ui.HStack(12,
            Ui.Glyph(Glyphs.Volume3, 13, Theme.Secondary),
            scope.Text(() => room.NowPlaying ?? ""),
            RoomPage.Tabular(scope.Text(() => L.F("%lld s left", room.SecondsLeft), 13, null, Theme.Secondary)),
            liveMeter.Width(240)), () => room.Phase == RoomPhase.Measuring);

        var message = RoomPage.Visible(scope, scope.Text(() => room.LastMessage ?? "", 13, null, Theme.Secondary, wrap: true), () => room.LastMessage != null);
        var privacy = RoomPage.Visible(scope, Ui.Button(L.T("Open Microphone Settings"), RoomAudio.OpenMicrophoneSettings, Glyphs.OpenExternal, small: true),
            () => room.MicrophoneBlocked);
        var outputWarning = RoomPage.DeviceWarning(scope, () => model.CurrentOutput is { IsVirtual: true } output
            ? L.F("%@ is a virtual output. If it belongs to software that changes the sound (such as SoundID Reference or an EQ app), that change ends up in the measurement: switch it off, or make the speakers' own device the output.", output.Name)
            : null);

        var rows = RoomPage.Visible(scope, scope.Dynamic(() => string.Join(",", Runs.Select(r => r.Id)), _ =>
        {
            var list = Ui.VStack(4);
            var runs = Runs;
            for (var index = 0; index < runs.Count; index++)
            {
                var run = runs[index];
                var warn = run.SnrDb < 20 || run.PeakDb > -1;
                var id = run.Id;
                list.Children.Add(Ui.HStack(10,
                    RoomPage.Tabular(Ui.Text("#" + (index + 1).ToString(CultureInfo.InvariantCulture), 13, null, Theme.Secondary)),
                    Ui.Text(layout.Name),
                    Ui.Text(run.Date.ToString("t", CultureInfo.CurrentCulture), 13, null, Theme.Secondary),
                    RoomPage.QualityBadge(run.Quality, false),
                    RoomPage.Tabular(Ui.Text(L.F("SNR %.0f dB · peak %.0f dB", run.SnrDb, run.PeakDb), 11.5, null, warn ? Theme.Warning : Theme.Secondary)),
                    Ui.Spacer(),
                    Ui.IconButton(Glyphs.Delete, () => room.RemoveSpeakerRun(id), size: 12)));
            }
            return list;
        }), () => Runs.Count > 0);

        var duration = SpeakerSweepPlan.DurationOf(speakers.Select(c => new SpeakerSweepPlan.Speaker(c, layout.Positions[c].IsLfe())).ToList());
        var seconds = Math.Round(duration / 5, MidpointRounding.AwayFromZero) * 5;

        return Ui.Card(L.T("3. Measure"), L.T("Measure a few positions around your head; Tapline averages them."), Glyphs.Spectrum,
            Ui.HStack(12, measure, progress, analyzing, Ui.Spacer(), clear),
            status,
            Ui.Caption(L.F("Each speaker plays a %.0f-second sweep in turn, then a short click for timing: about %.0f seconds per position. The subwoofer's click is a low rumble.",
                           SpeakerSweepPlan.DefaultSweepSeconds, seconds)),
            outputWarning,
            RoomPage.Visible(scope, Ui.HStack(10, message.Filling(), privacy), () => room.LastMessage != null || room.MicrophoneBlocked),
            RoomPage.PositionGuide(scope, () => room.MeasuredSpeakerPositions(layout), () => room.NextSpeakerPosition(layout)),
            rows);
    }

    private void Measure()
    {
        if (model.CurrentOutput is not { } output || page.Input is not { } input) return;
        Stop();
        _ = room.MeasureSpeakers(output, input, page.InputChannel, layout);
    }

    // MARK: - Step 4: levels, distances and polarity

    private FrameworkElement ResultsCard()
    {
        var content = scope.Dynamic(() => fits, _ =>
        {
            if (fits is not { } f) return Ui.Secondary(L.T("Measure to see each speaker's level, distance and polarity."));
            var stack = Ui.VStack(10, SpeakerTable(f));
            if (f.Speakers.Any(s => s.Position.IsLfe()))
                stack.Children.Add(Ui.Caption(L.T("* The subwoofer's level from 30 to 80 Hz, against the speakers' from 500 Hz to 2 kHz. Its distance includes the delay of its low-pass filter.")));
            if (f.Advice.Count > 0)
            {
                var advice = Ui.VStack(10);
                foreach (var a in f.Advice) advice.Children.Add(AdviceRow(a));
                advice.Margin = new Thickness(0, 4, 0, 0);
                stack.Children.Add(advice);
            }
            return stack;
        });
        static FrameworkElement Note(string text) =>
            Ui.HStack(8, Ui.Glyph(Glyphs.Info, 12, Theme.Secondary).Width(16).Top().Margin(0, 1, 0, 0), Ui.Caption(L.T(text)).Filling());
        return Ui.Card(L.T("4. Levels, Distances and Polarity"),
            L.T("Compared with the front left speaker. Tapline turns the louder speakers down to the quietest and holds back the nearer ones until the farthest arrives."),
            RoomPage.ChecklistGlyph,
            content,
            Ui.VStack(4,
                Note("Bass management and crossovers stay the receiver's job: set the speakers to Small or Large and the crossover there. Tapline leaves the subwoofer's level to its own gain or the receiver."),
                Note("Switch off the receiver's own room correction (Audyssey, Dirac Live, YPAO) while measuring, and then use either it or Tapline, not both.")));
    }

    /// Every speaker's level, distance and polarity against the front left, and its trim and delay.
    private static FrameworkElement SpeakerTable(Fits f)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        for (var c = 0; c < 6; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var row = 0;
        void Add(UIElement element, int column, bool trailing = false)
        {
            if (element is FrameworkElement fe)
            {
                fe.Margin = new Thickness(column == 0 ? 0 : 18, 2.5, 0, 2.5);
                fe.HorizontalAlignment = trailing ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }
        void NextRow()
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row = grid.RowDefinitions.Count - 1;
        }
        static TextBlock Header(string text) => Ui.Text(L.T(text), 11.5, FontWeights.SemiBold, Theme.Secondary);
        static TextBlock Cell(string text, Brush? brush = null) => RoomPage.Tabular(Ui.Text(text, 13, null, brush));

        NextRow();
        Add(Header("Speaker"), 0);
        Add(Header("Level"), 1, trailing: true);
        Add(Header("Distance"), 2, trailing: true);
        Add(Header("Polarity"), 3);
        Add(Header("Trim"), 4, trailing: true);
        Add(Header("Delay"), 5, trailing: true);
        NextRow();
        var divider = new Border { Height = 1, Background = Theme.Separator, Margin = new Thickness(0, 2, 0, 2) };
        Grid.SetRow(divider, row);
        Grid.SetColumnSpan(divider, 6);
        grid.Children.Add(divider);

        foreach (var s in f.Speakers)
        {
            NextRow();
            var abbreviation = RoomPage.Tabular(Ui.Text(s.Position.Abbreviation(), 11.5, FontWeights.SemiBold));
            abbreviation.Width = 32;
            Add(Ui.HStack(6, abbreviation, Ui.Text(RoomAudio.SpeakerName(s.Position))), 0);
            Add(Cell(Format.Gain(s.LevelDb) + (s.Position.IsLfe() ? "*" : "")), 1, trailing: true);
            Add(Cell(s.Distance is { } d ? Printf.Format("%+.2f m", [d]) : "—"), 2, trailing: true);
            Add(s.OppositePolarity switch
            {
                true => Ui.HStack(5, Ui.Glyph(Glyphs.Warning, 12, Theme.Warning), Ui.Text(L.T("Reversed"), 13, null, Theme.Warning)),
                false => Ui.Text(L.T("Same"), 13, null, Theme.Secondary),
                null => Ui.Text("—", 13, null, Theme.Secondary),
            }, 3);
            Add(Cell(Format.Gain(s.TrimDb), Theme.Secondary), 4, trailing: true);
            Add(Cell(Printf.Format("%.2f ms", [s.DelayMs]), Theme.Secondary), 5, trailing: true);
        }
        return grid;
    }

    private FrameworkElement AdviceRow(SpeakerAdvice advice)
    {
        var (glyph, brush) = advice.Severity switch
        {
            RoomAdviceSeverity.Warning => (Glyphs.Warning, Theme.Warning),
            RoomAdviceSeverity.Suggestion => (Glyphs.Lightbulb, Theme.Accent),
            _ => (Glyphs.Info, Theme.Secondary),
        };
        return Ui.HStack(10, Ui.Glyph(glyph, 14, brush).Width(18).Top().Margin(0, 1, 0, 0),
            Ui.VStack(3,
                Ui.Text(AdviceTitle(advice.Kind), 13, FontWeights.SemiBold, null, wrap: true),
                Ui.Caption(AdviceDetail(advice.Kind))).Filling());
    }

    private string Name(int channel) =>
        channel >= 0 && channel < layout.ChannelCount ? RoomAudio.SpeakerName(layout.Positions[channel]) : L.F("channel %lld", channel + 1);

    private string AdviceTitle(SpeakerAdviceKind kind) => kind switch
    {
        SpeakerAdviceKind.Polarity p => L.F("%@ has opposite polarity", Name(p.Channel)),
        SpeakerAdviceKind.Silent s => L.F("%@: no timing click heard", Name(s.Channel)),
        SpeakerAdviceKind.Level l => l.Db > 0
            ? L.F("%@ is %.1f dB louder than the others", Name(l.Channel), l.Db)
            : L.F("%@ is %.1f dB quieter than the others", Name(l.Channel), -l.Db),
        SpeakerAdviceKind.Distance d => d.Meters > 0
            ? L.F("%@ is %.1f m farther than the others", Name(d.Channel), d.Meters)
            : L.F("%@ is %.1f m nearer than the others", Name(d.Channel), -d.Meters),
        SpeakerAdviceKind.Subwoofer s => L.F("Subwoofer %+.1f dB against the speakers", s.Db),
        SpeakerAdviceKind.Noisy n => L.F("The measurement is noisy (SNR %.0f dB)", n.SnrDb),
        SpeakerAdviceKind.FewPositions f => L.F("Only %d measurement position(s)", f.Count),
        _ => "",
    };

    private static string AdviceDetail(SpeakerAdviceKind kind) => L.T(kind switch
    {
        SpeakerAdviceKind.Polarity =>
            "It moves out while the front left moves in, which thins the sound between them. Check its cable (+ and − swapped) and the receiver's settings, then measure again.",
        SpeakerAdviceKind.Silent =>
            "It may not play at all, or play from another channel. Run the wiring check, then measure again.",
        SpeakerAdviceKind.Level =>
            "Tapline trims it, but a difference this large is worth fixing with the receiver's channel level or the speaker's own level control.",
        SpeakerAdviceKind.Distance =>
            "Tapline delays the nearer speakers to match. Check the speaker's placement, and that the microphone heard this speaker and not a reflection.",
        SpeakerAdviceKind.Subwoofer =>
            "Its level is a matter of taste: set it with the subwoofer's gain or the receiver's subwoofer level, where most people like it a few dB above the speakers. Through a receiver the LFE channel also plays 10 dB hotter by design.",
        SpeakerAdviceKind.Noisy =>
            "Turn the volume up a little, close windows and switch off fans, then measure again.",
        _ => "Measure 3 to 5 positions within about 30 cm of your head for a more reliable correction.",
    });

    // MARK: - Step 5: correct

    private FrameworkElement CorrectionCard()
    {
        var chart = scope.Dynamic(() => (fits, chartChannel, showPositions, alignSpeakers), inner =>
        {
            if (fits is not { } f || (f.SpeakerOn(chartChannel ?? (speakers.Count > 0 ? speakers[0] : 0)) ?? f.Speakers.FirstOrDefault()) is not { } shown)
            {
                return new Border
                {
                    MinHeight = 120,
                    Child = Ui.Secondary(L.T("Measure at least once to see each speaker's response here.")).CenterBoth(),
                };
            }
            var picker = inner.Combo<int>(() => f.Speakers.Select(s => s.Channel).ToList(),
                c => f.SpeakerOn(c) is { } s ? $"{s.Position.Abbreviation()} · {RoomAudio.SpeakerName(s.Position)}" : "",
                () => shown.Channel, c =>
                {
                    chartChannel = c;
                    Refresh();
                }, 240);
            var deviation = RoomPage.Tabular(Ui.Text(L.F("Deviation %.1f → %.1f dB", shown.Result.ErrorBeforeDb, shown.Result.ErrorAfterDb), 11.5, null, Theme.Secondary));
            var overlay = shown.ChartOverlay();
            var graph = new MeasurementChart { Height = 200 };
            graph.Set(shown.Result.Frequencies, shown.Result.Measurement, shown.Result.Target, shown.Result.Predicted);
            graph.SetPositions(overlay.Low, overlay.High, showPositions ? overlay.Positions : []);
            var several = overlay.Positions.Length > 0;
            var legend = Ui.HStack(14,
                RoomPage.Legend(Brushes.Gray, "Measured"),
                RoomPage.Legend(Brushes.White, "Target", dashed: true),
                RoomPage.Legend(Theme.Accent, "With EQ"),
                several ? RoomPage.Legend(MeasurementChart.SpreadBrush, "Spread", band: true)
                    .Tip(L.T("One standard deviation between the measured positions. Where it is wide, the response changes from spot to spot and the average is less certain.")) : null,
                several ? inner.Check(L.T("Positions"), () => showPositions, v =>
                {
                    showPositions = v;
                    Refresh();
                }, tip: L.T("Show the curve of each measured position")) : null,
                Ui.Spacer(),
                alignSpeakers ? RoomPage.Tabular(Ui.Text(L.F("Trim %@ · delay %.2f ms", Format.Gain(shown.TrimDb), shown.DelayMs), 11.5, null, Theme.Secondary)) : null);
            return Ui.VStack(10,
                Ui.HStack(12, picker, Ui.Spacer(), deviation),
                graph,
                legend,
                RoomPage.FilterList(shown.Result.Bands),
                shown.Position.IsLfe() ? Ui.Caption(L.T("The subwoofer is corrected below 120 Hz and never boosted above it.")) : null);
        });

        // "Correct below" on a log scale, rounded as displayed (as on the stereo steps).
        FrameworkElement CorrectBelowSlider(string title, Func<double> get, Action<double> set) =>
            scope.LabeledSlider(L.T(title), Math.Log10(RoomPage.CorrectBelowMin), Math.Log10(RoomPage.CorrectBelowMax),
                () => Math.Log10(Math.Clamp(get(), RoomPage.CorrectBelowMin, RoomPage.CorrectBelowMax)),
                (v, _) => set(RoomPage.RoundedFrequency(Math.Pow(10, v))),
                _ => Format.Frequency(Math.Round(get(), MidpointRounding.AwayFromZero)), titleWidth: TitleWidth);
        var sliders = Ui.VStack(6,
            CorrectBelowSlider("Correct below (front)", () => page.CorrectBelow, v => page.CorrectBelow = v),
            CorrectBelowSlider("Correct below (surround, height)", () => surroundCorrectBelow, SetSurroundCorrectBelow),
            scope.LabeledSlider(L.T("Max boost"), 0, 6, () => page.MaxBoost, (v, _) => page.MaxBoost = RoomPage.Step(v), Format.Gain, 0.5, titleWidth: TitleWidth),
            scope.LabeledSlider(L.T("Bass"), -3, 8, () => page.Bass, (v, _) => page.Bass = RoomPage.Step(v), Format.Gain, 0.5, titleWidth: TitleWidth),
            scope.LabeledSlider(L.T("Tilt"), -1.5, 0.5, () => page.Tilt, (v, _) => page.Tilt = RoomPage.Step(v),
                v => Printf.Format("%+.1f dB/oct", [v]), 0.1, titleWidth: TitleWidth),
            FiltersRow());

        var presets = Ui.HStack(8,
            Ui.Button(L.T("Flat"), () => SetTarget(0, 0)),
            Ui.Button(L.T("Room curve"), () => SetTarget(4, -0.6),
                tip: L.T("A gentle bass lift and treble roll-off, how well-tuned speakers usually sound in a room")),
            Ui.Button(L.T("Defaults"), () =>
            {
                SetSurroundCorrectBelow(DefaultSurroundCorrectBelow);
                page.CorrectBelow = 500;
            }, tip: L.T("500 Hz for the front speakers, 300 Hz for surrounds and heights")),
            Ui.Spacer(),
            scope.Check(L.T("Align levels and delays"), () => alignSpeakers, v =>
            {
                alignSpeakers = v;
                page.Save("roomAlignSpeakers", v);
                Refresh();
            }, tip: L.T("Apply each speaker's trim and delay with its correction")));

        var actions = scope.Dynamic(() => (applied, OutputName, fits != null, Corrected), inner =>
        {
            var none = fits == null;
            var row = Ui.HStack(8,
                Ui.Button(applied ? L.T("Applied") : L.F("Apply to %@", OutputName), Apply, applied ? Glyphs.Check : Glyphs.Wand, accent: true).Disabled(none),
                Ui.Button(L.T("Copy Filters as Text"), CopyFilters).Disabled(none),
                Ui.Spacer());
            if (Corrected)
            {
                row.Children.Add(inner.LabeledSwitch(L.T("Speaker correction"), () => model.Settings.Speakers.CorrectionEnabled, model.SetSpeakerCorrectionEnabled,
                    tip: L.T("Switches every speaker's correction, trim and delay on or off")));
                row.Children.Add(Ui.Button(L.T("Remove"), () =>
                {
                    model.ClearSpeakerCorrection();
                    applied = false;
                    Refresh();
                }));
            }
            return row;
        });

        var message = RoomPage.Visible(scope, scope.Text(() => correctionMessage ?? "", 13, null, Theme.Secondary, wrap: true), () => correctionMessage != null);

        return Ui.Card(L.T("5. Correct"),
            L.T("Each speaker gets its own room correction, after the EQ and effects. Cuts room resonances first; dips are only lifted a little."),
            Glyphs.Wand,
            chart, sliders, presets, actions, message);
    }

    private const double TitleWidth = 230;

    /// Whether the current output plays a correction measured on this layout.
    private bool Corrected => model.Settings.Speakers.Layout?.Equals(layout) == true && model.Settings.Speakers.Corrections.Count > 0;

    private FrameworkElement FiltersRow()
    {
        var label = Ui.Text(L.T("Filters"));
        label.VerticalAlignment = VerticalAlignment.Center;
        var count = RoomPage.Tabular(scope.Text(() => page.FilterCount.ToString(CultureInfo.InvariantCulture)));
        count.Width = 22;
        count.TextAlignment = TextAlignment.Center;
        count.VerticalAlignment = VerticalAlignment.Center;
        var minus = scope.Bind(Ui.Button("−", () => page.FilterCount -= 1, small: true), b => b.IsEnabled = page.FilterCount > 2);
        var plus = scope.Bind(Ui.Button("+", () => page.FilterCount += 1, small: true), b => b.IsEnabled = page.FilterCount < 12);
        var value = Ui.HStack(6, minus, count, plus);
        value.VerticalAlignment = VerticalAlignment.Center;
        var row = Ui.Columns((label, Ui.Px(TitleWidth)), (value, Ui.Star()));
        row.MinHeight = 28;
        return row;
    }

    private void SetSurroundCorrectBelow(double value)
    {
        surroundCorrectBelow = value;
        page.Save("roomSurroundCorrectBelow", value);
        Refresh();
    }

    private void SetTarget(double bass, double tilt)
    {
        page.Bass = bass;
        page.Tilt = tilt;
    }

    private void Apply()
    {
        if (fits is not { } f) return;
        model.ApplySpeakerCorrection(f.Corrections, layout);
        applied = true;
        correctionMessage = L.F("Applied to %@: every speaker's filters, trim and delay. Measurements bypass Tapline, so measuring again still shows the room without them.", OutputName);
        Refresh();
    }

    private void CopyFilters()
    {
        if (fits is not { } f) return;
        Ui.Copy(ParametricEqFormat.ExportSpeakers(f.Corrections, layout.Name, SpeakerSetup.HeadroomDb(f.Corrections)));
        correctionMessage = L.T("Copied, with a Channel section per speaker for Equalizer APO.");
        Refresh();
    }

    // MARK: - Fitting

    private sealed record Overlay(double[][] Positions, double[] Low, double[] High);

    private sealed class Fits(IReadOnlyList<Fits.Speaker> speakers, List<SpeakerAdvice> advice, IReadOnlyList<SpeakerCorrection> corrections)
    {
        /// <param name="LevelDb">In its own band, relative to the reference speaker's.</param>
        /// <param name="Distance">Metres farther than the reference speaker.</param>
        public sealed record Speaker(int Channel, SpeakerPosition Position, EqFitter.Result Result, double LevelDb, double? Distance,
                                     bool? OppositePolarity, double TrimDb, double DelayMs, double[][] Shapes)
        {
            /// The measured positions at the chart's level, and one standard deviation around their mean.
            public Overlay ChartOverlay()
            {
                var f = Result.Frequencies;
                if (RoomResponse.Spread(Shapes) is not { } spread || spread.Length != f.Length) return new Overlay([], [], []);
                var (lo, hi) = Position.IsLfe() ? (30.0, 80.0) : (200.0, 2000.0);
                double level = 0;
                var mids = 0;
                for (var i = 0; i < f.Length; i++)
                {
                    if (f[i] < lo || f[i] > hi) continue;
                    level += Result.Measurement[i];
                    mids++;
                }
                level /= Math.Max(mids, 1);
                var positions = Shapes.Select(shape => shape.Select(v => v + level).ToArray()).ToArray();
                var low = new double[f.Length];
                var high = new double[f.Length];
                for (var i = 0; i < f.Length; i++)
                {
                    var mean = positions.Average(p => p[i]);
                    low[i] = mean - spread[i];
                    high[i] = mean + spread[i];
                }
                return new Overlay(positions, low, high);
            }
        }

        public IReadOnlyList<Speaker> Speakers { get; } = speakers;
        public List<SpeakerAdvice> Advice { get; } = advice;
        /// What Apply and Copy Filters as Text use.
        public IReadOnlyList<SpeakerCorrection> Corrections { get; } = corrections;

        public Speaker? SpeakerOn(int channel) => Speakers.FirstOrDefault(s => s.Channel == channel);
    }

    private sealed record FitKey(string Runs, double CorrectBelow, double SurroundCorrectBelow, double MaxBoost, double Tilt, double Bass,
                                 int Filters, bool Align, bool Calibrated);

    /// Starts a refit when anything the fit depends on changed. Returns whether it did.
    public bool CheckFit()
    {
        var key = new FitKey(string.Join(",", Runs.Select(r => r.Id)), Math.Round(page.CorrectBelow, MidpointRounding.AwayFromZero),
                             Math.Round(surroundCorrectBelow, MidpointRounding.AwayFromZero), page.MaxBoost, page.Tilt, page.Bass, page.FilterCount,
                             alignSpeakers, room.Calibration != null);
        if (key == lastKey) return false;
        lastKey = key;
        _ = Refit();
        return true;
    }

    private async Task Refit()
    {
        fitCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        fitCancel = cancel;
        applied = false;
        correctionMessage = null;
        var averages = room.SpeakerAverages(layout);
        if (averages.Count == 0)
        {
            fits = null;
            return;
        }
        try
        {
            await Task.Delay(150, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var baseOptions = new EqFitter.Options(maxFilters: page.FilterCount, bassAdjustDb: page.Bass, tiltDbPerOctave: page.Tilt)
        {
            MaxBoostDb = page.MaxBoost,
            MaxCutDb = 15,
            MaxQ = 10,
            LowSmoothingOctaves = 1.0 / 12,
        };
        double front = page.CorrectBelow, surround = surroundCorrectBelow;
        var align = alignSpeakers;
        var runs = Runs;
        var positions = runs.Count;
        var snr = runs.Count == 0 ? 0 : runs.Min(r => r.SnrDb);

        Fits fitted;
        try
        {
            fitted = await Task.Run(() =>
            {
                var target = RoomResponse.FlatTarget();
                var reference = averages.FirstOrDefault(a => a.Position == SpeakerPosition.FrontLeft)
                                ?? averages.FirstOrDefault(a => !a.Position.IsLfe()) ?? averages[0];
                var referenceLevel = SpeakerAlignment.Level(reference.Response, reference.Position);
                var referenceArrival = reference.Arrival ?? 0;
                var levels = averages.Select(a => SpeakerAlignment.Level(a.Response, a.Position)).ToArray();
                var alignment = SpeakerAlignment.Align([.. averages.Select((a, i) => new SpeakerAlignment.Speaker(a.Position, levels[i], a.Arrival))]);
                var fittedSpeakers = new Fits.Speaker[averages.Count];
                var corrections = new SpeakerCorrection[averages.Count];
                Parallel.For(0, averages.Count, new ParallelOptions { CancellationToken = cancel.Token }, i =>
                {
                    var a = averages[i];
                    EqFitter.Options o;
                    if (a.Position.IsLfe())
                    {
                        // The LFE channel carries nothing above 120 Hz: correct below it, never boost above it.
                        o = new EqFitter.Options(maxFilters: Math.Min(baseOptions.MaxFilters, 6))
                        {
                            MaxBoostDb = baseOptions.MaxBoostDb,
                            MaxCutDb = baseOptions.MaxCutDb,
                            MaxQ = baseOptions.MaxQ,
                            LowSmoothingOctaves = baseOptions.LowSmoothingOctaves,
                            CorrectOnlyBelow = SpeakerSetup.LfeBoostCeiling,
                            NoBoostAbove = SpeakerSetup.LfeBoostCeiling,
                            NoBoostBelow = 30,
                            LevelMatchRange = (30, 80),
                        };
                    }
                    else
                    {
                        o = baseOptions with
                        {
                            CorrectOnlyBelow = a.Position.Group() == SpeakerGroup.Front ? front : surround,
                            NoBoostBelow = RoomResponse.LowFrequencyLimit(a.Response) * 1.41,
                        };
                    }
                    var result = EqFitter.Fit(a.Response, target, o);
                    var trim = align ? alignment[i].TrimDb : 0;
                    var delay = align ? alignment[i].DelayMs : 0;
                    fittedSpeakers[i] = new Fits.Speaker(a.Channel, a.Position, result, levels[i] - referenceLevel,
                                                         a.Arrival is { } arrival ? (arrival - referenceArrival) * SpeakerAlignment.SpeedOfSound : null,
                                                         a.OppositePolarity, trim, delay,
                                                         a.Responses.Count >= 2 ? RoomResponse.Shapes(a.Responses) : []);
                    corrections[i] = new SpeakerCorrection(a.Channel, a.Position, result.Bands, trim, delay);
                });
                var advice = SpeakerAdvice.Advise([.. averages.Select((a, i) => new SpeakerAdvice.Speaker(a.Channel, a.Position, levels[i], a.Arrival, a.OppositePolarity))],
                                                  positions, snr);
                return new Fits(fittedSpeakers, advice, corrections);
            });
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            if (cancel.IsCancellationRequested) return;
            CrashLog.Write(e);
            correctionMessage = e.Message;
            Refresh();
            return;
        }
        if (cancel.IsCancellationRequested) return;
        fits = fitted;
        Refresh();
    }
}
