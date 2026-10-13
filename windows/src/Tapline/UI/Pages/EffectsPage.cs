using System.Windows;
using Tapline.Core;
using Tapline.Infrastructure;
using Tapline.Model;

namespace Tapline.UI.Pages;

sealed class EffectsPage : PageBase
{
    public EffectsPage(AppModel model) : base(model)
    {
        EffectsSettings Fx() => Model.Settings.Effects;
        void Set(Func<EffectsSettings, EffectsSettings> change, bool dragging = false) =>
            Model.Mutate(s => s.Effects = change(s.Effects), undoable: !dragging);

        var intro = Scope.Text(() => L.F("Effects apply to everything playing through %@ and are saved per device.", Model.CurrentOutput?.Name ?? L.T("this device")), 13, null, Theme.Secondary, wrap: true);

        var output = Ui.Card(L.T("Output Gain"), L.T("Overall level after the EQ and effects."), Glyphs.Volume,
            Scope.LabeledSlider(L.T("Gain"), -24, 12, () => Fx().OutputGainDb, (v, d) => Set(f => f with { OutputGainDb = Format.Round(v, 1) }, d), Format.Gain, 0.5, Model.Checkpoint),
            Ui.Caption(L.T("Equalizer APO has no limiter. Keep the preamp on Auto so boosts don't clip, and lower this if the meters reach the top.")));

        var balance = Ui.Card(L.T("Balance"), L.T("Shift the stereo image left or right."), Glyphs.Swap,
            Scope.LabeledSlider(L.T("Balance"), -1, 1, () => Fx().Balance, (v, d) => Set(f => f with { Balance = Math.Abs(v) < 0.02 ? 0 : v }, d), v =>
                Math.Abs(v) < 0.01 ? L.T("Center") : (v < 0 ? Printf.Format("L %.0f", [-v * 100]) : Printf.Format("R %.0f", [v * 100])), 0, Model.Checkpoint));

        var widthSlider = Scope.LabeledSlider(L.T("Width"), 0, 2, () => Fx().Width, (v, d) => Set(f => f with { Width = Math.Abs(v - 1) < 0.03 ? 1 : v }, d), Format.Percent, 0, Model.Checkpoint);
        Scope.Bind(widthSlider, w => w.IsEnabled = !Fx().Mono);
        var width = Ui.Card(L.T("Stereo Width"), L.T("Narrow or widen the stereo image. 0% is mono."), Glyphs.Speakers,
            widthSlider,
            Scope.Check(L.T("Mono"), () => Fx().Mono, v => Set(f => f with { Mono = v })),
            Scope.Check(L.T("Swap left and right"), () => Fx().SwapChannels, v => Set(f => f with { SwapChannels = v })));

        var amount = Scope.LabeledSlider(L.T("Amount"), 0, 1, () => Fx().Crossfeed, (v, d) => Set(f => f with { Crossfeed = v }, d), Format.Percent, 0, Model.Checkpoint);
        Scope.Bind(amount, a => a.IsEnabled = Fx().CrossfeedEnabled);
        var crossfeed = Ui.Card(L.T("Headphone Crossfeed"), L.T("Blends a little of each channel into the other, like listening to speakers. Reduces fatigue on hard-panned recordings."), Glyphs.Headphones,
            HeadphonesOnly(Scope.LabeledSwitch(L.T("Enabled"), () => Fx().CrossfeedEnabled, Model.SetCrossfeed)),
            amount);

        var speakers = VirtualSpeakersCard();

        var grid = new AdaptiveGrid { MinItemWidth = 380, Spacing = 16 };
        grid.Children.Add(output);
        grid.Children.Add(balance);
        grid.Children.Add(width);
        grid.Children.Add(crossfeed);
        grid.Children.Add(speakers);

        var reset = Ui.Button(L.T("Reset Effects"), () => Model.Mutate(s => s.Effects = new EffectsSettings())).Right();
        var speakerSetup = Scope.Dynamic(() => (Model.IsSurroundOutput ? Model.CurrentLayout : null, Model.CurrentOutput?.Name), inner =>
            Model.IsSurroundOutput && Model.CurrentLayout is { } layout ? SpeakersCard(inner, layout) : null);
        Scope.Bind(speakerSetup, c => c.Visibility = Model.IsSurroundOutput ? Visibility.Visible : Visibility.Collapsed);
        Content = Ui.Page(Ui.VStack(16, intro, speakerSetup, grid, reset));
    }

    /// A headphone effect's switch: off on a multichannel speaker output, where the effect never plays.
    private T HeadphonesOnly<T>(T element) where T : UIElement => Scope.Bind(element, e => e.IsEnabled = !Model.IsSurroundOutput);

