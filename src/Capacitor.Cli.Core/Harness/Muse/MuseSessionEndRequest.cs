using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Harness.Muse;

/// <summary>
/// A session-end the Muse SessionEnd hook hands to the session's watcher: the hook's own
/// <c>/hooks/session-end/muse</c> payload, left where the watcher looks every tick. Under
/// <c>muse exec</c> the hook is killed well inside a second, before it could drain the log and post
/// the end itself; the watcher is a detached process, so it posts the end after its final drain.
/// Whoever takes it claims it first by renaming it, an atomic step only one party can win, so two
/// watchers on the session, or a watcher and the re-arm of a resumed session, never both act on one end.
/// Each run the request names has a file of its own, so one run's end never overwrites or supersedes another's
/// whatever order their hooks fire in; a request naming no run keeps the name every version writes.
/// </summary>
public sealed record MuseSessionEndRequest(string Payload, string ClaimedPath, DateTimeOffset HandedAt, string SourcePath) {
    const string Suffix = ".muse-end-requested";

    /// <summary>The server the request was handed over for; null for one written before requests recorded it.</summary>
    public string? Server { get; init; }

    /// <summary>How many times its end has failed to be sent or kept.</summary>
    public int Attempts { get; init; }

    /// <summary>Where a request naming no run is handed over.</summary>
    public static string PathFor(WatcherPaths paths, string sessionId) =>
        Path.Combine(paths.Directory, sessionId + Suffix);

    /// <summary>Where <paramref name="payload"/>'s request is handed over: a file per run it names.</summary>
    public static string PathFor(WatcherPaths paths, string sessionId, string payload) =>
        RunKey(payload) is { } key ? Path.Combine(paths.Directory, $"{sessionId}.{key}{Suffix}") : PathFor(paths, sessionId);

    static string? RunKey(string payload) {
        try {
            if (JsonNode.Parse(payload) is not JsonObject end || end["agent_host_id"]?.GetValue<string>() is not { Length: > 0 } agentId) return null;
            var epoch = end["daemon_epoch"]?.GetValue<string>() ?? "";

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{agentId}\n{epoch}")))[..16].ToLowerInvariant();
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException) {
            return null;
        }
    }

    static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(30);
    static readonly TimeSpan MaxRetryDelay   = TimeSpan.FromMinutes(30);

    /// <summary>What the handoff itself records, inside the request so one rename publishes it with the payload. A
    /// server reading the payload ignores the key; a request written before it existed has none.</summary>
    const string HandoffKey = "kcap_handoff";

