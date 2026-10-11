using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Tapline.Audio;
using Tapline.Core;
using Tapline.Infrastructure;
using Tapline.Model;
using Tapline.UI.Controls;

namespace Tapline.UI.Pages;

/// Room correction: measure the speakers with a microphone, check the setup,
/// and fit an EQ for Tapline or for the EQ built into an RME ADI-2 DAC.
sealed class RoomPage : PageBase
{
    /// Where the correction runs.
    private enum Hardware { Tapline, Adi2 }

    /// Segoe MDL2 "CheckList".
    private const string ChecklistGlyph = "";

    private readonly RoomAudio room;

    // Persisted in Model.Prefs under the macOS @AppStorage names.
    private string inputUid;
    private int inputChannel;
    private RoomSpeakers speakers;
    private double correctBelow;
    private double maxBoost;
    private double tilt;
    private double bass;
    private int filterCount;
    private Hardware hardware;
    private bool separate;

    private Fits? fits;
    private bool applied;
    private EqChannels chartChannel = EqChannels.Left;
    private string? correctionMessage;
    private FitKey? lastKey;
    private CancellationTokenSource? fitCancel;

    public RoomPage(AppModel model) : base(model)
    {
        var prefs = model.Prefs;
        inputUid = prefs.Get("roomInputUID", "");
        inputChannel = Math.Max(0, prefs.Get("roomInputChannel", 0));
        speakers = RoomAudio.ParseSpeakers(prefs.Get("roomSpeakers", "stereo"));
        correctBelow = Math.Clamp(prefs.Get("roomCorrectBelow", 500.0), 100, 2000);
        maxBoost = Math.Clamp(prefs.Get("roomMaxBoost", 3.0), 0, 6);
        tilt = Math.Clamp(prefs.Get("roomTilt", 0.0), -1.5, 0.5);
        bass = Math.Clamp(prefs.Get("roomBass", 0.0), -3, 8);
        filterCount = Math.Clamp(prefs.Get("roomFilters", 8), 2, 12);
        hardware = prefs.Get("roomHardware", "tapline") == "adi2" ? Hardware.Adi2 : Hardware.Tapline;
        separate = prefs.Get("roomSeparate", true);

        room = new RoomAudio(model);
        room.Changed += Update;

        Content = Ui.Page(Ui.VStack(16,
            Intro(),
            MicrophoneCard(),
            MeasureCard(),
            CheckCard(),
            CorrectionCard()), 980);
        CheckFit();
    }

    private AudioDevice? Input => Model.Devices.Inputs.FirstOrDefault(d => d.Id == inputUid) ?? Model.Devices.DefaultInput;

    private string OutputName => Model.CurrentOutput?.Name ?? "";

    private bool CanRun => !room.IsBusy && Input != null && Model.CurrentOutput != null;

    /// After a change of the page's own state: refit if needed, then redraw.
    private void Update()
    {
        CheckFit();
        Scope.Refresh();
    }

    protected override void OnModelChanged()
    {
        if (CheckFit()) Scope.Refresh();
    }

    private void Save<T>(string key, T value) => Model.Prefs.Set(key, value);

