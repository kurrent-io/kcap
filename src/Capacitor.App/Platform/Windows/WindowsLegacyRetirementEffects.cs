using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;
using Microsoft.Win32;

namespace Capacitor.App.Platform.Windows;

/// Reads what a legacy retirement plans from, and makes its changes, on this machine and in this user's hive.
[SupportedOSPlatform("windows")]
public sealed class WindowsLegacyRetirementEffects(IProcessRunner runner, string ownCli, ConfigRoot config) : ILegacyRetirementEffects {
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string InstallLocation = "InstallLocation";
    const string RetiredMarker = ".retired-";
    const long LargestShortcut = 1024 * 1024;
    static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);
    static readonly TimeSpan RepointBudget = TimeSpan.FromMinutes(2);
    static readonly string[] StoppedImages = [VelopackRoot.UpdateExe, VelopackRoot.AppExecutable, VelopackRoot.LegacyAppExecutable];

    public static IReadOnlyList<UninstallEntry> UninstallEntries() {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        if (key is null) return [];
        var entries = new List<UninstallEntry>();
        foreach (var name in key.GetSubKeyNames()) {
            using var entry = key.OpenSubKey(name);
            entries.Add(new UninstallEntry(name, entry?.GetValue(InstallLocation) as string));
        }
        return entries;
    }

    public static LegacyRetirementFacts Scan(
            LegacyWindowsInstall legacy, string ownRoot, string ownCli, IReadOnlyList<UninstallEntry> entries, IReadOnlyList<string> keptPaths) =>
        new(legacy, ownRoot, ownCli, ProcessesIn(legacy.Root), entries, ShortcutsTo(legacy.Root), AutostartTo(legacy.Root),
            CliOnPath(legacy.Root), keptPaths);

    /// <summary>Deletes what an earlier removal moved aside but could not finish deleting.</summary>
    public static void SweepRetired(IEnumerable<string> parents) {
        foreach (var parent in parents.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)) {
            try {
                foreach (var aside in Directory.EnumerateDirectories(parent, LegacyWindowsInstall.FolderName + RetiredMarker + "*"))
                    if (new DirectoryInfo(aside).LinkTarget is null) DeleteTree(aside);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            }
        }
    }

    static List<RunningImage> ProcessesIn(string root) {
        var found = new List<RunningImage>();
        foreach (var image in StoppedImages) {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(image))) {
                using (process) {
                    try {
                        if (process.Id == Environment.ProcessId || process.HasExited) continue;
                        if (process.MainModule?.FileName is { } path && LegacyPaths.Within(path, root))
                            found.Add(new RunningImage(process.Id, path, process.StartTime));
                    } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) {
                    }
                }
            }
        }
        return found;
    }

    static List<string> ShortcutsTo(string root) {
#pragma warning disable RS0030 // shell folders, which the user can move off the home (OneDrive moves the Desktop)
        var desktop  = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
#pragma warning restore RS0030
        var found = new List<string>();
        void Search(string folder, int depth) {
            if (folder.Length == 0 || !Directory.Exists(folder)) return;
            var options = new EnumerationOptions { RecurseSubdirectories = depth > 0, MaxRecursionDepth = depth, IgnoreInaccessible = true };
            foreach (var lnk in Directory.EnumerateFiles(folder, "*.lnk", options)) {
                try {
                    if (new FileInfo(lnk).Length <= LargestShortcut && LegacyPaths.ShortcutMentions(File.ReadAllBytes(lnk), root)) found.Add(lnk);
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                }
            }
        }
        Search(desktop, 0);
        Search(programs, 1);
        return found;
    }

    static List<string> AutostartTo(string root) {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key is null) return [];
        return [.. key.GetValueNames().Where(name => LegacyPaths.Mentions(key.GetValue(name) as string, root))];
    }

    /// The legacy CLI whose folder is on the user PATH: in <c>current\bin</c> for the releases that ship one, else in <c>current</c>.
    static string? CliOnPath(string root) {
        var installer = new WindowsUserPathInstaller(new WindowsUserPathStore(), new WindowsPathProbe());
        return new[] { Path.Combine(VelopackRoot.Content(root), "bin", "kcap.exe"), Path.Combine(VelopackRoot.Content(root), "kcap.exe") }
            .FirstOrDefault(cli => installer.Preflight(cli) == ShimPreflight.AlreadyInstalled);
    }

    public bool EndProcess(RunningImage image) {
        Process process;
        try {
            process = Process.GetProcessById(image.Pid);
        } catch (ArgumentException) {
            return true;
        }
        using (process) {
            try {
                // The open handle keeps the pid from naming another process while it is checked and ended.
                _ = process.SafeHandle;
                if (process.HasExited || process.StartTime != image.StartTime
                        || !string.Equals(process.MainModule?.FileName, image.Path, StringComparison.OrdinalIgnoreCase))
                    return true;
                process.Kill();
                return process.WaitForExit(ExitWait);
            } catch (InvalidOperationException) {
                return true;
            }
        }
    }

    public async Task<string?> RepointDaemonsAsync(string legacyRoot, CancellationToken ct) {
        if (!File.Exists(ownCli)) return $"this install's kcap command is missing ({ownCli})";
        // Not killed on cancel: a repoint cut short between ending the task and running it again leaves no daemon.
        var result = await runner.RunAsync(ownCli, ["daemon", "service", "repoint", "--from", legacyRoot],
            new RunOptions(EnvOverlay: new Dictionary<string, string> { [ConfigRoot.ConfigDirEnvVar] = config.Directory },
                Timeout: RepointBudget), ct).ConfigureAwait(false);
        if (result.TimedOut) return "repointing the daemon took too long";
        if (result.ExitCode == 0) return null;
        return result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
            ?? $"kcap daemon service repoint exited {result.ExitCode}";
    }

    public void DeleteShortcut(string path) => File.Delete(path);

    public void DeleteAutostart(string valueName) {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public void RepointUninstallEntry(string legacyRoot, string ownRoot) {
        using var key = Registry.CurrentUser.OpenSubKey($@"{UninstallKey}\{UninstallEntry.KcapKey}", writable: true)
            ?? throw new IOException("the kcap entry is gone");
        if (!LegacyPaths.Same(key.GetValue(InstallLocation) as string, legacyRoot)) return;
        foreach (var name in key.GetValueNames()) {
            var kind = key.GetValueKind(name);
            if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) continue;
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value && LegacyPaths.Mentions(value, legacyRoot))
                key.SetValue(name, LegacyPaths.Rebase(value, legacyRoot, ownRoot), kind);
        }
    }

    /// Adds this install's folder before taking the legacy one off, so kcap never drops off the PATH between the two.
    public void MoveUserPath(string fromCli, string toCli) {
        var store     = new WindowsUserPathStore();
        var installer = new WindowsUserPathInstaller(store, new WindowsPathProbe());
        if (installer.Preflight(toCli) != ShimPreflight.AlreadyInstalled && Path.GetDirectoryName(toCli) is { } folder) store.Append(folder);
        installer.Remove(fromCli);
    }

    public void RemoveRoot(string root) {
        var aside = Path.TrimEndingDirectorySeparator(root) + RetiredMarker + Guid.NewGuid().ToString("N");
        Directory.Move(root, aside);
        try {
            DeleteTree(aside);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Out of the way under its retired name; the next launch's sweep finishes it.
        }
    }

    public void RemoveUpdater(string root) => File.Delete(Path.Combine(root, VelopackRoot.UpdateExe));

    public void DeleteUninstallEntry(string key, string root) {
        using (var entry = Registry.CurrentUser.OpenSubKey($@"{UninstallKey}\{key}")) {
            if (entry is null || !LegacyPaths.Same(entry.GetValue(InstallLocation) as string, root)) return;
        }
        Registry.CurrentUser.DeleteSubKeyTree($@"{UninstallKey}\{key}", throwOnMissingSubKey: false);
    }

    static void DeleteTree(string path) {
        foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })) {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(path, recursive: true);
    }
}
