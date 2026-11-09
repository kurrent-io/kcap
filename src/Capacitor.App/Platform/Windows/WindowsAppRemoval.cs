using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Platform.Windows;

/// <summary>
/// Removes a per-user Velopack install once the CLI has removed everything else: the CLI's folder comes off the user
/// PATH, then the install goes to its own <c>Update.exe --uninstall</c>, the command Settings › Apps runs. Update.exe
/// ends every process running from the install before it deletes it, so it is started only as this app exits, never
/// while the app still shows the result. It runs silent: the app's own window already said the app is removed on close,
/// and a removal that fails leaves kcap listed in Settings › Apps.
/// </summary>
public sealed class WindowsAppRemoval : IAppRemoval {
    public const string SilentArg    = "--silent";
    public const string UninstallArg = "--uninstall";
    public const string AppsSettings = "ms-settings:appsfeatures";

    readonly VelopackInstall          _install;
    readonly WindowsUserPathInstaller _path;
    readonly Action<ProcessStartInfo> _start;
    bool                              _armed;

    /// <param name="start">Starts a process and lets it outlive this one.</param>
    public WindowsAppRemoval(VelopackInstall install, WindowsUserPathInstaller path, Action<ProcessStartInfo>? start = null) {
        _install = install;
        _path    = path;
        _start   = start ?? (info => Process.Start(info)?.Dispose());
    }

    public string FinishByHand => "Remove kcap in Settings › Apps";

    public string Trashed => "The kcap app is removed when you close this window";

    public string RemovedSummary => "The app is removed when you close this window.";

    public string RemovedLine => "The app is removed when you close this window.";

    public string NotRemoved => "The app could not be handed to its Windows uninstaller";

    public bool RemovedOnExit => true;

    string CliFolder => Path.GetDirectoryName(_install.Cli) ?? _install.Cli;

    /// The CLI's folder while it is still on the user PATH; Windows has no command links to remove.
    public Task<IReadOnlyList<string>> RemoveLinksAsync(CancellationToken ct) {
        try {
            _path.Remove(_install.Cli);
            if (_path.Preflight(_install.Cli) != ShimPreflight.AlreadyInstalled) return Task.FromResult<IReadOnlyList<string>>([]);
        } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException) {
            Console.Error.WriteLine($"kcap: could not take {CliFolder} off the user PATH: {ex.Message}");
        }
        return Task.FromResult<IReadOnlyList<string>>([$"{CliFolder} from your user PATH"]);
    }

    /// Everything else the app keeps on Windows sits under the install root, which the uninstaller deletes.
    public bool DeleteSupportFiles() => true;

    /// True once the uninstaller is set to start on exit. Nothing is removed yet.
    public Task<bool> TrashBundleAsync(CancellationToken ct) {
        _armed = File.Exists(_install.UpdateExe);
        if (!_armed) Console.Error.WriteLine($"kcap: {_install.UpdateExe} is missing, so the app was left in place");
        return Task.FromResult(_armed);
    }

    /// <summary>Starts the uninstaller if a finished run set it up; called as the app exits. Its working folder is
    /// outside the install, because a process whose current folder is inside it keeps that folder from being deleted.
    /// The variable tells the uninstall hook that <c>kcap uninstall</c> already ran.</summary>
    public void StartUninstallerIfSet() {
        if (!_armed) return;
        _armed = false;
        var info = new ProcessStartInfo(_install.UpdateExe) {
            UseShellExecute  = false,
            WorkingDirectory = Path.GetDirectoryName(_install.Root) ?? _install.Root,
        };
        info.ArgumentList.Add(SilentArg);
        info.ArgumentList.Add(UninstallArg);
        info.Environment[WindowsUninstallCleanup.CompletedEnvVar] = "1";
        try {
            _start(info);
        } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) {
            Console.Error.WriteLine($"kcap: could not start {_install.UpdateExe}: {ex.Message}. Remove kcap in Settings › Apps.");
        }
    }

    public void RevealBundle() {
        try {
            _start(new ProcessStartInfo(AppsSettings) { UseShellExecute = true });
        } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) {
            Console.Error.WriteLine($"kcap: could not open Settings › Apps: {ex.Message}");
        }
    }

    /// The CLI's folder when it is on the user PATH, for the confirmation to name; null elsewhere or when unreadable.
    public static string? FolderOnUserPath(string cli) {
        if (!OperatingSystem.IsWindows()) return null;
        var installer = new WindowsUserPathInstaller(new WindowsUserPathStore(), new WindowsPathProbe());
        return installer.Preflight(cli) == ShimPreflight.AlreadyInstalled ? Path.GetDirectoryName(cli) : null;
    }
}
