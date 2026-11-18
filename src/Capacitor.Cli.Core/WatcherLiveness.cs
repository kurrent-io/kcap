using System.Diagnostics;

namespace Capacitor.Cli.Core;

/// <summary>
/// Whether a watcher holds a key, and the per-key spawn lock that makes acting on the answer safe: whichever process
/// starts a recorder for a key holds the lock with the live-watcher check inside it.
/// </summary>
public sealed class WatcherLiveness(WatcherPaths paths, TimeProvider time) {
    public bool PidAlive(string key) => PidAlive(paths, key);

    /// <summary>The pid file names a process that is still this watcher's incarnation, irrespective of
    /// whether it's wedged. A pid file that exists but cannot be read counts as alive: reading it as
    /// gone would let a spawn overwrite it and orphan the watcher it names.</summary>
    public static bool PidAlive(WatcherPaths paths, string key) {
        string[] lines;

        try {
            lines = File.ReadAllLines(paths.PidFile(key));
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            return false;
        } catch {
            return true;
        }

        if (lines.Length == 0 || !int.TryParse(lines[0].Trim(), out var pid)) {
            return false;
        }

        return !IsGone(pid, lines.Length > 1 ? lines[1].Trim() : "");
    }

    /// <summary>Conclusive only: the pid left the process table, or its start token names another
    /// incarnation. A process whose identity cannot be compared counts as the watcher.</summary>
    public static bool IsGone(int pid, string token) {
        if (token.Length > 0 && ProcessStartToken.Matches(pid, token) == false) return true;

        try {
            using var process = Process.GetProcessById(pid);

            return process.HasExited;
        } catch (ArgumentException) {
            return true;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// True when the watcher's PID exists AND its heartbeat isn't stale (after the startup
    /// grace) — i.e. the process is alive AND its main loop is provably still turning, not
    /// wedged. <see cref="PidAlive(WatcherPaths, string)"/> alone can't tell a hung watcher from a healthy one.
    /// </summary>
    public bool IsWatcherAlive(string key) {
        if (!PidAlive(key)) {
            return false;
        }

        var now       = time.GetUtcNow();
        var lastBeat  = WatcherHeartbeat.Read(paths.HeartbeatFile(key));
        // A missing started marker (shouldn't happen in practice — the spawner always
        // writes it) falls back to "now", i.e. the freshest possible grace window rather
        // than treating an unknown start time as long-past and immediately stale.
        var startupAt = WatcherHeartbeat.Read(paths.StartedFile(key)) ?? now;

        return !WatcherHeartbeat.IsStale(lastBeat, startupAt, now, WatcherHeartbeat.Grace, WatcherHeartbeat.Threshold);
    }

    /// <summary>
    /// Runs <paramref name="body"/> while holding the per-key spawn lock. If another process
    /// already holds it, returns immediately WITHOUT running <paramref name="body"/> — the current
    /// holder is either already reaping + respawning this key, or about to, so there is nothing for
    /// the loser to do but skip. Two concurrent hooks would otherwise double-spawn one key.
    /// </summary>
    public async Task WithSpawnLock(string key, Func<Task> body) {
        var watcherDir = paths.Directory;
        Directory.CreateDirectory(watcherDir);

        FileStream stream;

        try {
            // FileShare.None maps to flock(LOCK_EX) on POSIX and a real exclusive lock on
            // Windows — the same cross-platform primitive DaemonLock uses. FileMode.OpenOrCreate
            // keeps a stale lock file on disk from ever blocking acquisition; the kernel lock,
            // not file presence, is what enforces exclusion.
            stream = new FileStream(paths.SpawnLockFile(key), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        } catch (IOException) {
            return; // contended — the current holder wins; we skip rather than wait.
        }

        try {
            await body();
        } finally {
            stream.Dispose();
        }
    }

    /// <summary>Unlinks a spawn lock nothing holds, taking it first. Unlinking a held one would hand the next taker a
    /// fresh inode with a lock that conflicts with nothing, and both would go on to start a recorder.</summary>
    /// <returns>False when something holds it, so it stays.</returns>
    public static bool TryDeleteSpawnLock(string lockFile) {
        try {
            new FileStream(lockFile, FileMode.Open, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose).Dispose();

            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }
}
