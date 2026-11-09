using System.Diagnostics;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// Whether the daemon's pid file names a live process. A token that cannot be compared counts as
/// live: a wrong answer only makes the uninstall warn needlessly, or holds the footer's Start back
/// for the bounded grace a starting daemon gets.
public static class DaemonPidLiveness {
    /// Every daemon in the store whose pid file names a live process.
    public static IReadOnlyList<string> LiveDaemons(DaemonStore store) =>
        [.. store.EnumerateNames().Where(name => IsRunning(store, name))];

    public static bool IsRunning(DaemonStore store, string daemonName) => LivePid(store, daemonName) is not null;

    /// The pid the daemon's pid file names, while that process is alive; null otherwise.
    public static int? LivePid(DaemonStore store, string daemonName) {
        string[] lines;
        try {
            using var stream = new FileStream(store.PidPath(daemonName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }

        if (lines.Length == 0 || !int.TryParse(lines[0], out var pid) || pid <= 0) return null;
        if (lines.Length > 1 && ProcessStartToken.Matches(pid, lines[1]) is { } matched) return matched ? pid : null;

        try {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : pid;
        } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) {
            return null;
        }
    }
}
