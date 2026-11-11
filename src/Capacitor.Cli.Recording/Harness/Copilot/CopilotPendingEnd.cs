using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>
/// An end a stopped Copilot watcher could not yet prove: quitting VS Code terminates its watchers before the runtime has
/// exited or written <c>session.shutdown</c>, and a session whose sessionEnd hook Copilot skipped has no other owner.
/// Every spool drain moves a due one into the lifecycle spool, and drops one whose session began a new run.
/// </summary>
public static class CopilotPendingEnd {
    public const string Route = "session-end/copilot";

    const string Suffix = ".copilot-pending-end.json";

    static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    // For a watcher that never resolved its runtime: Copilot writes its shutdown line within seconds of stopping, and a
    // session waiting on a person is woken by a new run, which drops the record, so ten quiet minutes stand for an exit.
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(10);

    static string FileFor(WatcherPaths paths, string sessionId) => Path.Combine(paths.Directory, sessionId + Suffix);

    /// <summary>Holds <paramref name="endBody"/> until the runtime is gone or the transcript ends on its shutdown line.
    /// <paramref name="runId"/> is the run the transcript named when the stop arrived: a later run supersedes this end.
    /// <paramref name="watcherPid"/> is the writer, which a later watcher replaces.</summary>
    public static bool Write(WatcherPaths paths, string sessionId, string endBody, string? server, int? runtimePid, string transcriptPath, TimeProvider time,
            string? runId = null, int? watcherPid = null) {
        var record = new JsonObject {
            ["server"]            = server,
            ["body"]              = endBody,
            ["runtime_pid"]       = runtimePid,
            ["runtime_token"]     = runtimePid is { } pid ? ProcessStartToken.ForPid(pid) : null,
            ["watcher_pid"]       = watcherPid,
            ["transcript_path"]   = transcriptPath,
            ["run_id"]            = runId,
            ["written_at"]        = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)
        };

        using var held = CopilotPendingEndLock.TryAcquire(paths, sessionId);

