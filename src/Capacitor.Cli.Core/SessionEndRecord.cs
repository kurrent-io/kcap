using System.Globalization;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core;

/// <summary>
/// That a daemon posted a session's end itself, because the session's process exited and no SessionEnd hook came in
/// the grace it gave one. A SessionEnd hook later than that reads it and posts no second end. Beside the claims, in the
/// directory hooks and the daemon share, and honoured only while fresh: a session resumed and ended again much later
/// owes its own end.
/// </summary>
public static class SessionEndRecord {
    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>How long after the daemon's post a hook's end is taken for the same one.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    public static void Write(WatcherPaths paths, string sessionId, DateTimeOffset postedAt) {
        try {
            Directory.CreateDirectory(paths.Directory);
            AtomicFile.Replace(PathOf(paths, sessionId), postedAt.ToString("O", CultureInfo.InvariantCulture), OwnerOnly, followLink: false, durable: false);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // A late hook then posts an end of its own, which is what it does today.
        }
    }

    /// <summary>Whether a daemon posted <paramref name="sessionId"/>'s end within <see cref="Window"/>. The record goes
    /// either way, so it answers once. One stat when there is none.</summary>
    public static bool TryTake(WatcherPaths paths, string sessionId, DateTimeOffset now) {
        var path = PathOf(paths, sessionId);

        try {
            if (!File.Exists(path)) return false;

            var fresh = DateTimeOffset.TryParse(File.ReadAllText(path), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                     && (now - at).Duration() <= Window;

            File.Delete(path);

            return fresh;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
            return false;
        }
    }

    static string PathOf(WatcherPaths paths, string sessionId) => Path.Combine(paths.Directory, $"{SessionIds.Canonical(sessionId) ?? sessionId}.session-end");
}
