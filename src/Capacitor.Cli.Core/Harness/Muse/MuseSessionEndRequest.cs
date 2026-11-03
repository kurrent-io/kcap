using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    /// <summary>Written whole, then renamed into place, so a reader never sees a partial request. <paramref name="server"/>
    /// is where the end belongs, recorded first so the request is never visible without it.</summary>
    public static void Write(WatcherPaths paths, string sessionId, string payload, string? server = null) {
        Directory.CreateDirectory(paths.Directory);
        var target = PathFor(paths, sessionId, payload);
        var temp   = $"{target}.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.tmp";

        if (server is not null) File.WriteAllText(ServerPathFor(target), server);
        else TryDelete(ServerPathFor(target));
        File.WriteAllText(temp, payload);
        File.Move(temp, target, overwrite: true);
        TryDelete(RetryPathFor(target));
    }

    static string ServerPathFor(string source) => source + ".server";

    /// <summary>The server a request was handed over for, or null for one written before it was recorded.</summary>
    static string? ServerOf(string source) {
        try {
            return File.Exists(ServerPathFor(source)) ? File.ReadAllText(ServerPathFor(source)) : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    static string RetryPathFor(string source) => source + ".retry";

    static string SourceOf(string claimedPath) =>
        claimedPath[..(claimedPath.IndexOf(Suffix, StringComparison.Ordinal) + Suffix.Length)];

    static void TryDelete(string path) {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>How many sends a request has failed, and when it may be taken again.</summary>
    static (int Attempts, DateTimeOffset NotBefore) RetryStateOf(string source) {
        try {
            if (File.ReadAllText(RetryPathFor(source)).Split('\n') is [var attempts, var notBefore]
             && int.TryParse(attempts, CultureInfo.InvariantCulture, out var n)
             && long.TryParse(notBefore, CultureInfo.InvariantCulture, out var ticks))
                return (n, new DateTimeOffset(ticks, TimeSpan.Zero));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return (0, DateTimeOffset.MinValue);
    }

    /// <summary>True while an unclaimed request is on disk.</summary>
    public static bool Exists(WatcherPaths paths, string sessionId) => Pending(paths, sessionId).Any();

    /// <summary>Takes the session's oldest unclaimed request for <paramref name="owner"/>, or null when there is none
    /// or other parties took each first. An empty request is discarded rather than claimed.</summary>
    public static MuseSessionEndRequest? TryClaim(WatcherPaths paths, string sessionId, string owner, TimeProvider? time = null) =>
        TryClaim(paths, sessionId, owner, time, forServer: _ => true);

    /// <summary><see cref="TryClaim(WatcherPaths,string,string,TimeProvider?)"/>, taking only a request whose recorded
    /// server <paramref name="forServer"/> accepts; it is passed null for a request that recorded none.</summary>
    public static MuseSessionEndRequest? TryClaim(
            WatcherPaths paths, string sessionId, string owner, TimeProvider? time, Func<string?, bool> forServer) {
        var now = (time ?? TimeProvider.System).GetUtcNow();

        foreach (var source in Pending(paths, sessionId))
            if (RetryStateOf(source).NotBefore <= now && forServer(ServerOf(source)) && TryClaimAt(source, owner) is { } request) return request;

        return null;
    }

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

        string   payload;
        DateTime handedAt;
        try {
            payload  = File.ReadAllText(claimed);
            handedAt = File.GetLastWriteTimeUtc(claimed);
        } catch (IOException) {
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        }

        // The rename keeps the write time, which is when the hook handed the end over.
        if (!string.IsNullOrWhiteSpace(payload)) return new MuseSessionEndRequest(payload, claimed, new DateTimeOffset(handedAt, TimeSpan.Zero), source);

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
    /// doubles with each failure, up to a cap: whatever failed may well fail the same way at once.</summary>
    public static void Defer(MuseSessionEndRequest request, TimeProvider time) {
        var attempts = RetryStateOf(request.SourcePath).Attempts + 1;
        var delay    = TimeSpan.FromTicks(Math.Min(MaxRetryDelay.Ticks, FirstRetryDelay.Ticks << Math.Min(attempts - 1, 16)));

        try {
            File.WriteAllText(RetryPathFor(request.SourcePath),
                $"{attempts.ToString(CultureInfo.InvariantCulture)}\n{(time.GetUtcNow() + delay).UtcTicks.ToString(CultureInfo.InvariantCulture)}");
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        Unclaim(request);
    }

    /// <summary>Removes a claimed request once its end is posted or durably spooled.</summary>
    public static void Release(string claimedPath) {
        TryDelete(claimedPath);
        if (File.Exists(SourceOf(claimedPath))) return;

        TryDelete(RetryPathFor(SourceOf(claimedPath)));
        TryDelete(ServerPathFor(SourceOf(claimedPath)));
    }

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
