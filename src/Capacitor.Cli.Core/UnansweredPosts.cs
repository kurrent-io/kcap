namespace Capacitor.Cli.Core;

/// <summary>
/// Posts a drain pass sent that have no answer yet, one empty file per attempt under the spool's directory, written
/// before the send. The server may still be taking such a post in, so a deletion of its session can be overtaken by it,
/// and a free <see cref="SpoolDrainLease"/> says only that the pass has ended. An attempt's own answer, whatever its
/// status, removes that attempt's record and nothing else. A process that dies mid-post leaves its record, and the
/// spool's reap ages it out. Best effort throughout: a record that can't be written or removed never stops a drain.
/// </summary>
public static class UnansweredPosts {
    // No spool key holds a dot, so no session's own files or directory can be this, and a key ends each record's prefix.
    public const string DirectoryName = ".unanswered";

    /// <summary>One post of a drain pass for <paramref name="sessionId"/>. A poster returns an outcome only once the
    /// server has answered or nothing was sent, and lets an exception through for a post that may have reached it.</summary>
    public static async Task<DrainOutcome> TrackAsync(string spoolDir, string sessionId, Func<Task<DrainOutcome>> post) {
        var attempt = Begin(spoolDir, sessionId);

        try {
            var outcome = await post();
            End(attempt);

            return outcome;
        } catch (Exception ex) {
            if (!Sent(ex)) End(attempt);

            return DrainOutcome.TransientStop;
        }
    }

    /// <summary>Records an attempt ahead of its send.</summary>
    /// <returns>The attempt, for <see cref="End"/>; null when it could not be recorded.</returns>
    public static string? Begin(string spoolDir, string sessionId) {
        if (KeyOf(sessionId) is not { } key) return null;

        var path = Path.Combine(spoolDir, DirectoryName, $"{key}.{Guid.NewGuid().ToString("N")}");

        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose();

            return path;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>The attempt had its answer, or never left: the server has nothing of it still to take in.</summary>
    public static void End(string? attempt) {
        if (attempt is null) return;

        try {
            File.Delete(attempt);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Left behind, it makes the next disable of the session doubt its stop, and the reap removes it.
        }
    }

    /// <summary>Whether a post that failed with <paramref name="ex"/> may have reached the server: everything but a
    /// connection that was never made.</summary>
    public static bool Sent(Exception ex) =>
        ex is not HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError };

    /// <summary>Whether any attempt for the session has no answer. One that can't be read may.</summary>
    public static bool Has(string spoolDir, string sessionId) {
        if (KeyOf(sessionId) is not { } key) return false;

        var dir = Path.Combine(spoolDir, DirectoryName);

        try {
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir, $"{key}.*").Any();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return true;
        }
    }

    /// <summary>Removes every attempt recorded for the session.</summary>
    /// <returns>Whether none is left.</returns>
    public static bool Clear(string spoolDir, string sessionId) {
        if (KeyOf(sessionId) is not { } key) return true;

        var dir = Path.Combine(spoolDir, DirectoryName);

        try {
            if (!Directory.Exists(dir)) return true;

            foreach (var attempt in Directory.EnumerateFiles(dir, $"{key}.*").ToList()) File.Delete(attempt);

            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    // The canonical spelling, which kcap disable has, whatever the vendor's spool holds.
    static string? KeyOf(string sessionId) => HookSpool.KeyFor(SessionIds.Canonical(sessionId) ?? sessionId);
}
