using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>
/// The workspace span close a spooled Copilot end owes its session, kept with the server whose spans it closes and the end
/// it follows. Recorded before the end is spooled, so no end can be delivered without it. The lifecycle drain marks it when
/// the server takes that end; a detached finalizer or the daemon then pays it, and it stays owed until a close leaves
/// nothing of the session behind. An undelivered one goes on a rejection, a new run's start, or after
/// <see cref="UndeliveredFor"/>, never because its end is absent, which it also is just before the end is spooled. The drain
/// ahead of every hook pays none: under Copilot's shared shutdown budget, closing a span there would starve the hook's own end.
/// </summary>
public static class CopilotSpanClose {
    const string Suffix = ".span-close-pending";

    // Far past any spool retry or resume that could still deliver the end it follows.
    static readonly TimeSpan UndeliveredFor = TimeSpan.FromHours(24);

    static string FileFor(WatcherPaths paths, string sessionId) => Path.Combine(paths.Directory, sessionId + Suffix);

    public static bool Record(WatcherPaths paths, string sessionId, string? server, string endBody) =>
        Write(paths, sessionId, new JsonObject { ["server"] = server, ["end"] = endBody, ["delivered"] = false });

    /// <summary>What the lifecycle drain made of an entry: the owed end's delivery makes its close due, a rejection drops
    /// it. False when a delivery could not be marked, so the drain keeps the end for a retry rather than lose the close.</summary>
    public static bool NoteOutcome(WatcherPaths paths, string route, string body, DrainOutcome outcome) {
        if (route != CopilotPendingEnd.Route || outcome == DrainOutcome.TransientStop || SessionOf(body) is not { } session) return true;
        if (Read(paths, session) is not { } owed || owed["end"]?.GetValue<string>() != body) return true;

        if (outcome != DrainOutcome.Delivered) {
            Discard(paths, session);

            return true;
        }

        owed["delivered"] = true;

        return Write(paths, session, owed);
    }

    /// <summary>Drops the session's owed close: a new run starts its own span, which that close would end.</summary>
    public static void Discard(WatcherPaths paths, string sessionId) {
        try { File.Delete(FileFor(paths, sessionId)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static bool IsDelivered(WatcherPaths paths, string sessionId) => Read(paths, sessionId)?["delivered"]?.GetValue<bool>() == true;

    /// <summary>The sessions owed a close on <paramref name="server"/>.</summary>
    public static IEnumerable<string> Pending(WatcherPaths paths, string? server) {
        if (!Directory.Exists(paths.Directory)) return [];

        return Directory.EnumerateFiles(paths.Directory, "*" + Suffix)
            .Select(file => Path.GetFileName(file)[..^Suffix.Length])
            .Where(session => Read(paths, session) is { } owed && ServerIdentity.SameServer(owed["server"]?.GetValue<string>(), server))
            .ToList();
    }

    /// <summary>Runs <paramref name="close"/>, which says whether nothing is left, for every close owed on
    /// <paramref name="server"/> whose end was delivered, and returns how many it settled. Never throws.</summary>
    public static async Task<int> CloseDueAsync(WatcherPaths paths, HookSpool lifecycle, string? server, TimeProvider time, Func<string, Task<bool>> close) {
        var settled = 0;

        foreach (var session in Pending(paths, server)) {
            try {
                if (!IsDelivered(paths, session)) {
                    if (!lifecycle.HasBacklog(session) && time.GetUtcNow() - File.GetLastWriteTimeUtc(FileFor(paths, session)) > UndeliveredFor)
                        Discard(paths, session);

                    continue;
                }

                if (!await close(session)) continue;

                Discard(paths, session);
                settled++;
            } catch (Exception ex) when (ex is not OutOfMemoryException) {
                // Still owed: the next pass tries again.
            }
        }

        return settled;
    }

    // Through a temp file and a rename, so a killed write never leaves a torn marker. The temp's name matches no marker.
    static bool Write(WatcherPaths paths, string sessionId, JsonObject owed) {
        var target = FileFor(paths, sessionId);
        var temp   = $"{target}.{Guid.NewGuid():N}.tmp";

        try {
            Directory.CreateDirectory(paths.Directory);
            File.WriteAllText(temp, owed.ToJsonString());
            File.Move(temp, target, overwrite: true);

            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            try { File.Delete(temp); } catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }

            return false;
        }
    }

    static JsonObject? Read(WatcherPaths paths, string sessionId) {
        var file = FileFor(paths, sessionId);

        try {
            if (!File.Exists(file)) return null;
            if (JsonNode.Parse(File.ReadAllText(file)) is JsonObject owed) return owed;
        } catch (JsonException) {
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }

        Console.Error.WriteLine($"[kcap] dropping unreadable span-close marker for {sessionId}");
        Discard(paths, sessionId);

        return null;
    }

    static string? SessionOf(string body) {
        try {
            return JsonNode.Parse(body)?["session_id"]?.GetValue<string>();
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException) {
            return null;
        }
    }
}
