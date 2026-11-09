namespace Capacitor.Cli.Core;

/// <summary>
/// Sessions a drain pass sent a post for that never got an answer, one empty file each under the spool's directory. The
/// server may still be taking such a post in, so a deletion of the session can be overtaken by it, and a free
/// <see cref="SpoolDrainLease"/> says only that the pass has ended. A delivered post of the session clears it. Best
/// effort throughout: a record that can't be written or cleared never stops a drain, and the spool's reap ages it out.
/// </summary>
public static class UnansweredPosts {
    // No spool key holds a dot, so no session's own files or directory can be this.
    public const string DirectoryName = ".unanswered";

    /// <summary>One post of a drain pass for <paramref name="sessionId"/>. A poster lets an exception through for a post
    /// that may have reached the server; this records it and reads it as <see cref="DrainOutcome.TransientStop"/>.</summary>
    public static async Task<DrainOutcome> TrackAsync(string spoolDir, string sessionId, Func<Task<DrainOutcome>> post) {
        DrainOutcome outcome;

        try {
            outcome = await post();
        } catch (Exception ex) {
            if (Sent(ex)) Record(spoolDir, sessionId);

            return DrainOutcome.TransientStop;
        }

        if (outcome == DrainOutcome.Delivered) Clear(spoolDir, sessionId);

        return outcome;
    }

    /// <summary>Whether a post that failed with <paramref name="ex"/> may have reached the server: everything but a
    /// connection that was never made.</summary>
    public static bool Sent(Exception ex) =>
        ex is not HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError };

    public static bool Has(string spoolDir, string sessionId) => PathFor(spoolDir, sessionId) is { } path && File.Exists(path);

    public static void Record(string spoolDir, string sessionId) {
        if (PathFor(spoolDir, sessionId) is not { } path) return;

        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Truncating rewrites the time, which the reap counts from.
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Unrecorded, the post is as a lease alone left it.
        }
    }

    public static void Clear(string spoolDir, string sessionId) {
        if (PathFor(spoolDir, sessionId) is not { } path) return;

        try {
            File.Delete(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Left behind, it makes the next disable of the session doubt its stop, and the reap removes it.
        }
    }

    // The canonical spelling, which kcap disable has, whatever the vendor's spool holds.
    static string? PathFor(string spoolDir, string sessionId) =>
        HookSpool.KeyFor(SessionIds.Canonical(sessionId) ?? sessionId) is { } key ? Path.Combine(spoolDir, DirectoryName, key) : null;
}
