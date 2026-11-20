using System.Reactive;
using Capacitor.App.ProgressiveDisclosure;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The header's progressive disclosure toggle. One instance, because the basis is process-wide.
/// On fades detail and footnote; off shows both normally.
public sealed class ProgressiveDisclosureViewModel : ReactiveObject {
    public static ProgressiveDisclosureViewModel Shared { get; } = new();

    static readonly ProgressiveDisclosureBasis On = new(ProgressiveDisclosureDisplay.Faded, ProgressiveDisclosureDisplay.Faded);
    static readonly ProgressiveDisclosureBasis Off = new(ProgressiveDisclosureDisplay.Normal, ProgressiveDisclosureDisplay.Normal);

    IAppStateStore? _store;

    ProgressiveDisclosureViewModel() => ToggleCommand = ReactiveCommand.Create(() => { Enabled = !Enabled; });

    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    public bool Enabled {
        get => ProgressiveDisclosureBasis.Current != Off;
        set {
            if (value == Enabled) return;
            ProgressiveDisclosureBasis.Set(value ? On : Off);
            this.RaisePropertyChanged();
            Save();
        }
    }

    /// Takes the saved setting, then saves every later change to the store.
    public void Restore(ProgressiveDisclosurePreferences? saved, IAppStateStore? store) {
        _store = null;
        Enabled = saved?.Enabled ?? true;
        _store = store;
    }

    /// Each write stores what is current when it runs, so overlapping changes cannot leave an older one on disk.
    void Save() {
        if (_store is not { } store) return;
        _ = store.UpdateAsync(state => state with { ProgressiveDisclosure = new(Enabled) });
    }
}
