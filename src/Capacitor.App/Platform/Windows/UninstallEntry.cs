namespace Capacitor.App.Platform.Windows;

/// One per-user entry in Installed apps: its key under <c>HKCU\...\Uninstall</c> and the install location it names.
public sealed record UninstallEntry(string Key, string? InstallLocation) {
    /// The key Velopack writes for the kcap package, whichever folder installed it last.
    public const string KcapKey = "kcap";
}
