using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core;

/// <summary>Result of a single spooled-entry replay attempt.</summary>
public enum DrainOutcome {
    Delivered,    // POST succeeded — advance past the entry
    Drop,         // the payload itself was rejected (HookSpool.IsRetryable is false) — discard, do not retry
    TransientStop // server down/timeout/budget/credential lapse — stop draining, keep the remainder
}

/// <summary>
/// Vendor-neutral on-disk spool for lifecycle hook POSTs whose delivery failed.
/// Per-session JSONL (<c>{DirectoryPath}/&lt;dashless-sid&gt;.jsonl</c>), one
/// <c>{"route","body"}</c> object per line in arrival order. Drains are
/// rotate-on-drain: the live file is atomically renamed to a private
/// <c>.draining</c> temp before reading, so concurrent appends never collide
/// with an in-flight drain.
///
/// <para>Under a config root, each server has its own directory (see <see cref="SpoolPartition"/>).</para>
/// </summary>
public sealed partial class HookSpool(SpoolPartition partition, TimeProvider time, int capBytes = HookSpool.DefaultCapBytes) {
    public const int DefaultCapBytes = 1_048_576; // 1 MB per session file

    /// <summary>
    /// Whether a lifecycle POST answered with <paramref name="statusCode"/> is kept for a later
    /// drain. The one rule every live post and every drain poster shares, so no vendor can spool a
    /// status the drain then drops. A 401 is retryable: it is a lapsed credential, which
    /// <c>kcap login</c> repairs, so the payload has to outlive the lapse — a dropped session-start
    /// leaves the session without a server-side record, a dropped session-end leaves it stuck
    /// active. Every other 4xx rejects the payload itself, and a replay would be rejected the same
    /// way. Retention while a lapse lasts is the spool's own: the per-session byte cap and the
    /// 30-day reap.
    /// </summary>
    public static bool IsRetryable(int statusCode) => statusCode is >= 500 or 408 or 429 or 401;

    /// <summary>The drain outcome for a non-success status: retryable stops the pass, anything else drops the entry.</summary>
    public static DrainOutcome OutcomeOf(int statusCode) =>
        IsRetryable(statusCode) ? DrainOutcome.TransientStop : DrainOutcome.Drop;

    // The subdirectory name belongs to the spool, not to ConfigRoot: a root that enumerated its
    // tenants' directories would change every time one of them gained a file.
    public const string DirectoryName = "spool";

    /// <summary>The spool under a config root. The directory overload is for a test's own throwaway directory.</summary>
    public HookSpool(ConfigRoot config, TimeProvider time, string? server, int capBytes = DefaultCapBytes)
        : this(SpoolPartition.Server(config.Path(DirectoryName), server), time, capBytes) { }

    public HookSpool(string dir, TimeProvider time, int capBytes = DefaultCapBytes)
        : this(SpoolPartition.Standalone(dir), time, capBytes) { }

    static readonly Regex SafeSessionId  = SafeSessionIdRegex();
    static readonly Regex LegacyGuidKey = LegacyGuidKeyRegex();
    static          int   seqCounter;

    /// <summary>The directory where spool files are stored.</summary>
    internal string DirectoryPath => partition.DirectoryPath;

    string? LivePathFor(string sessionId) =>
        SafeSessionId.IsMatch(sessionId) ? Path.Combine(DirectoryPath, $"{EncodeKey(sessionId)}.jsonl") : null;

    /// <summary>
    /// Appends one payload for a later drain pass. Returns whether it was actually persisted, so a
    /// caller cannot report "spooled" after a rejected key or a disk/permission fault.
    /// </summary>
    public bool Append(string sessionId, string route, string rawPayloadJson) {
        var path = LivePathFor(sessionId);
        if (path is null) return false;
        try {
            Directory.CreateDirectory(DirectoryPath);
            var line = new JsonObject { ["route"] = route, ["body"] = rawPayloadJson }.ToJsonString();
            EnsureUnderCap(path, Encoding.UTF8.GetByteCount(line) + 1);
            File.AppendAllText(path, $"{line}\n");
            return true;
        } catch {
            return false;
        }
    }

