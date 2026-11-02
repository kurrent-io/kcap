using Capacitor.Cli.Core.Config;

namespace Capacitor.App.Services;

/// The "In-app pages" experimental switch, read from the store whenever a caller asks, so a change
/// made in Settings or by the CLI applies to the next page opened. An unchanged file costs one stat.
public sealed class InAppPagesSwitch(ExperimentalSettingsStore store, string? profile) {
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
                _on = store.Load(profile).IsOn(ExperimentalFeatures.InAppPages);
                _fingerprint = fingerprint;
                return _on;
            }
        }
    }
}
