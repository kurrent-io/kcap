using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>
/// Workspace span closes a delivered Copilot end owes its session. The drain ahead of every hook only records one: under
/// Copilot's shared shutdown budget, closing a span there would starve the hook's own end. A detached finalizer or the
/// daemon pays it, and it stays owed until a close leaves nothing of the session behind.
/// </summary>
public static class CopilotSpanClose {
    const string Suffix = ".span-close-pending";

    public static bool Record(WatcherPaths paths, string sessionId) {
        try {
            Directory.CreateDirectory(paths.Directory);
            File.WriteAllText(Path.Combine(paths.Directory, sessionId + Suffix), "");

            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    public static IEnumerable<string> Pending(WatcherPaths paths) =>
        Directory.Exists(paths.Directory)
            ? Directory.EnumerateFiles(paths.Directory, "*" + Suffix).Select(file => Path.GetFileName(file)[..^Suffix.Length]).ToList()
            : [];

    /// <summary>Runs <paramref name="close"/> for every owed session, which says whether nothing is left, and returns how
    /// many it settled. Never throws.</summary>
    public static async Task<int> CloseDueAsync(WatcherPaths paths, Func<string, Task<bool>> close) {
        var settled = 0;

        foreach (var session in Pending(paths)) {
            try {
                if (!await close(session)) continue;

                File.Delete(Path.Combine(paths.Directory, session + Suffix));
                settled++;
            } catch (Exception ex) when (ex is not OutOfMemoryException) {
                // Still owed: the next pass tries again.
            }
        }

        return settled;
    }
}