        return held is not null && Save(paths, sessionId, record);
    }

    // Through a temp file and a rename, so a killed write never leaves a torn record. The temp's name matches none. The
    // caller holds the session's CopilotPendingEndLock.
    internal static bool Save(WatcherPaths paths, string sessionId, JsonObject record) {
        var target = FileFor(paths, sessionId);
        var temp   = $"{target}.{Guid.NewGuid():N}.tmp";

        try {
            Directory.CreateDirectory(paths.Directory);
            File.WriteAllText(temp, record.ToJsonString());
            File.Move(temp, target, overwrite: true);

            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            TryDelete(temp);

            return false;
        }
    }

    // A watcher asked to stop drains and exits within KillWatcher's 5 s force-kill; a minute covers a slow host.
    static readonly TimeSpan WatcherHold = TimeSpan.FromMinutes(1);

    /// <summary>Whether a drain should hold a Copilot end while the session's watcher still runs: it may yet spool the tail
    /// the end must follow. Only a watcher whose start token proves it is still that incarnation holds it, and only for
    /// <see cref="WatcherHold"/> after the end; a pid file that cannot be read or verified holds nothing.</summary>
    public static bool HeldForWatcher(WatcherPaths paths, string route, string body, TimeProvider time) {
        if (route != Route || ParseOrNull(body) is not JsonObject end || end["session_id"]?.GetValue<string>() is not { } session) return false;

        if (!DateTimeOffset.TryParse(end["ended_at"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var endedAt)
         || time.GetUtcNow() - endedAt > WatcherHold) return false;

        try {
            var lines = File.ReadAllLines(paths.PidFile(session));

            return lines.Length > 1 && int.TryParse(lines[0].Trim(), out var pid) && lines[1].Trim() is { Length: > 0 } token
                && ProcessStartToken.Matches(pid, token) == true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    static JsonNode? ParseOrNull(string text) {
        try {
            return JsonNode.Parse(text);
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>Drops the session's pending end: a hook for it has run, and its end or its next run is that hook's.</summary>
    /// <param name="ownedBy">The watcher discarding the end it wrote, which leaves one a later watcher wrote since.</param>
    public static void Discard(WatcherPaths paths, string sessionId, int? ownedBy = null) {
        using var held = CopilotPendingEndLock.TryAcquire(paths, sessionId);
        if (held is null) return;

        if (ownedBy is { } owner && ReadRecord(paths, sessionId)?["watcher_pid"]?.GetValue<int>() != owner) return;

        TryDelete(FileFor(paths, sessionId));
    }

    static JsonObject? ReadRecord(WatcherPaths paths, string sessionId) {
        try {
            return File.Exists(FileFor(paths, sessionId)) ? JsonNode.Parse(File.ReadAllText(FileFor(paths, sessionId))) as JsonObject : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }

    // Best effort: a record left behind is retried or dropped on the next pass.
    static void TryDelete(string path) {
        try {
            File.Delete(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine($"[kcap] could not delete {path}: {ex.Message}");
        }
    }

    public static IEnumerable<string> SessionsWithPending(WatcherPaths paths) =>
        Directory.Exists(paths.Directory)
            ? Directory.EnumerateFiles(paths.Directory, "*" + Suffix).Select(file => Path.GetFileName(file)[..^Suffix.Length]).ToList()
            : [];

    /// <summary>Spools every pending end for <paramref name="server"/> that is due and returns how many. Never throws.</summary>
    public static int SpoolDue(WatcherPaths paths, HookSpool lifecycle, string? server, TimeProvider time) {
        var spooled = 0;

        foreach (var session in SessionsWithPending(paths)) {
            try {
                // From the read to the promotion or drop: a hook or watcher writing meanwhile would be overwritten or revived.
                using var held = CopilotPendingEndLock.TryAcquire(paths, session);
                if (held is null) continue;

                // The span close it owes is recorded first, so no spooled end goes without one.
                if (Decide(paths, session, lifecycle, server, time) is { } due
                 && CopilotSpanClose.Record(paths, session, server, due.Body, time, due.RunId) && lifecycle.Append(session, Route, due.Body)) {
                    TryDelete(FileFor(paths, session));
                    spooled++;
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) {
                // The next pass tries again.
            }
        }

        return spooled;
    }

    /// <summary>A pending end that is due, with the run its transcript was on when the watcher stopped.</summary>
    readonly record struct Due(string Body, string? RunId);

    // VS Code rewrites the transcript in place, so one read can catch it empty or half written. A run found gone or
    // superseded counts only once the file has stayed exactly so across passes this far apart.
    static readonly TimeSpan MismatchSettles = TimeSpan.FromSeconds(30);

    /// <summary>The body to spool, or null to keep waiting; drops a record that can no longer be due.</summary>
    /// <remarks>The caller holds the session's <see cref="CopilotPendingEndLock"/>.</remarks>
    static Due? Decide(WatcherPaths paths, string session, HookSpool lifecycle, string? server, TimeProvider time) {
        if (!File.Exists(FileFor(paths, session))) return null;

        JsonObject? record;

        try {
            record = JsonNode.Parse(File.ReadAllText(FileFor(paths, session))) as JsonObject;
        } catch (JsonException) {
            record = null;
        }

        if (record is null) {
            Console.Error.WriteLine($"[kcap] dropping unreadable pending Copilot end for {session}");

            return Drop();
        }

        if (lifecycle.IsDisabledByPerson(session)) return Drop();

        if (record["server"]?.GetValue<string>() is { } recorded && !ServerIdentity.SameServer(recorded, server)) return null;

        if (!DateTimeOffset.TryParse(record["written_at"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var writtenAt)
         || time.GetUtcNow() - writtenAt > Retention) return Drop();

        var transcript = record["transcript_path"]?.GetValue<string>() ?? "";

        // A later run owns the session's end, even one that has since shut down too. A rewrite that took this run's own line
        // away leaves nothing to tell, and a premature end cannot be undone where a sweep's late one can. A known run waits
        // while the transcript cannot be read at all, for as long as the record is kept.
        var runId = record["run_id"]?.GetValue<string>();
        var runs  = CopilotRunBoundary.Read(transcript);

        if (runs is null && runId is not null) return null;

        if (runs is not null) {
            var mismatch = runId is not null && !runs.Ids.Contains(runId) ? "its run is gone from the rewritten transcript"
                         : runs.LatestId != runId                       ? "a later run began"
                         :                                                null;

            if (mismatch is null) {
                if (record.Remove("mismatch")) Save(paths, session, record);
            } else if (Settled(paths, session, record, transcript, time)) {
                Console.Error.WriteLine($"[kcap] dropping pending Copilot end for {session}: {mismatch}");

                return Drop();
            } else {
                return null;
            }
        }

        // A watcher started since this one stopped records the session and sends its end.
        if (WatcherLiveness.PidAlive(paths, session) && CurrentWatcherPid(paths, session) != record["watcher_pid"]?.GetValue<int>()) return Drop();

        var shutdown = EndsOnShutdown(transcript);

        // An unreadable token proves nothing either way: only an exited pid or two tokens that differ show the runtime gone.
        var pid         = record["runtime_pid"]?.GetValue<int>();
        var token       = record["runtime_token"]?.GetValue<string>();
        var runtimeGone = pid is { } runtime && WatcherLiveness.IsGone(runtime, token ?? "");

        // Without the runtime's identity, a live pid may be another process, so a transcript gone quiet stands in.
        var quietUnidentified = (pid is null || token is null) && time.GetUtcNow() - Later(LastWrite(transcript, writtenAt), writtenAt) >= QuietPeriod;

        if (!shutdown && !runtimeGone && !quietUnidentified) return null;

        if (record["body"]?.GetValue<string>() is not { } body) return Drop();

        return new Due(body, runId);

        Due? Drop() {
            TryDelete(FileFor(paths, session));

            return null;
        }
    }

    /// <summary>Whether the transcript has stayed exactly as a mismatch first found it for <see cref="MismatchSettles"/>;
    /// otherwise records how it looks now, so a later pass can tell.</summary>
    static bool Settled(WatcherPaths paths, string session, JsonObject record, string transcript, TimeProvider time) {
        var info  = new FileInfo(transcript);
        var shape = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";

        if (record["mismatch"] is JsonObject seen && seen["shape"]?.GetValue<string>() == shape
         && DateTimeOffset.TryParse(seen["since"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var since)) {
            return time.GetUtcNow() - since >= MismatchSettles;
        }

        record["mismatch"] = new JsonObject { ["since"] = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), ["shape"] = shape };
        Save(paths, session, record);

        return false;
    }

    static bool EndsOnShutdown(string transcript) {
        try {
            if (!File.Exists(transcript)) return false;

            return File.ReadLinesShared(transcript).LastOrDefault(line => !string.IsNullOrWhiteSpace(line)) is { } last && CopilotShutdown.IsShutdownLine(last);
        } catch (IOException) {
            return false;
        }
    }

    static int? CurrentWatcherPid(WatcherPaths paths, string session) {
        try {
            return int.TryParse(File.ReadLines(paths.PidFile(session)).FirstOrDefault()?.Trim(), out var pid) ? pid : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    static DateTimeOffset LastWrite(string path, DateTimeOffset fallback) {
        try {
            return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : fallback;
        } catch (IOException) {
            return fallback;
        }
    }
}
