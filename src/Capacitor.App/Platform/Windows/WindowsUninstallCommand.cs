using System.Runtime.Versioning;
using Capacitor.App.Services;
using Microsoft.Win32;

namespace Capacitor.App.Platform.Windows;

/// The command Installed apps runs to remove kcap. The installer's own goes straight to its uninstaller, whose hook
/// has thirty seconds and no window: it can neither say what will go nor ask whether the settings should. This one
/// opens the app's uninstall instead. The quiet command stays the installer's, for a caller that must not be asked.
public static class WindowsUninstallCommand {
    const string UninstallString = "UninstallString";
    const string InstallLocation = "InstallLocation";

    public static string For(string appExe) => $"\"{appExe}\" {UninstallMode.Arg} {UninstallMode.ConfirmArg}";

    /// Null when the entry is another install's, or already runs <paramref name="appExe"/>.
    public static string? Replacement(string? installLocation, string? current, string root, string appExe) {
        if (!LegacyPaths.Same(installLocation, root)) return null;
        var command = For(appExe);
        return string.Equals(current, command, StringComparison.Ordinal) ? null : command;
    }

    /// The installer writes its own command, so this is checked at every start rather than once.
    [SupportedOSPlatform("windows")]
    public static void PointAtApp(string root, string appExe) {
        using var key = Registry.CurrentUser.OpenSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallEntry.KcapKey}", writable: true);
        if (key is null) return;
        if (Replacement(key.GetValue(InstallLocation) as string, key.GetValue(UninstallString) as string, root, appExe) is { } command)
            key.SetValue(UninstallString, command, RegistryValueKind.String);
    }
}
