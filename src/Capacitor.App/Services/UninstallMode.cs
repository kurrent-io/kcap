using Capacitor.App.Platform.Linux;
using Capacitor.App.Platform.Mac;
using Capacitor.App.Platform.Windows;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Services;

/// Options shared by the app menu and a standalone --uninstall launch. The standalone mode
/// builds none of the normal app; the menu supplies preparation to tear that app down first.
/// <param name="KeepConfig">Leave the settings and sign-in, and a local server's data with them.</param>
/// <param name="Confirm">Nobody has agreed to this launch yet, as when Windows starts it from Installed apps: it asks
/// before removing anything, and takes the other two options from the answer.</param>
internal sealed record UninstallMode(bool PurgeLocal, bool KeepConfig = false, bool Confirm = false) {
    public const string Arg = "--uninstall";
    public const string PurgeLocalArg = "--purge-local";
    public const string KeepConfigArg = "--keep-config";
    public const string ConfirmArg = "--confirm";

    public static UninstallMode? Parse(string[]? args) =>
        args?.Contains(Arg, StringComparer.Ordinal) == true
            ? new UninstallMode(
                args.Contains(PurgeLocalArg, StringComparer.Ordinal),
                args.Contains(KeepConfigArg, StringComparer.Ordinal),
                args.Contains(ConfirmArg, StringComparer.Ordinal))
            : null;

    /// The command line that carries an answered confirmation to a fresh copy of the app, which then asks nothing.
    public string[] Args() {
        List<string> args = [Arg];
        if (KeepConfig) args.Add(KeepConfigArg);
        if (PurgeLocal) args.Add(PurgeLocalArg);
        return [.. args];
    }

    /// Null unless the process runs from a real bundle: the removal trashes the bundle root, so a
    /// root that is merely computed from a path must never reach it.
    public UninstallRunViewModel? BuildRun(
            string? processPath, Func<string, IAppRemoval> removalFor, IKcapCli cli, Action quit, Action<Action>? post = null,
            Func<Task>? prepare = null, Func<bool>? hasSettings = null) {
        if (InstallLocation.BundleRoot(processPath) is not { } root
         || !File.Exists(Path.Combine(root, "Contents", "Info.plist"))) return null;
        return new UninstallRunViewModel(cli, removalFor(root), PurgeLocal, quit, post, prepare, KeepConfig, hasSettings);
    }

    /// Null unless the process runs from an AppImage that is still on disk: the removal trashes the image, so
    /// only the file the running app came from may reach it.
    public UninstallRunViewModel? BuildAppImageRun(
            AppImageInstall? install, Func<AppImageInstall, IAppRemoval> removalFor, IKcapCli cli, Action quit, Action<Action>? post = null,
            Func<Task>? prepare = null, Func<bool>? hasSettings = null) =>
        install is not null && File.Exists(install.Image)
            ? new UninstallRunViewModel(cli, removalFor(install), PurgeLocal, quit, post, prepare, KeepConfig, hasSettings)
            : null;

    /// Null unless the process runs from a per-user Velopack install whose updater is still there: the removal hands
    /// that install to its uninstaller, so a portable or development run must never reach it.
    public UninstallRunViewModel? BuildVelopackRun(
            VelopackInstall? install, Func<VelopackInstall, IAppRemoval> removalFor, IKcapCli cli, Action quit, Action<Action>? post = null,
            Func<Task>? prepare = null, Func<bool>? hasSettings = null) =>
        install is not null && File.Exists(install.UpdateExe)
            ? new UninstallRunViewModel(cli, removalFor(install), PurgeLocal, quit, post, prepare, KeepConfig, hasSettings)
            : null;
}
