using Capacitor.Cli.Core.Config;

namespace Capacitor.App.Services;

/// A desktop-only experimental switch, read from the store whenever a caller asks, so a change made in
/// Settings or by the CLI applies at the next ask. An unchanged file costs one stat.
public sealed class ExperimentalSwitch(ExperimentalSettingsStore store, string? profile, ExperimentalFeature feature) {
    readonly Lock _lock = new();
    (bool Exists, DateTime Written, long Length)? _fingerprint;
    bool _on;

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
