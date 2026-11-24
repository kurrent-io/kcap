using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Config;
using ReactiveUI.Reactive;

namespace Capacitor.App;

public partial class App {
    readonly CompositeDisposable _desktopExperimentSubscriptions = new();

    void WatchDesktopExperiment(ExperimentalFeature feature, string? profile, string resource, Action<bool>? onChanged = null) {
        if (_desktopAppearance is not { } appearance || (!feature.AppWide && string.IsNullOrEmpty(profile))) return;
        var flag = new ExperimentalSwitch(new ExperimentalSettingsStore(_config), profile, feature);
        var changes = _experimentalSaved.Merge(
            Window.IsActiveProperty.Changed.Where(change => change.NewValue.GetValueOrDefault()).Select(_ => Unit.Default));
        _desktopExperimentSubscriptions.Add(changes.StartWith(Unit.Default)
            .ObserveOn(TaskPoolScheduler.Default).Select(_ => flag.IsOn).DistinctUntilChanged()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .CombineLatest(appearance.WhenAnyValue(settings => settings.Enabled), (enabled, preview) => enabled && preview)
            .DistinctUntilChanged().Subscribe(enabled => {
                onChanged?.Invoke(enabled);
                if (enabled) Resources[resource] = true;
                else Resources.Remove(resource);
            }));
    }
}
