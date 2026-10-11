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
            Scope.LabeledSwitch(L.T("Enabled"), () => Fx().CrossfeedEnabled, v => Set(f => f with { CrossfeedEnabled = v })),
            amount);

        var grid = new AdaptiveGrid { MinItemWidth = 380, Spacing = 16 };
        grid.Children.Add(output);
        grid.Children.Add(balance);
        grid.Children.Add(width);
        grid.Children.Add(crossfeed);

        var reset = Ui.Button(L.T("Reset Effects"), () => Model.Mutate(s => s.Effects = new EffectsSettings())).Right();
        Content = Ui.Page(Ui.VStack(16, intro, grid, reset));
    }
}