    // ---- filename key encoding -------------------------------------------------------------
    //
    // Session ids are case-SENSITIVE and are preserved byte-for-byte (OpenCode's are base62 --
    // "ses_619a78374ffe7o0x1iTK74jFRg"), but macOS and Windows filesystems are case-INSENSITIVE, so
    // using the raw id as the filename would let two distinct sessions address one file: their
    // lifecycle entries would interleave and one session's ended marker would discard the other's
    // remainder.
    //
    // So the id is escaped into a single-case filename key and decoded back on the way out. '~' is
    // the escape and cannot occur in an admitted id, which keeps the mapping unambiguous and
    // reversible -- the drain posts the DECODED id as session_id, so a lossy or one-way transform
    // (a hash, or lowercasing) would put a fabricated id on the wire.
    static string EncodeKey(string sessionId) {
        // A dashless GUID is left EXACTLY as-is. Two hex spellings differing only by case are the
        // SAME id, so they neither need disambiguating nor may be renamed: this is the entire
        // population the pre-upgrade grammar admitted, so every spool file already on disk keeps its
        // historical name and stays readable. Escaping applies only to the ids that are new here.
        if (LegacyGuidKey.IsMatch(sessionId)) return sessionId;

        var sb = new StringBuilder(sessionId.Length + 8);

        foreach (var c in sessionId) {
            if (char.IsAsciiLetterUpper(c)) sb.Append('~').Append(char.ToLowerInvariant(c));
            else sb.Append(c);
        }

        return sb.ToString();
    }

    static string? DecodeKey(string key) {
        var sb = new StringBuilder(key.Length);

        for (var i = 0; i < key.Length; i++) {
            if (key[i] != '~') { sb.Append(key[i]); continue; }
            if (++i >= key.Length || !char.IsAsciiLetterLower(key[i])) return null; // malformed
            sb.Append(char.ToUpperInvariant(key[i]));
        }

        return sb.ToString();
    }

