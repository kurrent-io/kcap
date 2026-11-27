using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Harness;

/// <summary>
/// A session-end a vendor's SessionEnd hook hands to the session's watcher: the hook's own
/// <c>/hooks/session-end/&lt;vendor&gt;</c> payload, left where the watcher looks every tick. Muse and Codex both kill
/// the hook within seconds (under <c>muse exec</c> well inside one; Codex caps it at 3 s), before it could drain the
/// transcript and post the end itself; the watcher is a detached process, so it posts the end after its final drain.
/// Muse's files keep the <c>.muse-end-requested</c> name every version has written, so a request handed over before the
/// store served other vendors is still taken; another vendor's carry its own name, which a kcap that predates it never
/// takes for Muse's.
/// Whoever takes it claims it first by renaming it, an atomic step only one party can win, so two
/// watchers on the session, or a watcher and the re-arm of a resumed session, never both act on one end.
/// Each run the request names has a file of its own, so one run's end never overwrites or supersedes another's
/// whatever order their hooks fire in; a request naming no run keeps the name every version writes.
/// </summary>
public sealed record HandedSessionEndRequest(string Payload, string ClaimedPath, DateTimeOffset HandedAt, string SourcePath) {
    const string RequestedSuffix = "-end-requested";

    static string SuffixOf(string vendor) => $".{vendor}{RequestedSuffix}";

    static bool IsRequest(string name) => name.EndsWith(RequestedSuffix, StringComparison.Ordinal);

    /// <summary>A hook and a watcher may spell the id in different cases.</summary>
    static bool OfSession(string name, string sessionId) => name.StartsWith(sessionId + ".", StringComparison.OrdinalIgnoreCase);

    /// <summary>The server the request was handed over for; null for one written before requests recorded it.</summary>
    public string? Server { get; init; }

    /// <summary>The vendor whose hook handed it over, which names its session-end route. A request written before
    /// requests recorded one is Muse's, the only vendor that handed ends over then.</summary>
    public string Vendor { get; init; } = DefaultVendor;

    const string DefaultVendor = "muse";

    /// <summary>The lifecycle route the request's end is posted on.</summary>
    public string Route => $"session-end/{Vendor}";

    /// <summary>Whether <paramref name="vendor"/>'s SessionEnd hook hands its end to the session's watcher.</summary>
    public static bool HandedOverBy(string vendor) => vendor is "muse" or "codex";

    /// <summary>The coding-agent process whose hook handed the end over, when it could tell: an end of a process the
    /// session has since moved on from is not the session's.</summary>
    public int? AgentPid { get; init; }

    /// <summary>How many times its end has failed to be sent or kept.</summary>
    public int Attempts { get; init; }

    /// <summary>Where a Muse request naming no run is handed over.</summary>
    public static string PathFor(WatcherPaths paths, string sessionId) => PathFor(paths, sessionId, payload: "", DefaultVendor);

    /// <summary>Where <paramref name="vendor"/>'s request for <paramref name="payload"/> is handed over: a file per run it names.</summary>
    public static string PathFor(WatcherPaths paths, string sessionId, string payload, string vendor = DefaultVendor) =>
        Path.Combine(paths.Directory, RunKey(payload) is { } key ? $"{sessionId}.{key}{SuffixOf(vendor)}" : sessionId + SuffixOf(vendor));

    static string? RunKey(string payload) {
        try {
            if (JsonNode.Parse(payload) is not JsonObject end || end["agent_host_id"]?.GetValue<string>() is not { Length: > 0 } agentId) return null;
            var epoch = end["daemon_epoch"]?.GetValue<string>() ?? "";

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{agentId}\n{epoch}")))[..16].ToLowerInvariant();
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException) {
            return null;
        }
    }

    /// <summary>What a claim by this process is named for: its pid, so a claim it dies holding is recovered.</summary>
    public static string OwnClaim => Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

    static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(30);
    static readonly TimeSpan MaxRetryDelay   = TimeSpan.FromMinutes(30);

    /// <summary>What the handoff itself records, inside the request so one rename publishes it with the payload. A
    /// server reading the payload ignores the key; a request written before it existed has none.</summary>
    const string HandoffKey = "kcap_handoff";