    private static TextBlock Tabular(TextBlock t)
    {
        Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    private static T Visible<T>(UiScope scope, T element, Func<bool> visible) where T : UIElement =>
        scope.Bind(element, e => e.Visibility = visible() ? Visibility.Visible : Visibility.Collapsed);

    // MARK: - Intro

    private static FrameworkElement Intro()
    {
        static FrameworkElement Row(string glyph, string text) =>
            Ui.HStack(8, Ui.Glyph(glyph, 13, Theme.Secondary).Width(18).Top().Margin(0, 2, 0, 0), Ui.Secondary(L.T(text), 12.5).Filling());
        return Ui.Card(L.T("Room Correction"),
            L.T("Measure your speakers where you sit, fix what the setup can fix, and correct the room's bass resonances. The rest of the sound is left to your speakers."),
            Glyphs.Speakers,
            Ui.VStack(4,
                Row(Glyphs.Mute, "Pause music and keep the room quiet while measuring."),
                Row(Glyphs.Microphone, "Use a measurement or studio condenser microphone at ear height, pointing between the speakers. A calibration file improves accuracy."),
                Row(Glyphs.Equalizer, "Switch off any EQ, Bass/Treble or Loudness in the DAC (such as the RME ADI-2) while measuring, so Tapline sees the speakers as they are."),
                Row(Glyphs.Warning, "Measurements play at the current output volume. Start low.")));
    }

    // MARK: - Step 1

    private FrameworkElement MicrophoneCard()
    {
        var inputPicker = Scope.Combo<AudioDevice>(() => Model.Devices.Inputs, d => d.Name, () => Input, d =>
        {
            inputUid = d.Id;
            inputChannel = 0;
            Save("roomInputUID", inputUid);
            Save("roomInputChannel", inputChannel);
            Update();
        }, 280);
        var channelPicker = Scope.Combo<int>(() => Enumerable.Range(0, Math.Max(Input?.Channels ?? 1, 1)).ToList(),
            i => (i + 1).ToString(CultureInfo.InvariantCulture), () => inputChannel, i =>
            {
                inputChannel = i;
                Save("roomInputChannel", inputChannel);
                Update();
            }, 90);

        var calibration = Scope.Dynamic(() => room.CalibrationName, _ =>
        {
            var row = Ui.HStack(8);
            if (room.CalibrationName is { } name)
            {
                row.Children.Add(Ui.Glyph(Glyphs.Check, 13, Theme.Accent));
                row.Children.Add(Ui.Text(name).MaxW(300).Tip(name));
                row.Children.Add(Ui.Button(L.T("Remove"), room.ClearCalibration, small: true));
            }
            else
            {
                row.Children.Add(Ui.Text(L.T("None"), 13, null, Theme.Tertiary));
            }
            row.Children.Add(Ui.Button(L.T("Load File…"), LoadCalibration, small: true));
            return row;
        });

        var checkLevel = Ui.Button(L.T("Check Level"), CheckLevel, Glyphs.Spectrum);
        Scope.Bind(checkLevel, b => b.IsEnabled = CanRun);
        var meter = new MeterBar { Horizontal = true, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        Scope.Bind(meter, m => m.Set((float)Math.Pow(10, room.InputPeakDb / 20), (float)Math.Pow(10, room.InputRmsDb / 20), 0));
        var levelText = Tabular(Scope.Text(() => room.IsBusy ? Printf.Format("%.0f dB", [Math.Max(room.InputPeakDb, -99)]) : "—", 11.5, null, Theme.Secondary));
        levelText.Width = 48;
        levelText.TextAlignment = TextAlignment.Right;
        levelText.VerticalAlignment = VerticalAlignment.Center;
        var levelBar = Ui.HStack(8, meter.Filling(), levelText).Width(320);

        return Ui.Card(L.T("1. Microphone"), null, Glyphs.Microphone,
            Ui.Form(110,
                (L.T("Input"), inputPicker),
                (L.T("Channel"), channelPicker),
                (L.T("Calibration"), calibration)),
            Ui.HStack(12, checkLevel, levelBar),
            Ui.Caption(L.T("Plays pink noise for three seconds. Aim for peaks between −30 and −10 dB on the microphone.")));
    }

    private void CheckLevel()
    {
        if (Model.CurrentOutput is not { } output || Input is not { } input) return;
        _ = room.CheckLevel(output, input, inputChannel, speakers);
    }

    private void LoadCalibration()
    {
        var path = Dialogs.OpenFile($"{L.T("Calibration")} (*.txt;*.cal;*.frd;*.csv)|*.txt;*.cal;*.frd;*.csv|{L.T("All files")} (*.*)|*.*");
        if (path == null) return;
        if (!room.LoadCalibration(path))
        {
            room.LastMessage = L.T("No calibration data found in that file.");
            room.MicrophoneBlocked = false;
        }
        Update();
    }

    // MARK: - Step 2

    private FrameworkElement MeasureCard()
    {
        var picker = Scope.Segmented<RoomSpeakers>(
            [
                (RoomSpeakers.Stereo, RoomAudio.Title(RoomSpeakers.Stereo)),
                (RoomSpeakers.Both, RoomAudio.Title(RoomSpeakers.Both)),
                (RoomSpeakers.Left, RoomAudio.Title(RoomSpeakers.Left)),
                (RoomSpeakers.Right, RoomAudio.Title(RoomSpeakers.Right)),
            ],
            () => speakers, v =>
            {
                speakers = v;
                Save("roomSpeakers", RoomAudio.Key(v));
                Update();
            }).Width(360);

        var measure = Ui.Button(L.T("Measure"), Measure, Glyphs.Play, accent: true);
        Scope.Bind(measure, b => b.IsEnabled = CanRun);
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Width = 140, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        Scope.Bind(progress, p =>
        {
            p.Visibility = room.Phase == RoomPhase.Measuring ? Visibility.Visible : Visibility.Collapsed;
            p.Value = room.Progress;
        });
        var spinner = new ProgressBar { Width = 60, Height = 4, VerticalAlignment = VerticalAlignment.Center };
        var analyzing = Ui.HStack(6, spinner, Ui.Text(L.T("Analyzing…"), 11.5, null, Theme.Secondary));
        Scope.Bind(analyzing, a =>
        {
            var on = room.Phase == RoomPhase.Analyzing;
            a.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (spinner.IsIndeterminate != on) spinner.IsIndeterminate = on;
        });
        var clear = Visible(Scope, Ui.Button(L.T("Clear"), () =>
        {
            room.ClearMeasurements();
            fits = null;
            Update();
        }), () => room.Measurements.Count > 0);

        var explanation = Scope.Text(() => speakers == RoomSpeakers.Stereo
            ? L.T("Left & right: an 8-second sweep on each speaker, then three short clicks for timing (about 22 seconds). Needed for balance, distance and polarity checks, and for correcting each speaker separately.")
            : L.T("One 8-second sweep on the chosen speakers."), 11.5, null, Theme.Secondary, wrap: true);

        var message = Visible(Scope, Scope.Text(() => room.LastMessage ?? "", 13, null, Theme.Secondary, wrap: true), () => room.LastMessage != null);
        var privacy = Visible(Scope, Ui.Button(L.T("Open Microphone Settings"), RoomAudio.OpenMicrophoneSettings, Glyphs.OpenExternal, small: true),
            () => room.MicrophoneBlocked);

        var rows = Visible(Scope, Scope.Dynamic(() => string.Join(",", room.Measurements.Select(m => m.Id)), _ =>
        {
            if (room.Measurements.Count == 0) return null;
            var list = Ui.VStack(4);
            for (var index = 0; index < room.Measurements.Count; index++)
            {
                var m = room.Measurements[index];
                var warn = m.SnrDb < 20 || m.PeakDb > -1;
                var id = m.Id;
                list.Children.Add(Ui.HStack(10,
                    Tabular(Ui.Text("#" + (index + 1).ToString(CultureInfo.InvariantCulture), 13, null, Theme.Secondary)),
                    Ui.Text(RoomAudio.Title(m.Speakers)),
                    Ui.Text(m.Date.ToString("t", CultureInfo.CurrentCulture), 13, null, Theme.Secondary),
                    Tabular(Ui.Text(L.F("SNR %.0f dB · peak %.0f dB", m.SnrDb, m.PeakDb), 11.5, null, warn ? Theme.Warning : Theme.Secondary)),
                    m.ArrivalDifference is { } dt ? Tabular(Ui.Text(L.F("R − L %+.2f ms", dt * 1000), 11.5, null, Theme.Secondary)) : null,
                    Ui.Spacer(),
                    Ui.IconButton(Glyphs.Delete, () => room.Remove(id), size: 12)));
            }
            return list;
        }), () => room.Measurements.Count > 0);

        return Ui.Card(L.T("2. Measure"), L.T("Measure a few positions around your head; Tapline averages them."), Glyphs.Spectrum,
            Ui.HStack(12, picker, measure, progress, analyzing, Ui.Spacer(), clear),
            explanation,
            Visible(Scope, Ui.HStack(10, message.Filling(), privacy), () => room.LastMessage != null || room.MicrophoneBlocked),
            rows);
    }

    private void Measure()
    {
        if (Model.CurrentOutput is not { } output || Input is not { } input) return;
        _ = room.Measure(output, input, inputChannel, speakers);
    }

    // MARK: - Step 3

    private FrameworkElement CheckCard()
    {
        var content = Scope.Dynamic(() => fits, inner =>
        {
            if (fits is not { } f)
                return Ui.Secondary(L.T("Measure to see suggestions for balance, speaker distance, polarity, subwoofer level and the speakers' room switches."));
            if (f.Advice.Count == 0)
            {
                return Ui.HStack(8, Ui.Glyph(Glyphs.Check, 14, Theme.Accent),
                    Ui.Text(L.T("Nothing to fix at the speakers. The setup looks good."), 13, null, Theme.Accent, wrap: true).Filling());
            }
            var list = Ui.VStack(10);
            foreach (var advice in f.Advice) list.Children.Add(AdviceRow(inner, advice));
            return list;
        });
        return Ui.Card(L.T("3. Check the Setup"), L.T("Fix these at the speakers first: the EQ then has less to do and works better."), ChecklistGlyph, content);
    }

    private FrameworkElement AdviceRow(UiScope scope, RoomAdvice advice)
    {
        var (glyph, brush) = advice.Severity switch
        {
            RoomAdviceSeverity.Warning => (Glyphs.Warning, Theme.Warning),
            RoomAdviceSeverity.Suggestion => (Glyphs.Lightbulb, Theme.Accent),
            _ => (Glyphs.Info, Theme.Secondary),
        };
        var text = Ui.VStack(3,
            Ui.Text(AdviceTitle(advice.Kind), 13, FontWeights.SemiBold, null, wrap: true),
            Ui.Caption(AdviceDetail(advice.Kind)));
        if (advice.Kind is RoomAdviceKind.Balance b) text.Children.Add(BalanceAction(scope, b.Louder, b.Db));
        return Ui.HStack(10, Ui.Glyph(glyph, 14, brush).Width(18).Top().Margin(0, 1, 0, 0), text.Filling());
    }

    /// Balance that turns the louder side down by `db` (Tapline's law: the other side is scaled by 1 - |b|).
    private static double BalanceFor(EqChannels louder, double db)
    {
        var cut = 1 - Math.Pow(10, -db / 20);
        return louder == EqChannels.Right ? -cut : cut;
    }

    private FrameworkElement BalanceAction(UiScope scope, EqChannels louder, double db)
    {
        var wanted = BalanceFor(louder, db);
        bool Balanced() => Math.Abs(Model.Settings.Effects.Balance - wanted) < 0.01;
        return scope.Dynamic(() => Balanced(), _ => Balanced()
            ? Ui.HStack(6, Ui.Glyph(Glyphs.Check, 11, Theme.Accent).Top().Margin(0, 2, 0, 0),
                Ui.Text(L.T("Balanced in Tapline. Measurements bypass Tapline, so this stays listed."), 11.5, null, Theme.Accent, wrap: true).Filling())
            : Ui.Button(L.T("Set Balance in Tapline"), () => Model.Mutate(s => s.Effects = s.Effects with { Balance = wanted }), small: true).Margin(0, 2, 0, 0));
    }

    private static string Side(EqChannels c) => c == EqChannels.Right ? L.T("right") : L.T("left");

    private static string AdviceTitle(RoomAdviceKind kind) => kind switch
    {
        RoomAdviceKind.Polarity => L.T("Left and right have opposite polarity"),
        RoomAdviceKind.Noisy n => L.F("The measurement is noisy (SNR %.0f dB)", n.SnrDb),
        RoomAdviceKind.Balance b => L.F("The %@ speaker is %.1f dB louder", Side(b.Louder), b.Db),
        RoomAdviceKind.Distance d => L.F("The %@ speaker is %.0f cm farther away", Side(d.Farther), d.Centimeters),
        RoomAdviceKind.ChannelMismatch m => L.F("Left and right differ by up to %.0f dB around %@", m.Db, Format.Hz(m.Frequency)),
        RoomAdviceKind.Subwoofer s => s.Db > 0
            ? L.F("Too much deep bass (%+.1f dB below 90 Hz)", s.Db)
            : L.F("Too little deep bass (%+.1f dB below 90 Hz)", s.Db),
        RoomAdviceKind.BassTilt t => L.F("Boomy lower bass (%+.1f dB from 100 to 400 Hz)", t.Db),
        RoomAdviceKind.Desktop d => L.F("Desk bounce around %@ (%+.1f dB)", Format.Hz(d.Frequency), d.Db),
        RoomAdviceKind.RoomMode r => L.F("Room resonance at %@ (%+.1f dB)", Format.Hz(r.Frequency), r.Db),
        RoomAdviceKind.Cancellation c => L.F("Deep dip at %@ (−%.0f dB)", Format.Hz(c.Frequency), c.DepthDb),
        RoomAdviceKind.Treble t => t.Db > 0
            ? L.F("Bright treble (%+.1f dB above 4 kHz)", t.Db)
            : L.F("Dull treble (%+.1f dB above 4 kHz)", t.Db),
        RoomAdviceKind.FewPositions f => L.F("Only %d measurement position(s)", f.Count),
        _ => "",
    };

    private static string AdviceDetail(RoomAdviceKind kind) => kind switch
    {
        RoomAdviceKind.Polarity =>
            L.T("One speaker moves out while the other moves in, which thins the bass and blurs the centre. Check the cable wiring (XLR pins 2 and 3) and any polarity or phase switch, then measure again."),
        RoomAdviceKind.Noisy =>
            L.T("Turn the volume up a little, close windows and switch off fans, then measure again."),
        RoomAdviceKind.Balance b =>
            L.F("Turn that speaker down by %.1f dB with its level control, or set the balance in the DAC (on the RME ADI-2: VOL screen, Balance).", b.Db),
        RoomAdviceKind.Distance d =>
            L.F("Move the %@ speaker about %.0f cm closer, or the other one back, so both reach you at the same time. The centre image gets sharper.", Side(d.Farther), d.Centimeters),
        RoomAdviceKind.ChannelMismatch =>
            L.T("Normal near walls and corners. Correcting left and right separately takes care of it."),
        RoomAdviceKind.Subwoofer s => s.Db > 0
            ? L.F("Turn the subwoofer level down by about %.0f dB before correcting, so the EQ has less to cut.", Math.Abs(s.Db))
            : L.F("Turn the subwoofer level up by about %.0f dB. Without a subwoofer this is where the speakers run out.", Math.Abs(s.Db)),
        RoomAdviceKind.BassTilt t =>
            L.F("Typical of speakers close to a wall. On Genelec monitors set the Bass Tilt switches to %.0f dB; other speakers have a similar bass or boundary switch.", t.SwitchDb),
        RoomAdviceKind.Desktop =>
            L.T("Sound bouncing off the desk or console adds a bump. On Genelec monitors switch on Desktop Control (−4 dB at 160 Hz). Stands, or moving the speakers to the front edge, also help."),
        RoomAdviceKind.RoomMode r =>
            L.F("Fits a room dimension of about %.1f m. The correction cuts it.", r.DimensionMeters),
        RoomAdviceKind.Cancellation c => c.NearCrossover
            ? L.T("Near the subwoofer crossover this is often the subwoofer and the speakers cancelling. Try the subwoofer's phase switch or another subwoofer position, then measure again. EQ cannot fill it.")
            : L.F("A reflection cancelling the direct sound, for example from a wall about %.0f cm behind the speakers or behind you. EQ cannot fill it: move the speakers or your seat by 20 to 30 cm and measure again.", c.QuarterWaveCentimeters),
        RoomAdviceKind.Treble t =>
            L.F("If it also sounds that way, set the speakers' Treble Tilt to %+.0f dB.", t.SwitchDb)
            + (t.Calibrated ? "" : " " + L.T("Without a calibration file this may be the microphone.")),
        RoomAdviceKind.FewPositions =>
            L.T("Measure 3 to 5 positions within about 30 cm of your head for a more reliable correction."),
        _ => "",
    };

    // MARK: - Step 4

    private FrameworkElement CorrectionCard()
    {
        var hardwarePicker = Scope.Segmented<Hardware>([(Hardware.Tapline, "Tapline"), (Hardware.Adi2, "RME ADI-2 DAC")], () => hardware, v =>
        {
            hardware = v;
            Save("roomHardware", v == Hardware.Adi2 ? "adi2" : "tapline");
            Update();
        }).Width(260);
        var separateToggle = Scope.Check(L.T("Correct left and right separately"), () => separate, v =>
        {
            separate = v;
            Save("roomSeparate", v);
            Update();
        }, tip: L.T("Needs a left & right measurement. On the ADI-2 this uses Dual EQ."));
        ToolTipService.SetShowOnDisabled(separateToggle, true);
        Scope.Bind(separateToggle, c => c.IsEnabled = room.AveragedLeft != null && room.AveragedRight != null);

        var runOn = Ui.HStack(12, Ui.Text(L.T("Run on"), 13, null, Theme.Secondary), hardwarePicker, separateToggle, Ui.Spacer());
        var where = Scope.Text(() => hardware == Hardware.Adi2
            ? L.T("Fits the ADI-2's own parametric EQ: five bands per channel (bands 4 and 5 from 200 Hz), 0.5 dB steps. The DAC then corrects every source, not only this PC.")
            : L.T("Tapline corrects what this PC plays, on the current output."), 11.5, null, Theme.Secondary, wrap: true);

        var chartPicker = Visible(Scope, Scope.Segmented<EqChannels>([(EqChannels.Left, L.T("Left")), (EqChannels.Right, L.T("Right"))],
            () => chartChannel, v =>
            {
                chartChannel = v;
                Update();
            }).Width(160), () => fits?.IsSeparate == true);

        var chart = Scope.Dynamic(() => (fits, chartChannel), _ =>
        {
            if (fits is not { } f)
            {
                return new Border
                {
                    MinHeight = 120,
                    Child = Ui.Secondary(L.T("Measure at least once to see the room response here.")).CenterBoth(),
                };
            }
            var shown = f.ResultFor(chartChannel);
            var graph = new MeasurementChart { Height = 200 };
            graph.Set(shown.Frequencies, shown.Measurement, shown.Target, shown.Predicted);
            var deviation = Tabular(Ui.Text(L.F("Deviation %.1f → %.1f dB", shown.ErrorBeforeDb, shown.ErrorAfterDb), 11.5, null, Theme.Secondary));
            var legend = Ui.HStack(14,
                Legend(Brushes.Gray, "Measured"),
                Legend(Brushes.White, "Target", dashed: true),
                Legend(Theme.Accent, "With EQ"),
                Ui.Spacer(),
                deviation);
            return Ui.VStack(10, graph, legend, FilterList(f.IsSeparate ? shown.Bands : f.Combined.Bands));
        });

        var sliders = Ui.VStack(6,
            Scope.LabeledSlider(L.T("Correct below"), 100, 2000, () => correctBelow, (v, _) =>
            {
                correctBelow = v;
                Save("roomCorrectBelow", v);
                Update();
            }, v => Format.Frequency(Math.Round(v, MidpointRounding.AwayFromZero)), titleWidth: 110),
            Scope.LabeledSlider(L.T("Max boost"), 0, 6, () => maxBoost, (v, _) =>
            {
                maxBoost = Step(v);
                Save("roomMaxBoost", maxBoost);
                Update();
            }, Format.Gain, 0.5, titleWidth: 110),
            Scope.LabeledSlider(L.T("Bass"), -3, 8, () => bass, (v, _) =>
            {
                bass = Step(v);
                Save("roomBass", bass);
                Update();
            }, Format.Gain, 0.5, titleWidth: 110),
            Scope.LabeledSlider(L.T("Tilt"), -1.5, 0.5, () => tilt, (v, _) =>
            {
                tilt = Step(v);
                Save("roomTilt", tilt);
                Update();
            }, v => Printf.Format("%+.1f dB/oct", [v]), 0.1, titleWidth: 110),
            FiltersRow());

        var presets = Ui.HStack(8,
            Ui.Button(L.T("Flat"), () => SetTarget(0, 0)),
            Ui.Button(L.T("Room curve"), () => SetTarget(4, -0.6),
                tip: L.T("A gentle bass lift and treble roll-off, how well-tuned speakers usually sound in a room")));

        var actions = Scope.Dynamic(() => (hardware, applied, OutputName, fits != null), _ =>
        {
            var row = Ui.HStack(8);
            var none = fits == null;
            if (hardware == Hardware.Adi2)
            {
                row.Children.Add(Ui.Button(L.T("Export for ADI-2 Remote…"), ExportAdi2, Glyphs.Download, accent: true).Disabled(none));
                var preview = Ui.Button(L.T("Preview in Tapline"), ApplyInTapline,
                    tip: L.T("Listen to the same correction through Tapline before loading it into the ADI-2")).Disabled(none);
                row.Children.Add(preview);
            }
            else
            {
                row.Children.Add(Ui.Button(applied ? L.T("Applied") : L.F("Apply to %@", OutputName), ApplyInTapline,
                    applied ? Glyphs.Check : Glyphs.Wand, accent: true).Disabled(none));
            }
            row.Children.Add(Ui.Button(L.T("Copy Filters as Text"), CopyFilters).Disabled(none));
            return row;
        });

        var message = Visible(Scope, Scope.Text(() => correctionMessage ?? "", 13, null, Theme.Secondary, wrap: true), () => correctionMessage != null);
        var adiHint = Visible(Scope, Ui.Caption(L.T("Load the correction either into the ADI-2 or in Tapline, not both, or the room is corrected twice.")),
            () => hardware == Hardware.Adi2);

        return Ui.Card(L.T("4. Correct"),
            L.T("Cuts room resonances first. Dips are only lifted a little: they come from sound cancelling itself and boosting them mostly wastes amplifier power."),
            Glyphs.Wand,
            runOn, where, chartPicker, chart, sliders, presets, actions, message, adiHint);
    }

    private FrameworkElement FiltersRow()
    {
        var label = Ui.Text(L.T("Filters"));
        label.VerticalAlignment = VerticalAlignment.Center;
        var value = Scope.Dynamic(() => hardware, inner =>
        {
            if (hardware == Hardware.Adi2) return Ui.Text(L.T("5 per channel (ADI-2)"), 13, null, Theme.Secondary);
            var count = Tabular(inner.Text(() => filterCount.ToString(CultureInfo.InvariantCulture)));
            count.Width = 22;
            count.TextAlignment = TextAlignment.Center;
            count.VerticalAlignment = VerticalAlignment.Center;
            var minus = inner.Bind(Ui.Button("−", () => SetFilterCount(filterCount - 1), small: true), b => b.IsEnabled = filterCount > 2);
            var plus = inner.Bind(Ui.Button("+", () => SetFilterCount(filterCount + 1), small: true), b => b.IsEnabled = filterCount < 12);
            return Ui.HStack(6, minus, count, plus);
        });
        value.VerticalAlignment = VerticalAlignment.Center;
        var row = Ui.Columns((label, Ui.Px(110)), (value, Ui.Star()));
        row.MinHeight = 28;
        return row;
    }

    /// A slider value on its 0.1 grid, without a negative zero ("-0.0 dB/oct").
    private static double Step(double v)
    {
        var r = Format.Round(v, 1);
        return r == 0 ? 0 : r;
    }

    private void SetFilterCount(int n)
    {
        filterCount = Math.Clamp(n, 2, 12);
        Save("roomFilters", filterCount);
        Update();
    }

    private void SetTarget(double newBass, double newTilt)
    {
        bass = newBass;
        tilt = newTilt;
        Save("roomBass", bass);
        Save("roomTilt", tilt);
        Update();
    }

    private static FrameworkElement Legend(Brush brush, string text, bool dashed = false) =>
        Ui.HStack(5, new LegendSwatch(brush, dashed), Ui.Text(L.T(text), 11.5, null, Theme.Secondary));

    /// The fitted filters, as they would be entered into a hardware EQ.
    private static FrameworkElement? FilterList(IReadOnlyList<EqBand> bands)
    {
        if (bands.Count == 0) return null;
        var grid = new AdaptiveGrid { MinItemWidth = 126, Spacing = 8 };
        for (var i = 0; i < bands.Count; i++)
        {
            var b = bands[i];
            var glyph = new FilterGlyph(b.Type, 14, 9, 1.2) { VerticalAlignment = VerticalAlignment.Center };
            var lines = Ui.VStack(1,
                Tabular(Ui.Text($"{i + 1} · {Format.Hz(b.Frequency)}", 11.5, FontWeights.SemiBold)),
                Tabular(Ui.Text($"{Format.Gain(b.Gain)} · Q {b.Q.ToString("F1", CultureInfo.InvariantCulture)}", 10.5, null, Theme.Secondary)));
            grid.Children.Add(new Border
            {
                Background = Theme.ControlFill,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 4, 7, 4),
                ToolTip = Format.TypeName(b.Type),
                Child = Ui.HStack(5, glyph, lines),
            });
        }
        return grid;
    }

    private void ApplyInTapline()
    {
        if (fits is not { } f) return;
        var name = OutputName;
        Model.ApplyCorrection(f.Bands, L.F("Room · %@", name), L.T("Room measurement"));
        applied = true;
        correctionMessage = L.F("Applied to %@. Measurements bypass Tapline, so measuring again still shows the room without this EQ.", name);
        Update();
    }

    private void CopyFilters()
    {
        if (fits is not { } f) return;
        Ui.Copy(ParametricEqFormat.Export(f.Bands, 0));
        correctionMessage = L.T("Copied.");
        Update();
    }

    private void ExportAdi2()
    {
        if (fits is not { } f) return;
        var name = L.T("Room correction");
        try
        {
            var text = f.IsSeparate
                ? Adi2Preset.Export(f.Left!.Bands, "Tapline Room L/R", f.Right!.Bands)
                : Adi2Preset.Export(f.Combined.Bands, "Tapline Room");
            if (Adi2Export.Save(text, name)) correctionMessage = Adi2Export.LoadHint;
        }
        catch (Adi2PresetException e)
        {
            correctionMessage = Adi2Export.Message(e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            correctionMessage = e.Message;
        }
        Update();
    }

    // MARK: - Fitting

    private sealed class Fits(EqFitter.Result combined, EqFitter.Result? left, EqFitter.Result? right, List<RoomAdvice> advice)
    {
        public EqFitter.Result Combined { get; } = combined;
        public EqFitter.Result? Left { get; } = left;
        public EqFitter.Result? Right { get; } = right;
        public List<RoomAdvice> Advice { get; } = advice;

        public bool IsSeparate => Left != null && Right != null;

        public EqFitter.Result ResultFor(EqChannels channel)
        {
            if (!IsSeparate) return Combined;
            return (channel == EqChannels.Right ? Right : Left) ?? Combined;
        }

        /// The correction as Tapline bands: per channel when separate.
        public List<EqBand> Bands => Left is { } l && Right is { } r
            ? [.. l.Bands.Select(b => b with { Channels = EqChannels.Left }), .. r.Bands.Select(b => b with { Channels = EqChannels.Right })]
            : Combined.Bands;
    }

    private sealed record FitKey(string Measurements, double CorrectBelow, double MaxBoost, double Tilt, double Bass,
                                 int Filters, Hardware Hardware, bool Separate, bool Calibrated);

    /// Starts a refit when anything the fit depends on changed (like `.task(id: fitKey)`). Returns whether it did.
    private bool CheckFit()
    {
        var key = new FitKey(string.Join(",", room.Measurements.Select(m => m.Id)), Math.Round(correctBelow, MidpointRounding.AwayFromZero),
                             maxBoost, tilt, bass, filterCount, hardware, separate, room.Calibration != null);
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
        if (room.AveragedResponse is not { } measured)
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

        var options = new EqFitter.Options(maxFilters: filterCount, bassAdjustDb: bass, tiltDbPerOctave: tilt)
        {
            CorrectOnlyBelow = correctBelow,
            MaxBoostDb = maxBoost,
            MaxCutDb = 15,
            MaxQ = 10,
            LowSmoothingOctaves = 1.0 / 12,
        };
        if (hardware == Hardware.Adi2) options = Adi2Preset.FitOptions(options);
        var baseOptions = options;

        var left = room.AveragedLeft;
        var right = room.AveragedRight;
        var perChannel = separate && left != null && right != null;
        var arrival = room.ArrivalDifference;
        var polarity = room.OppositePolarity;
        var positions = room.Measurements.Count;
        var snr = room.WorstSnrDb;
        var calibrated = room.Calibration != null;

        Fits fitted;
        try
        {
            fitted = await Task.Run(() =>
            {
                var target = RoomResponse.FlatTarget();
                // Each response gets its own low-frequency limit: no boosting below the
                // speakers' roll-off, nor in the half octave above it where they already strain.
                EqFitter.Result Fit(FrequencyResponse response)
                {
                    var o = baseOptions;
                    o.NoBoostBelow = RoomResponse.LowFrequencyLimit(response) * 1.41;
                    return EqFitter.Fit(response, target, o);
                }
                var combined = Fit(measured);
                var grid = combined.Frequencies;
                var advice = RoomAdvisor.Advise(new RoomAdvisor.Input(
                    new FrequencyResponse(grid, combined.Measurement),
                    new FrequencyResponse(grid, combined.Target),
                    positions, snr, calibrated, left, right, arrival, polarity));
                if (!perChannel || left == null || right == null) return new Fits(combined, null, null, advice);
                return new Fits(combined, Fit(left), Fit(right), advice);
            });
        }
        catch (Exception e)
        {
            if (cancel.IsCancellationRequested) return;
            CrashLog.Write(e);
            correctionMessage = e.Message;
            Scope.Refresh();
            return;
        }
        if (cancel.IsCancellationRequested) return;
        fits = fitted;
        Scope.Refresh();
    }
}
