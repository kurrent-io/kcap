using Capacitor.App.Services;
using Velopack.Locators;

namespace Capacitor.App.Platform.Windows;

/// The per-user Velopack install the running app came from: the root its <c>Update.exe</c> deletes on uninstall, and
/// the CLI whose folder the app put on the user PATH.
public sealed record VelopackInstall(string Root, string UpdateExe, string Cli) {
    /// Null for a portable or development run, which no uninstaller owns.
    public static VelopackInstall? Current(string? processPath, string cliDirectory) {
        if (!VelopackLocator.IsCurrentSet) return null;
        var locator = VelopackLocator.Current;
        if (locator.IsPortable || locator.CurrentlyInstalledVersion is null) return null;
        return Detect(locator.RootAppDir, processPath, cliDirectory);
    }

    /// Null unless <paramref name="root"/> holds the app and its updater, and the process runs from that app's folder.
    public static VelopackInstall? Detect(string? root, string? processPath, string cliDirectory) {
        if (root is not { Length: > 0 } || processPath is not { Length: > 0 } || VelopackRoot.AppIn(root) is null) return null;
        var updateExe = Path.Combine(root, VelopackRoot.UpdateExe);
        if (!File.Exists(updateExe) || !LegacyPaths.Within(processPath, VelopackRoot.Content(root))) return null;
        return new VelopackInstall(LegacyPaths.Normalize(root), updateExe, Path.Combine(cliDirectory, CliResolver.BundledCliFileName));
    }
}
