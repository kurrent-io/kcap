using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>
/// An end a stopped Copilot watcher could not yet prove: quitting VS Code terminates its watchers before the runtime has
/// exited or written <c>session.shutdown</c>, and a session whose sessionEnd hook Copilot skipped has no other owner.
/// Every spool drain moves a due one into the lifecycle spool, and drops one whose session went on.
/// </summary>
public static class CopilotPendingEnd {
    public const string Route = "session-end/copilot";

    const string Suffix = ".copilot-pending-end.json";

    static readonly TimeSpan Retention = TimeSpan.FromDays(30);

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

        if (!shutdown && LengthOf(transcript) > (record["transcript_length"]?.GetValue<long>() ?? 0)) return Drop();

        var runtimeGone = record["runtime_pid"]?.GetValue<int>() is { } pid
                       && (record["runtime_token"]?.GetValue<string>() is not { } token || ProcessStartToken.ForPid(pid) is null || ProcessStartToken.Matches(pid, token) == false);

        return shutdown || runtimeGone ? record["body"]?.GetValue<string>() ?? Drop() : null;

        string? Drop() {
            Discard(paths, session);

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

    static long LengthOf(string path) {
        try {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        } catch (IOException) {
            return 0;
        }
    }
}
