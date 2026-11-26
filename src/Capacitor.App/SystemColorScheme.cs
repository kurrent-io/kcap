using Avalonia.Platform;

namespace Capacitor.App;

/// Whether the system prefers dark, for the Auto color scheme. A preference read at startup stands in
/// until the platform first reports a change: Linux's platform settings answer Light until their own
/// read of the same preference completes.
internal sealed class SystemColorScheme : IDisposable {
    readonly IPlatformSettings? _platform;
    bool? _startup;

    public SystemColorScheme(IPlatformSettings? platform, bool? startupPrefersDark) {
        _platform = platform;
        if (startupPrefersDark is not { } dark) return;
        _startup = dark;
        if (platform is not null) platform.ColorValuesChanged += Reported;
    }

    /// Dark when there is no platform to ask.
    public bool IsDark => _startup ?? _platform?.GetColorValues().ThemeVariant != PlatformThemeVariant.Light;

    void Reported(object? sender, PlatformColorValues values) {
        _startup = null;
        _platform!.ColorValuesChanged -= Reported;
    }

    public void Dispose() {
        if (_platform is not null) _platform.ColorValuesChanged -= Reported;
    }
}