    /// <summary>Sessions with an unclaimed request.</summary>
    public static IReadOnlyList<string> SessionsWithPending(WatcherPaths paths) {
        try {
            if (!Directory.Exists(paths.Directory)) return [];

            return Directory.EnumerateFiles(paths.Directory, "*" + RequestedSuffix)
                .Select(f => Path.GetFileName(f))
                .Where(name => IsRequest(name) && name.IndexOf('.') > 0)
                .Select(name => name[..name.IndexOf('.')])
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

            // Matched literally, never as a search pattern: a session id is no glob.
            return Directory.EnumerateFiles(paths.Directory, "*" + RequestedSuffix)
                .Where(f => Path.GetFileName(f) is var name && OfSession(name, sessionId) && IsRequest(name) && name.Count(c => c == '.') is 1 or 2)
                .OrderBy(File.GetLastWriteTimeUtc)
                .ToList();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    static string ResumedPathFor(WatcherPaths paths, string sessionId) =>
        Path.Combine(paths.Directory, $"{sessionId}.muse-resumed");

    /// <summary>A request as stored: the payload to send, the server it is for, and its failed sends.</summary>
    readonly record struct Stored(string Payload, string? Server, int Attempts, DateTimeOffset NotBefore, string Vendor, int? AgentPid = null);

    static Stored Parse(string content) {
        try {
            if (JsonNode.Parse(content) is JsonObject end && end[HandoffKey] is JsonObject handoff) {
                end.Remove(HandoffKey);
                return new Stored(end.ToJsonString(),
                    handoff["server"]?.GetValue<string>(),
                    handoff["attempts"]?.GetValue<int>() ?? 0,
                    handoff["not_before"]?.GetValue<long>() is { } ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : DateTimeOffset.MinValue,
                    handoff["vendor"]?.GetValue<string>() is { Length: > 0 } vendor ? vendor : DefaultVendor,
                    handoff["agent_pid"]?.GetValue<int>());
            }
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }

        return new Stored(content, null, 0, DateTimeOffset.MinValue, DefaultVendor);
    }

    static string Render(string payload, string? server, int attempts, DateTimeOffset notBefore, string vendor, int? agentPid) {
        try {
            if ((server is not null || attempts > 0 || vendor != DefaultVendor || agentPid is not null) && JsonNode.Parse(payload) is JsonObject end) {
                end[HandoffKey] = new JsonObject {
                    ["server"]     = server,
                    ["attempts"]   = attempts,
                    ["not_before"] = notBefore.UtcTicks,
                    ["vendor"]     = vendor,
                    ["agent_pid"]  = agentPid,
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
    /// server <paramref name="server"/> it is for or the <paramref name="vendor"/> whose route it is posted on.</summary>
    /// <param name="agentPid">The coding-agent process whose hook hands the end over, when known.</param>
    public static void Write(WatcherPaths paths, string sessionId, string payload, string? server = null, string vendor = DefaultVendor, int? agentPid = null) {
        Directory.CreateDirectory(paths.Directory);
        var target = PathFor(paths, sessionId, payload, vendor);
        var temp   = $"{target}.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.tmp";

        File.WriteAllText(temp, Render(payload, server, 0, DateTimeOffset.MinValue, vendor, agentPid));
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>True while a request for the session is on disk, unclaimed or held by a taker.</summary>
    public static bool Outstanding(WatcherPaths paths, string sessionId) {
        try {
            return Directory.Exists(paths.Directory)
                && Directory.EnumerateFiles(paths.Directory).Any(f => Path.GetFileName(f) is var name && OfSession(name, sessionId)
                                                                    && name.Contains(RequestedSuffix, StringComparison.Ordinal) && !name.EndsWith(".tmp", StringComparison.Ordinal));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    /// <summary>True while an unclaimed request is on disk.</summary>
    public static bool Exists(WatcherPaths paths, string sessionId) => Pending(paths, sessionId).Any();

    /// <summary>Takes the session's oldest unclaimed request that is due for <paramref name="owner"/>, or null when there
    /// is none or other parties took each first. An empty request is discarded rather than claimed.</summary>
    public static HandedSessionEndRequest? TryClaim(WatcherPaths paths, string sessionId, string owner, TimeProvider? time = null) =>
        TryClaim(paths, sessionId, owner, time, forServer: _ => true);

    /// <summary><see cref="TryClaim(WatcherPaths,string,string,TimeProvider?)"/>, taking only a request whose recorded
    /// server <paramref name="forServer"/> accepts; it is passed null for a request that recorded none.</summary>
    public static HandedSessionEndRequest? TryClaim(
            WatcherPaths paths, string sessionId, string owner, TimeProvider? time, Func<string?, bool> forServer) {
        var now = (time ?? TimeProvider.System).GetUtcNow();

        foreach (var source in Pending(paths, sessionId)) {
            if (Peek(source) is { } stored && stored.NotBefore <= now && forServer(stored.Server) && TryClaimAt(source, owner) is { } request)
                return request;
        }

        return null;
    }

    /// <summary>Only a request handed over for <paramref name="serverUrl"/>, or one that recorded none: a recorder posts
    /// to its own server, and one for another is left to that server's drain.</summary>
    public static HandedSessionEndRequest? ClaimHandedEnd(WatcherPaths paths, string sessionId, string serverUrl, TimeProvider time) =>
        TryClaim(paths, sessionId, OwnClaim, time,
            recorded => recorded is null || ServerIdentity.SameServer(recorded, serverUrl));

    static HandedSessionEndRequest? TryClaimAt(string source, string owner) {
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
            return new HandedSessionEndRequest(stored.Payload, claimed, new DateTimeOffset(handedAt, TimeSpan.Zero), source) {
                Server = stored.Server, Attempts = stored.Attempts, Vendor = stored.Vendor, AgentPid = stored.AgentPid
            };

        Release(claimed);
        return null;
    }

    /// <summary>Records that the session carried on, so an end handed over before this moment is posted no
    /// more, whoever holds it: it is older than the session's new life. The session carries on on
    /// <paramref name="server"/>, when known; an end owed to another server is still that server's.</summary>
    public static void MarkResumed(WatcherPaths paths, string sessionId, string? server = null) {
        try {
            Directory.CreateDirectory(paths.Directory);
            File.WriteAllText(ResumedPathFor(paths, sessionId), server ?? "");
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
    }

    /// <summary>Whether the session was marked resumed at or after <paramref name="handedAt"/>, on
    /// <paramref name="server"/> when both the mark and the end know theirs.</summary>
    public static bool ResumedSince(WatcherPaths paths, string sessionId, DateTimeOffset handedAt, string? server = null) {
        try {
            var marker = ResumedPathFor(paths, sessionId);
            if (!File.Exists(marker) || File.GetLastWriteTimeUtc(marker) < handedAt.UtcDateTime) return false;

            return server is null || File.ReadAllText(marker) is not { Length: > 0 } resumedOn || ServerIdentity.SameServer(resumedOn, server);
        } catch (IOException) {
            return false;
        } catch (UnauthorizedAccessException) {
            return false;
        }
    }

    /// <summary>Puts a claimed request back for the next taker when its end could not be sent, unless the same run
    /// has handed over a newer one since, which supersedes it. Another run's request never does.</summary>
    public static void Unclaim(HandedSessionEndRequest request) {
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
    public static void Defer(HandedSessionEndRequest request, TimeProvider time) {
        var attempts = request.Attempts + 1;
        var delay    = TimeSpan.FromTicks(Math.Min(MaxRetryDelay.Ticks, FirstRetryDelay.Ticks << Math.Min(attempts - 1, 16)));
        var temp     = $"{request.SourcePath}.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.tmp";

        try {
            File.WriteAllText(temp, Render(request.Payload, request.Server, attempts, time.GetUtcNow() + delay, request.Vendor, request.AgentPid));
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

    /// <summary>Puts back every request a process claimed and then died holding, before it could send or return it, so
    /// the next taker sends it. A claim names its process (<see cref="OwnClaim"/>); a newer request of the same run
    /// supersedes the one put back. A pid that was reused leaves the claim for a later pass.</summary>
    public static void RecoverAbandoned(WatcherPaths paths) {
        try {
            if (!Directory.Exists(paths.Directory)) return;

            foreach (var claimed in Directory.EnumerateFiles(paths.Directory, $"*{RequestedSuffix}.*.claimed").ToList()) {
                var withoutClaim = claimed[..^".claimed".Length];
                var dot          = withoutClaim.LastIndexOf('.');

                if (!int.TryParse(withoutClaim[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                 || !WatcherLiveness.IsGone(pid, "")) continue;

                Unclaim(new HandedSessionEndRequest("", claimed, default, withoutClaim[..dot]));
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A session carrying on makes any end handed over before now stale. The mark covers a claim a watcher
    /// already holds, which it drops before posting and arms a successor; the claim here takes an end nobody has, so it
    /// cannot end the watcher armed next. An end handed over after the mark is the resumed run's own and goes back for
    /// that watcher.</summary>
    /// <param name="server">The server the session carries on on: an end owed to another is left to it.</param>
    public static void DiscardForResume(WatcherPaths paths, string sessionId, string? server) {
        MarkResumed(paths, sessionId, server);

        var kept = new List<HandedSessionEndRequest>();
        while (TryClaim(paths, sessionId, OwnClaim, time: null,
                   recorded => recorded is null || server is null || ServerIdentity.SameServer(recorded, server)) is { } handed) {
            if (ResumedSince(paths, sessionId, handed.HandedAt, handed.Server)) Release(handed.ClaimedPath);
            else kept.Add(handed);
        }

        foreach (var handed in kept) Unclaim(handed);
    }

    /// <summary>Removes a claimed request once its end is posted or durably spooled.</summary>
    public static void Release(string claimedPath) => TryDelete(claimedPath);

    /// <summary>Removes the request and every claim on it, so a watcher armed afterwards is not ended by
    /// an end nobody posted.</summary>
    public static void DeleteAll(WatcherPaths paths, string sessionId) {
        try {
            if (!Directory.Exists(paths.Directory)) return;

            foreach (var file in Directory.EnumerateFiles(paths.Directory)
                         .Where(f => Path.GetFileName(f) is var name && OfSession(name, sessionId) && name.Contains(RequestedSuffix, StringComparison.Ordinal)).ToList())
                File.Delete(file);
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
    }
}
