using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>
/// The workspace span close a spooled Copilot end owes its session, kept with the server whose spans it closes and the end
/// it follows. Recorded before the end is spooled, so no end can be delivered without it. The lifecycle drain marks it when
/// the server takes that end; a detached finalizer or the daemon then pays it, and it stays owed until a close leaves
/// nothing of the session behind. An end that leaves the spool any other way, superseded by a resumed run, rejected or
/// never spooled, takes the debt with it. The drain ahead of every hook pays none: under Copilot's shared shutdown budget,
/// closing a span there would starve the hook's own end.
/// </summary>
public static class CopilotSpanClose {
    const string Suffix = ".span-close-pending";

    static string FileFor(WatcherPaths paths, string sessionId) => Path.Combine(paths.Directory, sessionId + Suffix);

    public static bool Record(WatcherPaths paths, string sessionId, string? server, string endBody) =>
        Write(paths, sessionId, new JsonObject { ["server"] = server, ["end"] = endBody, ["delivered"] = false });

    /// <summary>What the lifecycle drain made of an entry: the owed end's delivery makes its close due, a rejection drops it.</summary>
    public static void NoteOutcome(WatcherPaths paths, string route, string body, DrainOutcome outcome) {
        if (route != CopilotPendingEnd.Route || outcome == DrainOutcome.TransientStop || SessionOf(body) is not { } session) return;
        if (Read(paths, session) is not { } owed || owed["end"]?.GetValue<string>() != body) return;

        if (outcome == DrainOutcome.Delivered) {
            owed["delivered"] = true;
            Write(paths, session, owed);
        } else {
            Discard(paths, session);
        }
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
    /// <paramref name="server"/> whose end was delivered, drops the ones whose end left <paramref name="lifecycle"/>
    /// undelivered, and returns how many it settled. Never throws.</summary>
    public static async Task<int> CloseDueAsync(WatcherPaths paths, HookSpool lifecycle, string? server, Func<string, Task<bool>> close) {
        var settled = 0;

        foreach (var session in Pending(paths, server)) {
            try {
                if (!IsDelivered(paths, session)) {
                    if (!lifecycle.HasBacklog(session)) Discard(paths, session);

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

    static bool Write(WatcherPaths paths, string sessionId, JsonObject owed) {
        try {
            Directory.CreateDirectory(paths.Directory);
            File.WriteAllText(FileFor(paths, sessionId), owed.ToJsonString());

            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    static JsonObject? Read(WatcherPaths paths, string sessionId) {
        try {
            return File.Exists(FileFor(paths, sessionId)) ? JsonNode.Parse(File.ReadAllText(FileFor(paths, sessionId))) as JsonObject : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }

    static string? SessionOf(string body) {
        try {
            return JsonNode.Parse(body)?["session_id"]?.GetValue<string>();
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException) {
            return null;
        }
    }
}
