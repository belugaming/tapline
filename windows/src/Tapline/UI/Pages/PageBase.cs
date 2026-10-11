using System.Windows.Controls;
using Tapline.Model;

namespace Tapline.UI.Pages;

/// A page of the main window. The window activates the visible page and deactivates it when it is
/// replaced or the window is hidden, so hidden pages cost nothing.
abstract class PageBase : UserControl
{
    private bool active;

    protected PageBase(AppModel model)
    {
        Model = model;
        Focusable = false;
    }

    protected AppModel Model { get; }
    protected UiScope Scope { get; } = new();

    /// Whether the page draws the output meters or the spectrum.
    public virtual bool ShowsMeters => false;
    /// Whether the page shows the per-app mixer.
    public virtual bool ShowsMixer => false;

    public void Activate()
    {
        if (active) return;
        active = true;
        Model.Changed += Changed;
        Model.Tick += OnTick;
        if (ShowsMeters) Model.MetersAppeared();
        if (ShowsMixer) Model.MixerAppeared();
        Changed();
        OnActivated();
    }

    public void Deactivate()
    {
        if (!active) return;
        active = false;
        Model.Changed -= Changed;
        Model.Tick -= OnTick;
        if (ShowsMeters) Model.MetersDisappeared();
        if (ShowsMixer) Model.MixerDisappeared();
        OnDeactivated();
    }

    private void Changed()
    {
        Scope.Refresh();
        OnModelChanged();
    }

    protected virtual void OnModelChanged() { }
    protected virtual void OnTick() { }
    protected virtual void OnActivated() { }
    protected virtual void OnDeactivated() { }
}
