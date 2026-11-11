using System.Globalization;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.Copilot;

/// <summary>
/// Which watcher incarnation's end a Copilot sessionEnd hook took over, so that watcher sends none of its own when it is
/// stopped and the finalizer knows which live watcher it may kill. It names the pid file the hook saw: a watcher started
/// later, for a resumed run, is not covered and keeps its own end.
/// </summary>
public static class CopilotEndHandoff {
    public static string MarkerFile(WatcherPaths paths, string key) => Path.Combine(paths.Directory, $"{key}.end-handed");

    /// <summary>Records the session's current watcher as handed off. False when there was no watcher to record.</summary>
    public static bool Record(WatcherPaths paths, string key) {
        try {
            var watcher = File.ReadAllText(paths.PidFile(key)).Trim();

            if (watcher.Length == 0) return false;

            Directory.CreateDirectory(paths.Directory);
            File.WriteAllText(MarkerFile(paths, key), watcher);

            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    /// <summary>Gives the end back to the watcher, for a hook that could not make its own durable.</summary>
    public static void Withdraw(WatcherPaths paths, string key) {
        try {
            File.Delete(MarkerFile(paths, key));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Whether the watcher the pid file names now is the one whose end was handed off.</summary>
    public static bool CoversCurrentWatcher(WatcherPaths paths, string key) {
        try {
            return Read(MarkerFile(paths, key)) is { } marker && marker == File.ReadAllText(paths.PidFile(key)).Trim();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    /// <summary>Whether this process is the watcher whose end was handed off.</summary>
    public static bool Covers(WatcherPaths paths, string key, int pid, string? token) {
        if (Read(MarkerFile(paths, key)) is not { } marker) return false;

        var lines = marker.Split('\n', StringSplitOptions.TrimEntries);

        if (lines[0] != pid.ToString(CultureInfo.InvariantCulture)) return false;

        return lines.Length < 2 || lines[1].Length == 0 || token is null || lines[1] == token;
    }

    /// <summary>Drops the marker when it names <paramref name="pid"/>, so a recycled pid can never inherit it.</summary>
    public static void Retire(WatcherPaths paths, string key, int pid) {
        var file = MarkerFile(paths, key);

        try {
            if (Read(file) is { } marker && marker.Split('\n', StringSplitOptions.TrimEntries)[0] == pid.ToString(CultureInfo.InvariantCulture))
                File.Delete(file);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static string? Read(string file) {
        try {
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }
}
