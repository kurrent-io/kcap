using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Capacitor.Cli.Core.Config;

namespace Capacitor.App.Services;

/// A desktop-only experimental switch, read from the store whenever a caller asks, so a change made in
/// Settings or by the CLI applies at the next ask. An unchanged file costs one stat.
public sealed class ExperimentalSwitch(ExperimentalSettingsStore store, string? profile, ExperimentalFeature feature) {
    readonly Lock _lock = new();
    (bool Exists, DateTime Written, long Length)? _fingerprint;
    bool _on;

    /// Signals each time <paramref name="read"/> returns other than it last did, reading once on
    /// subscribe and again on every <paramref name="mayChange"/>. Reads run on <paramref name="worker"/>:
    /// a changed file is read and parsed, which must not hold up a window gaining focus.
    public static IObservable<Unit> Flips(Func<bool> read, IObservable<Unit> mayChange, IScheduler worker) =>
        Observable.Defer(() => Observable.Return(Unit.Default).Concat(mayChange))
            .ObserveOn(worker)
            .Select(_ => read())
            .DistinctUntilChanged()
            .Skip(1)
            .Select(_ => Unit.Default);

    public bool IsOn {
        get {
            if (string.IsNullOrEmpty(profile)) return false;
            var file = new FileInfo(store.Path);
            var fingerprint = file.Exists ? (true, file.LastWriteTimeUtc, file.Length) : (false, default(DateTime), 0L);
            lock (_lock) {
                if (fingerprint == _fingerprint) return _on;
                _on = store.Load(profile).IsOn(feature);
                _fingerprint = fingerprint;
                return _on;
            }
        }
    }
}
