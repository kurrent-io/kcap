using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Harness.Muse;

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

    /// <summary>Written whole, then renamed into place, so a reader never sees a partial request.</summary>
    public static void Write(WatcherPaths paths, string sessionId, string payload) {
        Directory.CreateDirectory(paths.Directory);
        var target = PathFor(paths, sessionId, payload);
        var temp   = $"{target}.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.tmp";

        File.WriteAllText(temp, payload);
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>True while an unclaimed request is on disk.</summary>
    public static bool Exists(WatcherPaths paths, string sessionId) => Pending(paths, sessionId).Any();

    /// <summary>Takes the session's oldest unclaimed request for <paramref name="owner"/>, or null when there is none
    /// or other parties took each first. An empty request is discarded rather than claimed.</summary>
    public static MuseSessionEndRequest? TryClaim(WatcherPaths paths, string sessionId, string owner) {
        foreach (var source in Pending(paths, sessionId))
            if (TryClaimAt(source, owner) is { } request) return request;

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

    /// <summary>Removes a claimed request once its end is posted or durably spooled.</summary>
    public static void Release(string claimedPath) {
        try { File.Delete(claimedPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