    void EnsureUnderCap(string path, int incomingBytes) {
        try {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length + incomingBytes <= capBytes) return;
            // Count UTF-8 BYTES (not chars) so the cap holds for non-ASCII payloads — the file is
            // byte-measured (FileInfo.Length), so a char-based count would under-count and let the
            // file grow past capBytes.
            var lines = File.ReadAllLines(path).ToList();
            while (lines.Count > 0 && lines.Sum(l => Encoding.UTF8.GetByteCount(l) + 1) + incomingBytes > capBytes)
                lines.RemoveAt(0);
            File.WriteAllText(path, lines.Count > 0 ? string.Join('\n', lines) + "\n" : "");
        } catch {
            // Ignore: trimming is best-effort, the next append trims again
        }
    }

    public async Task DrainAllAsync(
            string?                                   currentSessionId,
            Func<string, string, Task<DrainOutcome>>  poster,
            TimeSpan                                  budget,
            CancellationToken                         ct) {
        AdoptLegacyFiles(NewFifoTempName);
        poster = RecordingDeliveries(poster);
        var started = time.GetTimestamp();
        bool Expired() => time.GetElapsedTime(started) >= budget;

        var sessions = SessionIdsWithBacklog().ToList();
        if (currentSessionId is not null && sessions.Remove(currentSessionId)) sessions.Insert(0, currentSessionId);

        foreach (var sid in sessions) {
            if (Expired() || ct.IsCancellationRequested) return;
            if (await DrainSessionAsync(sid, poster, Expired, ct)) return; // transient → stop the pass
        }
    }

    /// <summary>For the ordered drain. Each drain reads only its own temps, so a colliding live file
    /// has to become one of the adopting drain's, which it reads before the live file.</summary>
    internal void AdoptLegacyFiles() => AdoptLegacyFiles(NewOrderedTempName);

    void AdoptLegacyFiles(Func<string, string> temp) =>
        partition.AdoptLegacyFiles(name => name.EndsWith(".jsonl", StringComparison.Ordinal) ? temp(name[..^".jsonl".Length]) : null);

    static string? SessionIdOf(string filePath) {
        var name = Path.GetFileName(filePath);
        var dot  = name.IndexOf('.');
        if (dot <= 0) return null;
        var decoded = DecodeKey(name[..dot]);
        return decoded is not null && SafeSessionId.IsMatch(decoded) ? decoded : null;
    }

    // Recovered temps (oldest first) then the rotated live file. Returns true => stop the whole pass.
    // Delivers only its own ".draining" temps, never the ordered drain's ".ordered-*" ones: those hold a phase
    // the ordered drain withholds on purpose, which this drain would deliver out of order. Only the settling
    // of earlier runs reads both, and it delivers nothing past the latest run's boundary.
    async Task<bool> DrainSessionAsync(
            string sid, Func<string, string, Task<DrainOutcome>> poster, Func<bool> expired, CancellationToken ct) {
        if (!await SettleEarlierRunsAsync(sid, NewFifoTempName, poster, expired, ct)) return true;
        if (IsMarkedEnded(sid)) return false;

        foreach (var temp in Directory.EnumerateFiles(DirectoryPath, $"{EncodeKey(sid)}.*.draining").OrderBy(File.GetCreationTimeUtc)) {
            if (expired() || ct.IsCancellationRequested) return false;
            var (stop, ended) = await DrainFileAsync(temp, poster, expired, ct);
            if (stop) return true;
            if (ended is not null) { MarkEnded(sid, ended); return false; }
        }

        var live = Path.Combine(DirectoryPath, $"{EncodeKey(sid)}.jsonl");
        if (!File.Exists(live) || expired() || ct.IsCancellationRequested) return false;

        var rotated = Path.Combine(DirectoryPath, NewFifoTempName(EncodeKey(sid)));
        try { File.Move(live, rotated); }
        catch { return false; } // lost the atomic-rename race (or vanished) — the winner handles it
        var (stopped, end) = await DrainFileAsync(rotated, poster, expired, ct);
        if (end is not null) MarkEnded(sid, end);
        return stopped;
    }

    // Drain a private temp. Delivered/Drop advance; TransientStop or budget stops and keeps the remainder. An end
    // that is resolved stops the file too, so what follows it is judged against the marker on the next pass.
    static async Task<(bool StopPass, string? Ended)> DrainFileAsync(
            string path, Func<string, string, Task<DrainOutcome>> poster, Func<bool> expired, CancellationToken ct) {
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(path, ct); }
        catch { return (false, null); }

        string? ended = null;

        var i = 0;
        for (; i < lines.Length; i++) {
            if (expired() || ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            string? route, body;
            try {
                var node = JsonNode.Parse(lines[i]);
                route = node?["route"]?.GetValue<string>();
                body  = node?["body"]?.GetValue<string>();
            } catch { route = body = null; }
            if (route is null || body is null) continue; // skip old-format / malformed

            DrainOutcome outcome;
            try { outcome = await poster(route, body); }
            catch { outcome = DrainOutcome.TransientStop; }

            if (outcome == DrainOutcome.TransientStop) break;
            if (IsTerminalRoute(route)) { ended = body; i++; break; }
        }

        if (i >= lines.Length) {
            try {
                File.Delete(path);
            } catch {
                // Ignore: a leftover temp drains again on the next pass
            }
            return (false, ended);
        }
        try {
            await File.WriteAllLinesAsync(path, lines.Skip(i), ct);
        } catch {
            // Ignore: the temp still holds the remainder
        }
        return (ended is null, ended);
    }

    /// <summary>True if this session still has undelivered spool entries: a live .jsonl, a
    /// route-agnostic-drain (Claude/Cursor) ".draining" temp, or an ordered-drain ".ordered-*" temp.
    /// Callers use this as an ordering guard (e.g. "don't post my fresh session-end directly — a
    /// withheld ordered-drain phase is still in flight for this session") so both temp namespaces
    /// must be visible here even though only the ordered drain ever creates/consumes the latter.</summary>
    public bool HasBacklog(string sessionId) {
        if (!SafeSessionId.IsMatch(sessionId)) return false;

        var key            = EncodeKey(sessionId);
        var hasLiveFile    = partition.AnyFileMatches($"{key}.jsonl");
        var hasFifoTemp    = partition.AnyFileMatches($"{key}.*.draining");
        var hasOrderedTemp = partition.AnyFileMatches($"{key}.ordered-*");

        return hasLiveFile || hasFifoTemp || hasOrderedTemp;
    }

    /// <summary>Every distinct session id with a live .jsonl or recovered .draining/.ordered-* temp,
    /// in no particular order (callers that need current-session-first ordering add it themselves).</summary>
    public IEnumerable<string> SessionIdsWithBacklog() {
        if (!Directory.Exists(DirectoryPath)) return [];
        return [.. Directory.EnumerateFiles(DirectoryPath).Select(SessionIdOf).OfType<string>().Distinct()];
    }

    /// <summary>
    /// Route-filtered drain for a single session: only entries whose route is a "session-end"
    /// (terminal, when <paramref name="isTerminal"/> is <c>true</c>) or anything else (non-terminal,
    /// when <c>false</c>) are posted. Arrival order within the session file guarantees start-before-end,
    /// so as soon as an entry of the OTHER phase is reached, draining stops and the remainder — including
    /// that entry — is preserved for the caller's next phase. Used by <see cref="LifecycleSpoolDrain"/>
    /// to enforce cross-spool ordering (lifecycle start &#8594; transcript tail &#8594; lifecycle end).
    ///
    /// <para><b>Distinct temp namespace.</b> Rotates the live file to a
    /// <c>{sid}.ordered-{pid}-{seq}</c> temp — NOT the <c>{sid}.{pid}-{seq}.draining</c> shape
    /// <see cref="DrainAllAsync"/> (route-agnostic FIFO, still used by Claude/Cursor) uses — so a
    /// deliberately-withheld phase can never be recovered and redelivered out of order by the
    /// unrelated FIFO drain, and vice versa.</para>
    ///
    /// <para>Returns the number of entries actually resolved (delivered or permanently dropped) in
    /// THIS call — never those left for a retry by a <c>TransientStop</c> or the budget. The caller
    /// uses a non-zero terminal-phase count to durably mark the session ended (see
    /// <see cref="MarkEnded"/>), which is how <see cref="LifecycleSpoolDrain"/> prevents a later
    /// straggler non-terminal entry from ever being delivered after session-end (BLOCKER-3).</para>
    /// </summary>
    public async Task<int> DrainRoutesAsync(
            string sid, bool isTerminal, Func<string, string, Task<DrainOutcome>> poster,
            Func<bool> expired, CancellationToken ct) {
        if (!SafeSessionId.IsMatch(sid) || !Directory.Exists(DirectoryPath)) return 0;
        poster = RecordingDeliveries(poster);

        var consumed = 0;

        foreach (var temp in Directory.EnumerateFiles(DirectoryPath, $"{EncodeKey(sid)}.ordered-*").OrderBy(File.GetCreationTimeUtc)) {
            if (expired() || ct.IsCancellationRequested) return consumed;
            var (stopped, n) = await DrainFileRoutesAsync(temp, isTerminal, poster, expired, ct);
            consumed += n;
            if (stopped) return consumed; // stopped — remainder kept
        }

        var live = Path.Combine(DirectoryPath, $"{EncodeKey(sid)}.jsonl");
        if (!File.Exists(live) || expired() || ct.IsCancellationRequested) return consumed;

        var rotated = Path.Combine(DirectoryPath, NewOrderedTempName(EncodeKey(sid)));
        try { File.Move(live, rotated); }
        catch { return consumed; } // lost the atomic-rename race (or vanished) — the winner handles it
        var (_, more) = await DrainFileRoutesAsync(rotated, isTerminal, poster, expired, ct);
        return consumed + more;
    }

    static string RouteHead(string route) {
        var slash = route.IndexOf('/');
        return slash >= 0 ? route[..slash] : route;
    }

    /// <summary>
    /// Settles what a session's earlier runs left ahead of its latest run, before either drain delivers the
    /// rest. The latest run begins at its spooled start, or at the note a start posted live leaves. With
    /// neither, a marked session's later run begins at the first hook naming a run other than the one whose
    /// end was delivered, or else at its latest end, as that earlier end was already delivered. Ahead of the
    /// boundary an end is dropped, as the run it closed has started again, and so is a hook of the run
    /// whose end was delivered; the rest is posted in order. False when the pass stopped first.
    /// </summary>
    /// <remarks>Reads both drains' temps, so neither delivers an earlier run's end the other is holding.
    /// Appenders share the live file with no lock, so it is only ever renamed into a temp, never rewritten.</remarks>
    public Task<bool> SettleEarlierRunsAsync(
            string sid, Func<string, string, Task<DrainOutcome>> poster, Func<bool> expired, CancellationToken ct) =>
        SettleEarlierRunsAsync(sid, NewOrderedTempName, poster, expired, ct);

    async Task<bool> SettleEarlierRunsAsync(
            string sid, Func<string, string> tempName, Func<string, string, Task<DrainOutcome>> poster, Func<bool> expired, CancellationToken ct) {
        if (!SafeSessionId.IsMatch(sid) || !Directory.Exists(DirectoryPath)) return true;

        var key  = EncodeKey(sid);
        var live = Path.Combine(DirectoryPath, $"{key}.jsonl");
        try {
            if (File.Exists(live)) File.Move(live, Path.Combine(DirectoryPath, tempName(key)));
        } catch {
            // Lost the rename to another drain, which settles what it took
        }

        var temps = Directory.EnumerateFiles(DirectoryPath, $"{key}.ordered-*")
            .Concat(Directory.EnumerateFiles(DirectoryPath, $"{key}.*.draining"))
            .OrderBy(File.GetCreationTimeUtc);

        var files = new List<(string Path, string[] Lines, List<(int Line, string Route, string Body)> Entries)>();
        foreach (var temp in temps) {
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(temp, ct); }
            catch { continue; }
            files.Add((temp, lines, [.. lines.Select((l, i) => (i, Entry(l))).Where(e => e.Item2 is not null).Select(e => (e.i, e.Item2!.Value.Route, e.Item2!.Value.Body))]));
        }

        var all    = files.SelectMany(f => f.Entries).ToList();
        var marked = IsMarkedEnded(sid);
        var ended  = marked ? EndedRun(sid) : default;
        bool Straggler(string body) => marked && !NamesAnotherRun(body, ended);

        var boundary = all.FindLastIndex(e => RouteHead(e.Route) is "session-start" or RunStartedRoute);
        if (boundary < 0 && marked) {
            boundary = all.FindIndex(e => !Straggler(e.Body));
            if (boundary < 0) boundary = all.FindLastIndex(e => IsTerminalRoute(e.Route));
        }
        if (boundary < 0 && !marked) return true;

        // A note is the boundary's position only: nothing is posted for it.
        var settle = boundary < 0 ? all.Count : all[boundary].Route == RunStartedRoute ? boundary + 1 : boundary;
        poster = RecordingDeliveries(poster);

        var index = 0;
        foreach (var (path, lines, entries) in files) {
            if (index >= settle) break;

            var keepFrom = -1;
            foreach (var (line, route, body) in entries) {
                if (index >= settle) { keepFrom = line; break; }

                var outcome = DrainOutcome.Drop;
                if (!IsTerminalRoute(route) && route != RunStartedRoute && !Straggler(body)) {
                    if (expired() || ct.IsCancellationRequested) outcome = DrainOutcome.TransientStop;
                    else {
                        try { outcome = await poster(route, body); }
                        catch { outcome = DrainOutcome.TransientStop; }
                    }
                }

                if (outcome == DrainOutcome.TransientStop) {
                    await KeepFromAsync(path, lines, line, ct);
                    return false;
                }

                index++;
            }

            if (keepFrom < 0) {
                try { File.Delete(path); } catch { /* a leftover temp settles again on the next pass */ }
            } else {
                await KeepFromAsync(path, lines, keepFrom, ct);
            }
        }

        if (boundary >= 0 && marked) ClearEnded(sid);
        return true;
    }

    /// <summary>The route of the note <see cref="NoteRunStarted"/> spools. It is never posted.</summary>
    const string RunStartedRoute = "run-started";

    /// <summary>
    /// Records, for a session whose spooled end was delivered, that a start was just posted live: what is
    /// spooled after it is the new run's, not a straggler of the ended one. A no-op otherwise, so it leaves
    /// no backlog behind an ordinary start.
    /// </summary>
    public void NoteRunStarted(string sessionId) {
        if (IsMarkedEnded(sessionId)) Append(sessionId, RunStartedRoute, "{}");
    }

    static (string? AgentId, string? Epoch) RunOf(string body) {
        try {
            var node = JsonNode.Parse(body);
            return (node?["agent_host_id"]?.GetValue<string>(), node?["daemon_epoch"]?.GetValue<string>());
        } catch {
            return default;
        }
    }

    // Only a field both name can tell two runs apart, so a hook that names no run is the ended run's.
    static bool NamesAnotherRun(string body, (string? AgentId, string? Epoch) ended) {
        var run = RunOf(body);
        return (run.AgentId is not null && ended.AgentId is not null && run.AgentId != ended.AgentId)
            || (run.Epoch is not null && ended.Epoch is not null && run.Epoch != ended.Epoch);
    }

    static async Task KeepFromAsync(string path, string[] lines, int from, CancellationToken ct) {
        try {
            await File.WriteAllLinesAsync(path, lines.Skip(from), ct);
        } catch {
            // Ignore: the temp still holds what was not settled
        }
    }

    static (string Route, string Body)? Entry(string line) {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try {
            var node = JsonNode.Parse(line);
            return node?["route"]?.GetValue<string>() is { } route && node["body"]?.GetValue<string>() is { } body ? (route, body) : null;
        } catch {
            return null;
        }
    }

    internal static bool IsTerminalRoute(string route) => RouteHead(route) == "session-end";

    // Drain a private temp, posting only entries matching isTerminal. Delivered/Drop advance and
    // count toward the returned "consumed" total; TransientStop, budget, or a phase mismatch stops
    // (Stopped=true) and keeps the remainder for a later pass.
    static async Task<(bool Stopped, int Consumed)> DrainFileRoutesAsync(
            string path, bool isTerminal, Func<string, string, Task<DrainOutcome>> poster,
            Func<bool> expired, CancellationToken ct) {
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(path, ct); }
        catch { return (false, 0); }

        var i = 0;
        var consumed = 0;
        for (; i < lines.Length; i++) {
            if (expired() || ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            string? route, body;
            try {
                var node = JsonNode.Parse(lines[i]);
                route = node?["route"]?.GetValue<string>();
                body  = node?["body"]?.GetValue<string>();
            } catch { route = body = null; }
            if (route is null || body is null) continue; // skip old-format / malformed

            if (IsTerminalRoute(route) != isTerminal) break; // other phase — stop, preserve order

            DrainOutcome outcome;
            try { outcome = await poster(route, body); }
            catch { outcome = DrainOutcome.TransientStop; }

            if (outcome == DrainOutcome.TransientStop) break;
            consumed++;
        }

        if (i >= lines.Length) {
            try {
                File.Delete(path);
            } catch {
                // Ignore: a leftover temp drains again on the next pass
            }
            return (false, consumed);
        }
        try {
            await File.WriteAllLinesAsync(path, lines.Skip(i), ct);
        } catch {
            // Ignore: the temp still holds the remainder
        }
        return (true, consumed);
    }

    /// <summary>
    /// True if a terminal (session-end) entry for this session has already been durably delivered
    /// (or permanently dropped) by a previous ordered-drain pass — see <see cref="MarkEnded"/>.
    /// Task 12 / BLOCKER-3: once true, <see cref="LifecycleSpoolDrain"/> must never attempt
    /// to deliver a straggler non-terminal entry for this session, even one that arrives (or is
    /// discovered) in a later pass — that would be a real cross-pass ordering violation, not just a
    /// same-pass one (the same-pass case is already prevented by <see cref="DrainRoutesAsync"/>'s
    /// phase-mismatch break).
    /// </summary>
    public bool IsMarkedEnded(string sessionId) =>
        SafeSessionId.IsMatch(sessionId) && File.Exists(EndedMarkerPath(sessionId));

    /// <summary>Durably marks a session as ended (see <see cref="IsMarkedEnded"/>). The marker's
    /// filename is dot-prefixed so it is invisible to <see cref="SessionIdOf"/> / the session-id-keyed
    /// enumerations (matching the existing <c>.last-drain</c> throttle-stamp convention) — it is swept
    /// only by <see cref="DeleteExpired"/>'s unconditional file-age sweep.</summary>
    /// <param name="endBody">The end that was delivered: the marker keeps the run it names, so a later run's
    /// hooks are not taken for that run's stragglers.</param>
    public void MarkEnded(string sessionId, string? endBody = null) {
        if (!SafeSessionId.IsMatch(sessionId)) return;
        try {
            Directory.CreateDirectory(DirectoryPath);
            var (agentId, epoch) = endBody is null ? default : RunOf(endBody);
            File.WriteAllText(EndedMarkerPath(sessionId),
                new JsonObject { ["agent_host_id"] = agentId, ["daemon_epoch"] = epoch }.ToJsonString());
        } catch {
            // Ignore: unmarked, a straggler can still post after the end
        }
    }

    // An empty marker, as an older kcap wrote it, names no run.
    (string? AgentId, string? Epoch) EndedRun(string sessionId) {
        try {
            return RunOf(File.ReadAllText(EndedMarkerPath(sessionId)));
        } catch {
            return default;
        }
    }

    /// <summary>Lifts <see cref="MarkEnded"/> for a session known to have run again, so its next end is
    /// delivered rather than discarded as a straggler.</summary>
    void ClearEnded(string sessionId) {
        if (!SafeSessionId.IsMatch(sessionId)) return;
        try {
            File.Delete(EndedMarkerPath(sessionId));
        } catch {
            // Ignore: still marked, the next end is discarded as a straggler
        }
    }

    string EndedMarkerPath(string sessionId) => Path.Combine(DirectoryPath, $".ended-{EncodeKey(sessionId)}");

    public const string PolicySnapshotRoute = "policy-snapshot";

    string? PolicySnapshotMarkerPath(string sessionId, string snapshotId) =>
        SafeSessionId.IsMatch(sessionId)
            ? Path.Combine(DirectoryPath, $".policy-snapshot-{EncodeKey(sessionId)}-{snapshotId[..Math.Min(16, snapshotId.Length)]}")
            : null;

    /// <summary>Whether a drain has had this session's policy snapshot accepted by the server.
    /// Every drain path records it, whichever vendor's hook ran the drain.</summary>
    public bool IsPolicySnapshotDelivered(string sessionId, string snapshotId) =>
        PolicySnapshotMarkerPath(sessionId, snapshotId) is { } path && File.Exists(path);

    Func<string, string, Task<DrainOutcome>> RecordingDeliveries(Func<string, string, Task<DrainOutcome>> poster) =>
        async (route, body) => {
            var outcome = await poster(route, body);
            if (outcome == DrainOutcome.Delivered && route == PolicySnapshotRoute) RecordPolicySnapshotDelivered(body);
            return outcome;
        };

    void RecordPolicySnapshotDelivered(string body) {
        try {
            var node = JsonNode.Parse(body);
            if (node?["session_id"]?.GetValue<string>() is not { Length: > 0 } sid
             || node["snapshot_id"]?.GetValue<string>() is not { Length: > 0 } snap
             || PolicySnapshotMarkerPath(sid, snap) is not { } path)
                return;
            File.WriteAllText(path, "");
        } catch {
            // Ignore: unrecorded, the next decision uploads the snapshot inline again
        }
    }

    public void DeleteExpired() => partition.DeleteExpired(time);

    // Filename-safe superset of the old dashless-GUID form. The filename IS the session id --
    // LifecycleSpoolDrain posts it verbatim as session_id for session-needs-import -- so the key may
    // be widened but never transformed (hashing would fabricate an id on the wire). Excludes '.', '/'
    // and '\\', preserving both the path-traversal property and the parse-before-first-dot split.
    // Vendors such as OpenCode use ids like "ses_7f3a9c21b8", which the old form silently dropped.
    /// <summary>The pre-upgrade key space: a dashless GUID, case-insensitively one id.</summary>
    static string NewFifoTempName(string key)    => $"{key}.{Environment.ProcessId}-{Interlocked.Increment(ref seqCounter)}.draining";
    static string NewOrderedTempName(string key) => $"{key}.ordered-{Environment.ProcessId}-{Interlocked.Increment(ref seqCounter)}";

    [GeneratedRegex("^[0-9a-fA-F]{32}$", RegexOptions.Compiled)]
    private static partial Regex LegacyGuidKeyRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled)]
    private static partial Regex SafeSessionIdRegex();
}
