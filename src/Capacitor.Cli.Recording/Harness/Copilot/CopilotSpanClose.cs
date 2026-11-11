using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>
/// Workspace span closes a spooled Copilot end owes its session, kept with the server whose spans they close. Recorded
/// with the end, so a delivered end can never lose its close; paid by a detached finalizer or the daemon once the end has
/// left that server's spool, and owed until a close leaves nothing of the session behind. The drain ahead of every hook
/// pays none: under Copilot's shared shutdown budget, closing a span there would starve the hook's own end.
/// </summary>
public static class CopilotSpanClose {
    const string Suffix = ".span-close-pending";

    static string FileFor(WatcherPaths paths, string sessionId) => Path.Combine(paths.Directory, sessionId + Suffix);

    public static bool Record(WatcherPaths paths, string sessionId, string? server) {
        try {
            Directory.CreateDirectory(paths.Directory);
            File.WriteAllText(FileFor(paths, sessionId), server ?? "");

            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    /// <summary>The sessions owed a close on <paramref name="server"/>.</summary>
    public static IEnumerable<string> Pending(WatcherPaths paths, string? server) {
        if (!Directory.Exists(paths.Directory)) return [];

        var owed = new List<string>();

        foreach (var file in Directory.EnumerateFiles(paths.Directory, "*" + Suffix)) {
            try {
                if (ServerIdentity.SameServer(File.ReadAllText(file).Trim(), server)) owed.Add(Path.GetFileName(file)[..^Suffix.Length]);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        return owed;
    }

    /// <summary>Runs <paramref name="close"/>, which says whether nothing is left, for every session owed a close on
    /// <paramref name="server"/> whose end has left <paramref name="lifecycle"/>, and returns how many it settled. Never throws.</summary>
    public static async Task<int> CloseDueAsync(WatcherPaths paths, HookSpool lifecycle, string? server, Func<string, Task<bool>> close) {
        var settled = 0;

        foreach (var session in Pending(paths, server)) {
            try {
                if (lifecycle.HasBacklog(session) || !await close(session)) continue;

                File.Delete(FileFor(paths, session));
                settled++;
            } catch (Exception ex) when (ex is not OutOfMemoryException) {
                // Still owed: the next pass tries again.
            }
        }

        return settled;
    }
}