    /// <summary>Sessions with an unclaimed request.</summary>
    public static IReadOnlyList<string> SessionsWithPending(WatcherPaths paths) {
        try {
            if (!Directory.Exists(paths.Directory)) return [];

            return Directory.EnumerateFiles(paths.Directory, "*" + Suffix)
                .Select(f => Path.GetFileName(f)[..Path.GetFileName(f).IndexOf('.')])
                .Distinct(StringComparer.Ordinal)
                .ToList();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    /// <summary>The session's unclaimed requests, oldest hand-over first.</summary>
    static IEnumerable<string> Pending(WatcherPaths paths, string sessionId) {
        try {
            if (!Directory.Exists(paths.Directory)) return [];

            return Directory.EnumerateFiles(paths.Directory, $"{sessionId}.*")
                .Where(f => Path.GetFileName(f) is var name && name.EndsWith(Suffix, StringComparison.Ordinal)
                         && (name == sessionId + Suffix || name.Count(c => c == '.') == 2))
                .OrderBy(File.GetLastWriteTimeUtc)
                .ToList();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    static string ResumedPathFor(WatcherPaths paths, string sessionId) =>
        Path.Combine(paths.Directory, $"{sessionId}.muse-resumed");

    /// <summary>A request as stored: the payload to send, the server it is for, and its failed sends.</summary>
    readonly record struct Stored(string Payload, string? Server, int Attempts, DateTimeOffset NotBefore);

    static Stored Parse(string content) {
        try {
            if (JsonNode.Parse(content) is JsonObject end && end[HandoffKey] is JsonObject handoff) {
                end.Remove(HandoffKey);
                return new Stored(end.ToJsonString(),
                    handoff["server"]?.GetValue<string>(),
                    handoff["attempts"]?.GetValue<int>() ?? 0,
                    handoff["not_before"]?.GetValue<long>() is { } ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : DateTimeOffset.MinValue);
            }
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }

        return new Stored(content, null, 0, DateTimeOffset.MinValue);
    }

    static string Render(string payload, string? server, int attempts, DateTimeOffset notBefore) {
        try {
            if ((server is not null || attempts > 0) && JsonNode.Parse(payload) is JsonObject end) {
                end[HandoffKey] = new JsonObject {
                    ["server"]     = server,
                    ["attempts"]   = attempts,
                    ["not_before"] = notBefore.UtcTicks,
                };
                return end.ToJsonString();
            }
        } catch (JsonException) { }

        return payload;
    }

    static Stored? Peek(string source) {
        try {
            return Parse(File.ReadAllText(source));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>Written whole, then renamed into place, so a reader never sees a partial request, nor one without the
    /// server <paramref name="server"/> it is for.</summary>
    public static void Write(WatcherPaths paths, string sessionId, string payload, string? server = null) {
        Directory.CreateDirectory(paths.Directory);
        var target = PathFor(paths, sessionId, payload);
        var temp   = $"{target}.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.tmp";

        File.WriteAllText(temp, Render(payload, server, 0, DateTimeOffset.MinValue));
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>True while an unclaimed request is on disk.</summary>
    public static bool Exists(WatcherPaths paths, string sessionId) => Pending(paths, sessionId).Any();

    /// <summary>Takes the session's oldest unclaimed request that is due for <paramref name="owner"/>, or null when there
    /// is none or other parties took each first. An empty request is discarded rather than claimed.</summary>
    public static MuseSessionEndRequest? TryClaim(WatcherPaths paths, string sessionId, string owner, TimeProvider? time = null) =>
        TryClaim(paths, sessionId, owner, time, forServer: _ => true);

    /// <summary><see cref="TryClaim(WatcherPaths,string,string,TimeProvider?)"/>, taking only a request whose recorded
    /// server <paramref name="forServer"/> accepts; it is passed null for a request that recorded none.</summary>
    public static MuseSessionEndRequest? TryClaim(
            WatcherPaths paths, string sessionId, string owner, TimeProvider? time, Func<string?, bool> forServer) {
        var now = (time ?? TimeProvider.System).GetUtcNow();

        foreach (var source in Pending(paths, sessionId))
            if (Peek(source) is { } stored && stored.NotBefore <= now && forServer(stored.Server) && TryClaimAt(source, owner) is { } request)
                return request;

        return null;
    }

    /// <summary>Only a request handed over for <paramref name="serverUrl"/>, or one that recorded none: a recorder posts
    /// to its own server, and one for another is left to that server's drain.</summary>
    public static MuseSessionEndRequest? ClaimHandedEnd(WatcherPaths paths, string sessionId, string serverUrl, TimeProvider time) =>
        TryClaim(paths, sessionId, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), time,
            recorded => recorded is null || ServerIdentity.SameServer(recorded, serverUrl));

    static MuseSessionEndRequest? TryClaimAt(string source, string owner) {
        var claimed = $"{source}.{owner}.claimed";

        try {
            File.Move(source, claimed);
        } catch (FileNotFoundException) {
            return null;
        } catch (DirectoryNotFoundException) {
            return null;
        } catch (IOException) {
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        }

        string   content;
        DateTime handedAt;
        try {
            content  = File.ReadAllText(claimed);
            handedAt = File.GetLastWriteTimeUtc(claimed);
        } catch (IOException) {
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        }

        // The rename keeps the write time, which is when the hook handed the end over.
        var stored = Parse(content);
        if (!string.IsNullOrWhiteSpace(stored.Payload))
            return new MuseSessionEndRequest(stored.Payload, claimed, new DateTimeOffset(handedAt, TimeSpan.Zero), source) {
                Server = stored.Server, Attempts = stored.Attempts
            };

        Release(claimed);
        return null;
    }

    /// <summary>Records that the session carried on, so an end handed over before this moment is posted no
    /// more, whoever holds it: it is older than the session's new life.</summary>
    public static void MarkResumed(WatcherPaths paths, string sessionId) {
        try {
            Directory.CreateDirectory(paths.Directory);
            File.WriteAllText(ResumedPathFor(paths, sessionId), "");
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
    }

    /// <summary>Whether the session was marked resumed at or after <paramref name="handedAt"/>.</summary>
    public static bool ResumedSince(WatcherPaths paths, string sessionId, DateTimeOffset handedAt) {
        try {
            var marker = ResumedPathFor(paths, sessionId);
            return File.Exists(marker) && File.GetLastWriteTimeUtc(marker) >= handedAt.UtcDateTime;
        } catch (IOException) {
            return false;
        } catch (UnauthorizedAccessException) {
            return false;
        }
    }

    /// <summary>Puts a claimed request back for the next taker when its end could not be sent, unless the same run
    /// has handed over a newer one since, which supersedes it. Another run's request never does.</summary>
    public static void Unclaim(MuseSessionEndRequest request) {
        var source = request.SourcePath;

        try {
            if (File.Exists(source)) {
                File.Delete(request.ClaimedPath);
                return;
            }

            File.Move(request.ClaimedPath, source);
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
    }

    /// <summary>Puts back a request whose end could be neither sent nor kept, not to be taken again until a delay that
    /// doubles with each failure, up to a cap: whatever failed may well fail the same way at once. Its backoff is written
    /// into the request and published by the one rename that puts it back, keeping its hand-over time; a newer request
    /// of the same run supersedes it.</summary>
    public static void Defer(MuseSessionEndRequest request, TimeProvider time) {
        var attempts = request.Attempts + 1;
        var delay    = TimeSpan.FromTicks(Math.Min(MaxRetryDelay.Ticks, FirstRetryDelay.Ticks << Math.Min(attempts - 1, 16)));
        var temp     = $"{request.SourcePath}.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.tmp";

        try {
            File.WriteAllText(temp, Render(request.Payload, request.Server, attempts, time.GetUtcNow() + delay));
            File.SetLastWriteTimeUtc(temp, request.HandedAt.UtcDateTime);
            File.Move(temp, request.SourcePath);
            File.Delete(request.ClaimedPath);
        } catch (IOException) {
            TryDelete(temp);
            Unclaim(request);
        } catch (UnauthorizedAccessException) {
            TryDelete(temp);
            Unclaim(request);
        }
    }

    static void TryDelete(string path) {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Removes a claimed request once its end is posted or durably spooled.</summary>
    public static void Release(string claimedPath) => TryDelete(claimedPath);

    /// <summary>Removes the request and every claim on it, so a watcher armed afterwards is not ended by
    /// an end nobody posted.</summary>
    public static void DeleteAll(WatcherPaths paths, string sessionId) {
        try {
            if (!Directory.Exists(paths.Directory)) return;

            foreach (var file in Directory.EnumerateFiles(paths.Directory, $"{sessionId}.*").Where(f => f.Contains(Suffix, StringComparison.Ordinal)))
                File.Delete(file);
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
    }
}
