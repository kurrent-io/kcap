using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Workspace;

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

    static string FileFor(WatcherPaths paths, string sessionId) => Path.Join(paths.Directory, sessionId + Suffix);

    /// <summary>Records the close of the run its transcript names <paramref name="runId"/> or, where that could not be read,
    /// of the run whose spans were opened by now.</summary>
    public static bool Record(WatcherPaths paths, string sessionId, string? server, string endBody, TimeProvider? time = null, string? runId = null) =>
        Write(paths, sessionId, new JsonObject {
            ["server"]       = server,
            ["end"]          = endBody,
            ["delivered"]    = false,
            ["recorded_at"]  = (time ?? TimeProvider.System).GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            ["run_id"]       = runId
        });

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
    public static void Discard(WatcherPaths paths, string sessionId) => TryDelete(FileFor(paths, sessionId));

    // Best effort: a marker left behind is settled or aged out on a later pass.
    static void TryDelete(string path) {
        try {
            File.Delete(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine($"[kcap] could not delete {path}: {ex.Message}");
        }
    }

    static JsonNode? ParseOrNull(string text) {
        try {
            return JsonNode.Parse(text);
        } catch (JsonException) {
            return null;
        }
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

    /// <summary>Runs <paramref name="close"/> with the run whose spans it may close, so it closes none a later run opened,
    /// and which says whether none of that run's is left, for every close owed on
    /// <paramref name="server"/> whose end was delivered, and returns how many it settled. Starts no close once
    /// <paramref name="budget"/> is spent. A marker a new run replaced or dropped meanwhile is neither closed nor removed.
    /// Never throws.</summary>
    public static async Task<int> CloseDueAsync(WatcherPaths paths, HookSpool lifecycle, string? server, TimeProvider time, Func<string, WorkspaceRunCutoff, Task<bool>> close,
            TimeSpan? budget = null) {
        var settled = 0;
        var started = time.GetTimestamp();

        foreach (var session in Pending(paths, server)) {
            if (budget is { } allowed && time.GetElapsedTime(started) >= allowed) break;

            try {
                if (Read(paths, session) is not { } owed) continue;

                if (owed["delivered"]?.GetValue<bool>() != true) {
                    if (!lifecycle.HasBacklog(session) && time.GetUtcNow() - File.GetLastWriteTimeUtc(FileFor(paths, session)) > UndeliveredFor)
                        Discard(paths, session);

                    continue;
                }

                var marker = owed.ToJsonString();

                // A marker from before markers named their run closes every span, as all did then.
                var run = new WorkspaceRunCutoff(
                    owed["run_id"]?.GetValue<string>(),
                    DateTimeOffset.TryParse(owed["recorded_at"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var recorded) ? recorded : null);

                if (!await close(session, run) || Read(paths, session)?.ToJsonString() != marker) continue;

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
            TryDelete(temp);

            return false;
        }
    }

    static JsonObject? Read(WatcherPaths paths, string sessionId) {
        var file = FileFor(paths, sessionId);

        JsonNode? parsed;

        try {
            if (!File.Exists(file)) return null;

            parsed = ParseOrNull(File.ReadAllText(file));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }

        if (parsed is JsonObject owed) return owed;

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