    /// A multichannel output: which effects reach which speakers, and the speaker corrections.
    private FrameworkElement SpeakersCard(UiScope scope, SpeakerLayout layout)
    {
        bool Corrected() => Model.Settings.Speakers.Layout?.Equals(layout) == true && Model.Settings.Speakers.Corrections.Count > 0;
        var correction = scope.LabeledSwitch(L.T("Speaker correction"), () => Corrected() && Model.Settings.Speakers.CorrectionEnabled,
            Model.SetSpeakerCorrectionEnabled);
        scope.Bind(correction, c => c.IsEnabled = Corrected());
        return Ui.Card(L.T("Speakers"),
            L.F("%@ is a %@ speaker layout. Tapline processes each of its %lld speakers.", Model.CurrentOutput?.Name ?? "", layout.Name, layout.SpeakerChannels.Count),
            Glyphs.Speakers,
            scope.LabeledSwitch(L.T("Play the EQ on every speaker"), () => Model.Settings.Speakers.EqOnAllSpeakers, Model.SetEqOnAllSpeakers),
            Ui.Caption(L.T("Off: the EQ plays on the front left and right only. On the subwoofer (LFE) channel it never boosts above 120 Hz.")),
            correction,
            scope.Text(() => Corrected()
                ? L.T("Each speaker's room correction, trim and delay from the Room page.")
                : L.T("Measure the speakers on the Room page to correct each one."), 11.5, null, Theme.Secondary, wrap: true),
            Ui.HStack(8, Ui.Glyph(Glyphs.Info, 12, Theme.Secondary).Width(16).Top().Margin(0, 1, 0, 0),
                Ui.Caption(L.T("Balance, width, mono and swap act on the front left and right speakers. Crossfeed, Virtual Speakers and Listen As are for headphones and stay off on this output.")).Filling()));
    }

    private string? importMessage;

    private FrameworkElement VirtualSpeakersCard()
    {
        var picker = Scope.Combo(() => Model.Speakers.All, s => s.Name, () => Model.CurrentVirtualSpeakers, s =>
        {
            importMessage = null;
            Model.ChooseVirtualSpeakers(s.Id);
        }, 240);
        var import = Ui.Button(L.T("Import…"), Import, Glyphs.Import,
            tip: L.T("A HeSuVi or Impulcifer WAV file, a 4-channel file, or two stereo files (one per speaker)"));
        var remove = Scope.Dynamic(() => Model.CurrentVirtualSpeakers, _ =>
        {
            var current = Model.CurrentVirtualSpeakers;
            return current.IsBuiltIn ? null : Ui.Button(L.F("Remove “%@”", current.Name), () => Model.RemoveVirtualSpeakers(current.Id));
        });
        var problem = Scope.Bind(Ui.Caption(""), t =>
        {
            var text = importMessage ?? (Model.Settings.Effects.VirtualSpeakersEnabled ? Model.VirtualSpeakersProblem : null);
            t.Text = text ?? "";
            t.Foreground = Theme.Warning;
            t.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        });
        return Ui.Card(L.T("Virtual Speakers"), L.T("Puts the sound in front of you, like a pair of speakers at ±30°, with binaural impulse responses. For headphones; replaces crossfeed."), Glyphs.Speakers,
            HeadphonesOnly(Scope.LabeledSwitch(L.T("Enabled"), () => Model.Settings.Effects.VirtualSpeakersEnabled, Model.SetVirtualSpeakers)),
            Ui.HStack(10, Ui.Text(L.T("Speakers")), picker, Ui.Spacer(), import),
            Scope.Text(() => Detail(Model.CurrentVirtualSpeakers), 11.5, null, Theme.Secondary, wrap: true),
            Ui.HStack(10, remove, Ui.Spacer(), Ui.Link(L.T("Where to find HRIR files"), "https://github.com/belugaming/tapline#virtual-speakers")),
            problem);
    }

    private static string Detail(VirtualSpeakerSet set)
    {
        if (set.Id == EffectsSettings.DefaultVirtualSpeakers)
            return L.T("Neumann KU100 dummy head from the SADIE II database, with the reflections of a treated studio room. Bass and lower mids stay dry.");
        if (set.Id == "sadie-ku100")
            return L.T("Neumann KU100 dummy head from the SADIE II database, anechoic: the speakers without a room. The cleanest sound, but it may seem closer to your head.");
        var layout = L.T(set.Layout switch
        {
            BinauralLayout.Hesuvi14 => "HeSuVi, 14 channels",
            BinauralLayout.Hesuvi7 => "HeSuVi, 7 channels",
            BinauralLayout.Impulcifer16 => "Impulcifer hrir.wav",
            BinauralLayout.FourChannel => "4 channels",
            BinauralLayout.StereoPair => "Two stereo files",
            _ => "One speaker, mirrored",
        });
        return $"{layout} · {Printf.Format("%g kHz", [set.SampleRate / 1000])} · {Printf.Format("%.0f ms", [set.Duration * 1000])}";
    }

    private void Import()
    {
        var paths = Dialogs.OpenFiles(L.T("WAV files") + " (*.wav)|*.wav");
        if (paths.Count == 0) return;
        try
        {
            Model.ImportVirtualSpeakers(paths);
            importMessage = null;
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or WavFormatException or BinauralImportException)
        {
            importMessage = VirtualSpeakerLibrary.Message(e);
        }
        Scope.Refresh();
    }
}
