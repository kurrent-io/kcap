using System.Runtime.InteropServices;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// The kcap that <c>PATH</c> runs and the desktop app's bundled one, each asked for its version. The running kcap is
/// left out, and a copy reached by both is counted once, as the app's: updating the app is what moves it.
/// </summary>
public sealed class LocalClientCopies(IProcessRunner runner, string? searchPath, string? self, IReadOnlyList<string> appClis) : ILocalClientCopies {
    static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);

    const int OutputLimit = 4096;

    IReadOnlyList<ClientCopy>? _found;

    public static LocalClientCopies ForCurrentProcess(UserHome home, TimeProvider time) =>
        new(new ProcessRunner(time), Environment.GetEnvironmentVariable("PATH"), Environment.ProcessPath, AppClis(home));

    /// <summary>Where a desktop app's bundled kcap can be.</summary>
    public static IReadOnlyList<string> AppClis(UserHome home) =>
        AppClis(home, OperatingSystem.IsMacOS() ? OSPlatform.OSX : OperatingSystem.IsWindows() ? OSPlatform.Windows : OSPlatform.Linux);

    internal static IReadOnlyList<string> AppClis(UserHome home, OSPlatform os) =>
        os == OSPlatform.OSX ? [AppCli("/Applications"), AppCli(Path.Combine(home.Path, "Applications"))]
        : os == OSPlatform.Windows ? WindowsAppClis(home)
        : [];

    public static string AppCli(string applications) => Path.Combine(applications, "kcap.app", "Contents", "MacOS", "kcap");

    /// The per-user Velopack install in <c>%LOCALAPPDATA%\kcap</c>: its kcap.exe is in <c>current\bin</c>, or in
    /// <c>current</c> itself for a release that shipped no <c>bin</c>.
    static string[] WindowsAppClis(UserHome home) {
        var current = Path.Combine(home.Path, "AppData", "Local", "kcap", "current");

        return [Path.Combine(current, "bin", "kcap.exe"), Path.Combine(current, "kcap.exe")];
    }

    public IReadOnlyList<ClientCopy> Find() => _found ??= Locate().Select(c => c with { Version = VersionOf(runner, c.Path) }).ToList();

    IEnumerable<ClientCopy> Locate() {
        // PATH, the process path and the profile can each spell one Windows path in a different case.
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (self is not null) seen.Add(CanonicalPath.Resolve(self));

        foreach (var app in appClis.Where(File.Exists)) {
            if (seen.Add(CanonicalPath.Resolve(app))) yield return new(ClientCopyKind.DesktopApp, app, null);
        }

        if (BinaryProbe.Searching(searchPath).Resolve("kcap") is { } onPath && seen.Add(CanonicalPath.Resolve(onPath)))
            yield return new(ClientCopyKind.OnPath, OperatingSystem.IsWindows() ? AsNamedOnDisk(onPath) : onPath, null);
    }

    /// The probe builds a Windows candidate from PATHEXT, which spells the extension in capitals; the user is shown the
    /// file as it is named on disk.
    static string AsNamedOnDisk(string path) {
        try {
            var dir = Path.GetDirectoryName(path);
            return dir is null ? path : Directory.EnumerateFiles(dir, Path.GetFileName(path)).FirstOrDefault() ?? path;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
            return path;
        }
    }

    /// <summary>The version the kcap at <paramref name="path"/> reports, or null when it reports none. Without
    /// <c>--no-update-check</c> the copy would run its npm update check on the way out.</summary>
    public static string? VersionOf(IProcessRunner runner, string path) {
        ProcessResult result;

        try {
            result = runner.RunBoundedAsync(path, ["--version", "--no-update-check"], new(Timeout: VersionTimeout), OutputLimit, CancellationToken.None)
                .GetAwaiter().GetResult();
        } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) {
            return null;
        }

        return result is { ExitCode: 0, TimedOut: false, Oversized: false } ? Parse(result.Stdout) : null;
    }

    /// <summary>Exactly one line, <c>kcap &lt;version&gt;</c>.</summary>
    static string? Parse(string stdout) {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines is not [var line] || !line.StartsWith("kcap ", StringComparison.Ordinal)) return null;

        var version = line["kcap ".Length..].Trim();

        return version is "" or "unknown" ? null : version;
    }
}
