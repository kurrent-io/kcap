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

    /// <summary>Holds <paramref name="endBody"/> until the runtime is gone or the transcript ends on its shutdown line.</summary>
    public static bool Write(WatcherPaths paths, string sessionId, string endBody, string? server, int? runtimePid, string transcriptPath, TimeProvider time) {
        try {
            var record = new JsonObject {
                ["server"]            = server,
                ["body"]              = endBody,
                ["runtime_pid"]       = runtimePid,
                ["runtime_token"]     = runtimePid is { } pid ? ProcessStartToken.ForPid(pid) : null,
                ["transcript_path"]   = transcriptPath,
                ["transcript_length"] = LengthOf(transcriptPath),
                ["written_at"]        = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)
            };

            Directory.CreateDirectory(paths.Directory);
            File.WriteAllText(FileFor(paths, sessionId), record.ToJsonString());

            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    /// <summary>Drops the session's pending end: a hook for it has run, and its end or its next run is that hook's.</summary>
    public static void Discard(WatcherPaths paths, string sessionId) {
        try { File.Delete(FileFor(paths, sessionId)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static IEnumerable<string> SessionsWithPending(WatcherPaths paths) =>
        Directory.Exists(paths.Directory)
            ? Directory.EnumerateFiles(paths.Directory, "*" + Suffix).Select(file => Path.GetFileName(file)[..^Suffix.Length]).ToList()
            : [];

    /// <summary>The Copilot session a drained lifecycle entry ended, when the server took it. No finalizer follows an end
    /// a drain delivers, so the drain closes that session's workspace span.</summary>
    public static string? EndedSession(string route, string body, DrainOutcome outcome) {
        if (route != Route || outcome != DrainOutcome.Delivered) return null;

        try {
            return JsonNode.Parse(body)?["session_id"]?.GetValue<string>();
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException) {
            return null;
        }
    }

    /// <summary>Spools every pending end for <paramref name="server"/> that is due and returns how many. Never throws.</summary>
    public static int SpoolDue(WatcherPaths paths, HookSpool lifecycle, string? server, TimeProvider time) {
        var spooled = 0;

        foreach (var session in SessionsWithPending(paths)) {
            try {
                if (Decide(paths, session, lifecycle, server, time) is { } body && lifecycle.Append(session, Route, body)) {
                    Discard(paths, session);
                    spooled++;
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) {
                // The next pass tries again.
            }
        }

        return spooled;
    }

    /// <summary>The body to spool, or null to keep waiting; drops a record that can no longer be due.</summary>
    static string? Decide(WatcherPaths paths, string session, HookSpool lifecycle, string? server, TimeProvider time) {
        if (JsonNode.Parse(File.ReadAllText(FileFor(paths, session))) is not JsonObject record) return Drop();

        if (lifecycle.IsDisabledByPerson(session)) return Drop();

        if (record["server"]?.GetValue<string>() is { } recorded && !ServerIdentity.SameServer(recorded, server)) return null;

        if (!DateTimeOffset.TryParse(record["written_at"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var writtenAt)
         || time.GetUtcNow() - writtenAt > Retention) return Drop();

        var transcript = record["transcript_path"]?.GetValue<string>() ?? "";
        var shutdown   = EndsOnShutdown(transcript);

        if (!shutdown && NewRunSince(transcript, record["transcript_length"]?.GetValue<long>() ?? 0)) return Drop();

        // An unreadable token proves nothing either way: only an exited pid or two tokens that differ show the runtime gone.
        var pid         = record["runtime_pid"]?.GetValue<int>();
        var token       = record["runtime_token"]?.GetValue<string>();
        var runtimeGone = pid is { } runtime && WatcherLiveness.IsGone(runtime, token ?? "");

        // Without the runtime's identity, a live pid may be another process, so a transcript gone quiet stands in.
        var quietUnidentified = (pid is null || token is null) && time.GetUtcNow() - Later(LastWrite(transcript, writtenAt), writtenAt) >= QuietPeriod;

        return shutdown || runtimeGone || quietUnidentified ? record["body"]?.GetValue<string>() ?? Drop() : null;

        string? Drop() {
            Discard(paths, session);

            return null;
        }
    }

    /// <summary>Whether a line written after <paramref name="offset"/> starts a run: Copilot can still append the stopped
    /// run's final turn before its shutdown line, so growth alone proves nothing.</summary>
    static bool NewRunSince(string transcript, long offset) {
        try {
            if (!File.Exists(transcript)) return false;

            using var stream = new FileStream(transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset > stream.Length) return false;
            stream.Seek(offset, SeekOrigin.Begin);

            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line) {
                if (TypeOf(line) is "session.start" or "session.resume" or "user.message") return true;
            }

            return false;
        } catch (IOException) {
            return false;
        }
    }

    static string? TypeOf(string line) {
        try {
            using var doc = JsonDocument.Parse(line);

            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null;
        } catch (JsonException) {
            return null;
        }
    }

    static bool EndsOnShutdown(string transcript) {
        try {
            if (!File.Exists(transcript)) return false;

            string? last = null;

            foreach (var line in File.ReadLinesShared(transcript))
                if (!string.IsNullOrWhiteSpace(line)) last = line;

            return last is not null && CopilotShutdown.IsShutdownLine(last);
        } catch (IOException) {
            return false;
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

    static long LengthOf(string path) {
        try {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        } catch (IOException) {
            return 0;
        }
    }
}
