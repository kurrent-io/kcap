using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Models.Transcripts.Harness.OpenCode;
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Harness.OpenCode;

/// <summary>
/// One OpenCode session row (subset used by import). <c>TimeCreated</c>/<c>TimeUpdated</c>
/// are <c>null</c> when the column is NULL or 0 (both observed in the wild) — "unknown",
/// never epoch.
/// </summary>
internal sealed record OpenCodeSessionRow(
    string  Id,
    string? ParentId,
    string? Directory,
    string  Title,
    long?   TimeCreated,
    long?   TimeUpdated);

/// <summary>
/// Read-only reader over OpenCode's SQLite db (<c>~/.local/share/opencode/opencode.db</c>).
/// Lives in the CLI project (not Core) so the Microsoft.Data.Sqlite native bundle
/// never reaches the AOT-published daemon. Opened read-only, WAL-tolerant.
///
/// <para>Reconstructs the live plugin's lines from both schemas one db can hold. A v1 message
/// (<c>session</c>, <c>message</c>, <c>part</c>) is its <c>{info,parts}</c> line, with each row's key
/// columns merged back onto its <c>data</c> JSON. A v2 message (<c>session_v2</c>,
/// <c>session_message</c>, <c>session_part</c>) is the <c>{id, type, ...}</c> message v2's context
/// returns. v2 copies every v1 session into its own tables and leaves the v1 rows in place, and the
/// copy's parts lose the ids v1 events are keyed by, so a message the v1 tables hold is read from them
/// and only the others from v2.</para>
/// </summary>
internal sealed class OpenCodeDb : IDisposable {
    readonly SqliteConnection _conn;

    // Install the on-demand native-SQLite resolver before SqliteConnection's static ctor
    // runs its first e_sqlite3 P/Invoke. A static ctor on this type is guaranteed to run
    // before the instance ctor body references SqliteConnection below.
    static OpenCodeDb() => SqliteNativeResolver.Register();

    readonly bool _v1;
    readonly bool _v2;
    readonly bool _v2Parts;

    public OpenCodeDb(string dbPath) {
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = dbPath,
            Mode       = SqliteOpenMode.ReadOnly,
            Cache      = SqliteCacheMode.Private,
        }.ToString());
        _conn.Open();

        var tables = Tables();
        _v1      = tables.Contains("session") && tables.Contains("message") && tables.Contains("part");
        _v2      = tables.Contains("session_v2") && tables.Contains("session_message");
        _v2Parts = _v2 && tables.Contains("session_part");
    }

    HashSet<string> Tables() {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        using var reader = cmd.ExecuteReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    const string SessionColumns = "id, parent_id, directory, title, time_created, time_updated";

    /// <summary>Where sessions are read from: a session v2 holds is its v2 row, which a copied v1 session keeps
    /// current; a db with neither schema reads the v1 table, so it fails as a schema-drifted db does.</summary>
    string SessionSource => (_v1, _v2) switch {
        (true, true) => $"(SELECT {SessionColumns} FROM session_v2 UNION ALL " +
                        $"SELECT {SessionColumns} FROM session WHERE id NOT IN (SELECT id FROM session_v2))",
        (false, true) => "session_v2",
        _             => "session",
    };

    public IReadOnlyList<OpenCodeSessionRow> QueryRoots() =>
        QuerySessions("(parent_id IS NULL OR parent_id = '')", parent: null).Rows;

    public IReadOnlyList<OpenCodeSessionRow> QueryChildren(string parentId) {
        QueryChildrenCallCount++;
        var (rows, _) = QuerySessions("parent_id = $parent", parentId);
        TrackRowsReturned(rows.Count);
        return rows;
    }

    /// <summary>
    /// Cap-aware variant of <see cref="QueryChildren"/> for a parent whose children are
    /// NECESSARILY beyond <see cref="MaxDescendantDepth"/> — avoids materializing the whole
    /// child set before truncation could be detected. Adds a SQL <c>LIMIT $limit</c> so at most
    /// <paramref name="limit"/> rows are ever read/allocated for a single call, instead of the
    /// full (potentially unbounded) result <see cref="QueryChildren"/> would return. Callers
    /// pass (remaining counting capacity + 1 sentinel) so <see cref="DescendantDiscoveryResult.CountTruncated"/>
    /// can still be set correctly the moment the fetched batch itself proves there are more than
    /// the remaining capacity allows. NEVER used for an in-cap parent — those keep the unbounded
    /// <see cref="QueryChildren"/> (see <see cref="QueryDescendants"/>), preserving the
    /// always-complete in-cap discovery contract.
    ///
    /// <para>The returned <c>HitLimit</c> reflects the number of RAW rows the SQL <c>LIMIT</c>
    /// actually returned — BEFORE <see cref="QuerySessions"/>'s per-row try/catch skips a
    /// malformed row — not the count of rows that survived mapping. A malformed or
    /// already-visited row can otherwise silently consume the sole sentinel slot, masking a
    /// genuinely-omitted valid descendant just beyond it: the caller must treat <c>HitLimit</c>,
    /// not the returned row count, as the signal that more rows may exist beyond this fetch (see
    /// <see cref="QueryDescendants"/>).</para>
    /// </summary>
    internal (IReadOnlyList<OpenCodeSessionRow> Rows, bool HitLimit) QueryChildrenBounded(string parentId, int limit) {
        QueryChildrenCallCount++;
        var (rows, rawCount) = QuerySessions("parent_id = $parent", parentId, limit);
        TrackRowsReturned(rows.Count);
        return (rows, rawCount >= limit);
    }

    /// <summary>
    /// Total <see cref="QueryChildren"/>/<see cref="QueryChildrenBounded"/> invocations made
    /// through this instance — internal-only instrumentation letting a regression test prove
    /// the below-cap counting walk in <see cref="QueryDescendants"/> stops issuing further DB
    /// queries the instant <see cref="DescendantDiscoveryResult.CountTruncated"/> is established,
    /// instead of draining every already-enqueued below-cap node to completion.
    /// </summary>
    internal int QueryChildrenCallCount { get; private set; }

    /// <summary>
    /// The largest number of rows any single <see cref="QueryChildren"/>/<see cref="QueryChildrenBounded"/>
    /// call has returned so far — internal-only instrumentation letting a regression test prove
    /// a single below-cap parent's child fetch never materializes more than
    /// ~<see cref="MaxCountingNodes"/> rows, even when that parent's true child count is far
    /// larger.
    /// </summary>
    internal int QueryChildrenMaxRowsReturned { get; private set; }

    void TrackRowsReturned(int count) {
        if (count > QueryChildrenMaxRowsReturned) QueryChildrenMaxRowsReturned = count;
    }

    /// <summary>Import-side recursion depth cap — a descendant beyond this depth
    /// is neither imported nor promoted; the caller surfaces the count.</summary>
    public const int MaxDescendantDepth = 8;

    /// <summary>
    /// Hard ceiling on the total number of BELOW-CAP (omitted) nodes visited by the counting
    /// walk. This bounds ONLY the walk into descendants already beyond
    /// <see cref="MaxDescendantDepth"/> — it never applies to in-cap import discovery, which is
    /// always complete (see <see cref="QueryDescendants"/>). The visited-id set already prevents
    /// an infinite loop on a reachable cycle; this ceiling instead bounds the total work a
    /// pathologically huge or wide UNIMPORTED subtree could otherwise force onto a single
    /// classify/import pass. A real OpenCode session graph tops out at a few dozen nodes, so
    /// this is a generous safety valve, not a realistic limit — if it's ever hit,
    /// <see cref="DescendantDiscoveryResult.CountTruncated"/> is set so the omitted count/ids are
    /// treated as a LOWER BOUND rather than silently corrupting anything.
    /// </summary>
    public const int MaxCountingNodes = 10_000;

    /// <summary>One recursively-discovered descendant row at its depth below the root (a direct
    /// child is depth 1, a grandchild depth 2, etc.).</summary>
    public readonly record struct DescendantRow(OpenCodeSessionRow Row, int Depth);

    /// <summary>
    /// Recursive descendant discovery result. <see cref="Descendants"/> holds EVERY descendant
    /// WITHIN <see cref="MaxDescendantDepth"/> — the import set (BFS, deterministic, each level
    /// ordered like <see cref="QueryChildren"/>) — and is NEVER truncated by
    /// <see cref="MaxCountingNodes"/>: in-cap discovery is unbounded.
    /// <see cref="DescendantsOmitted"/> is the count of every descendant BELOW the cap, not
    /// merely the boundary children immediately past it — a chain through depths 9 and 10
    /// reports 2, not 1 — UNLESS <see cref="CountTruncated"/> is true, in which case it (and
    /// <see cref="OmittedDescendantIds"/>) is a LOWER BOUND: the below-cap counting walk hit
    /// <see cref="MaxCountingNodes"/> before it could finish, so the true omitted count/identity
    /// set may be larger. <see cref="OmittedDescendantIds"/> carries the (possibly partial)
    /// omitted ids (sorted, order-independent) so a caller can fold them into a completeness
    /// fingerprint: a newly-reachable capped descendant then changes the fingerprint even though
    /// its content is never imported. Never silent — the caller surfaces the count (and any
    /// truncation) in the import summary, and MUST treat <see cref="CountTruncated"/> as blocking
    /// a fingerprint-based completeness/AlreadyLoaded decision.
    /// </summary>
    public readonly record struct DescendantDiscoveryResult(
        IReadOnlyList<DescendantRow> Descendants,
        int                          DescendantsOmitted,
        IReadOnlyList<string>        OmittedDescendantIds,
        bool                         CountTruncated);

    /// <summary>
    /// Walks <c>parent_id</c> edges from <paramref name="rootId"/>, guarding cycles with a visited set.
    /// Descendants within <see cref="MaxDescendantDepth"/> are always found completely. Those below it are
    /// never imported but are still walked to count and identify the omitted subtree; that walk alone is
    /// bounded by <see cref="MaxCountingNodes"/>, and hitting it sets
    /// <see cref="DescendantDiscoveryResult.CountTruncated"/>, making the omitted figures a lower bound.
    /// <para>The two walks use separate frontiers, so the below-cap one can be dropped the moment it
    /// truncates. A node at exactly the cap has only below-cap children, so it is the one call that goes
    /// through <see cref="QueryChildrenBounded"/>.</para>
    /// <para>Malformed and duplicate rows are dropped after the fetch and could use up the sentinel row, so
    /// a raw <c>LIMIT</c> hit (<c>HitLimit</c>) asserts truncation regardless of how many rows were valid.</para>
    /// </summary>
    public DescendantDiscoveryResult QueryDescendants(string rootId) {
        var result         = new List<DescendantRow>();
        var visited        = new HashSet<string>(StringComparer.Ordinal) { rootId };
        var omittedIds     = new List<string>();
        var countTruncated = false;

        var inCapFrontier    = new Queue<(string Id, int Depth)>();
        var belowCapFrontier = new Queue<string>();
        inCapFrontier.Enqueue((rootId, 0));

        // In-cap walk: always run to completion, regardless of below-cap truncation state —
        // this is the unbounded, always-complete import set.
        while (inCapFrontier.Count > 0) {
            var (id, depth) = inCapFrontier.Dequeue();

            if (depth < MaxDescendantDepth) {
                // Every child here is necessarily in-cap (childDepth <= MaxDescendantDepth) —
                // always fully discovered, never bounded by MaxCountingNodes. Unbounded
                // QueryChildren is safe/required here.
                foreach (var child in QueryChildren(id)) {
                    var childDepth = depth + 1;
                    if (!visited.Add(child.Id)) continue; // cycle guard — already reached
                    result.Add(new DescendantRow(child, childDepth));
                    inCapFrontier.Enqueue((child.Id, childDepth));
                }
                continue;
            }

            // depth == MaxDescendantDepth: every child is necessarily BELOW the cap
            // (childDepth = MaxDescendantDepth + 1). Cap-aware fetch instead of materializing
            // the whole child row set for this parent.
            if (countTruncated) continue; // already known to be a lower bound — skip the query entirely

            var remaining = MaxCountingNodes - omittedIds.Count;
            var (belowChildren, hitLimit) = QueryChildrenBounded(id, remaining + 1);
            foreach (var child in belowChildren) {
                if (!visited.Add(child.Id)) continue; // cycle guard — already reached

                if (omittedIds.Count >= MaxCountingNodes) {
                    countTruncated = true;
                    break; // stop scanning this parent's remaining fetched rows too
                }
                omittedIds.Add(child.Id);
                belowCapFrontier.Enqueue(child.Id);
            }
            // A malformed/already-visited row can consume the sole sentinel slot before it's
            // counted as an omitted descendant, so the valid-count threshold above can
            // under-report. HitLimit — the raw SQL LIMIT was actually exhausted — is the only
            // proof we lack; when it's true we cannot rule out further valid descendants beyond
            // this fetch, so treat it as truncation too (a safe over-report in the rare
            // exact-boundary+junk case; a raw count strictly below the limit is definite EOF and
            // needs no such fallback).
            if (hitLimit) countTruncated = true;
        }

        // Below-cap counting/signature walk — every node here is already below-cap, so every
        // one of its children is too; bounded by MaxCountingNodes via QueryChildrenBounded and
        // stopped IMMEDIATELY once truncated: any already-queued below-cap tail (up to
        // MaxCountingNodes entries) is abandoned rather than drained node-by-node, so a
        // pathologically large omitted subtree can't force thousands of further (or unbounded)
        // DB queries once the count is already known to be a lower bound.
        while (belowCapFrontier.Count > 0 && !countTruncated) {
            var id = belowCapFrontier.Dequeue();

            var remaining = MaxCountingNodes - omittedIds.Count;
            var (children, hitLimit) = QueryChildrenBounded(id, remaining + 1);
            foreach (var child in children) {
                if (!visited.Add(child.Id)) continue; // cycle guard — already reached

                if (omittedIds.Count >= MaxCountingNodes) {
                    countTruncated = true;
                    break; // stop scanning this node's remaining fetched rows too
                }
                omittedIds.Add(child.Id);
                belowCapFrontier.Enqueue(child.Id);
            }
            if (hitLimit) countTruncated = true; // see above
        }

        omittedIds.Sort(StringComparer.Ordinal);
        return new DescendantDiscoveryResult(result, omittedIds.Count, omittedIds, countTruncated);
    }

    /// <summary>
    /// <c>RawCount</c> is every row the reader actually yielded —
    /// INCLUDING one skipped below for being malformed — never just <c>Rows.Count</c>. Only
    /// <c>RawCount</c> can prove whether a <c>LIMIT</c> was truly exhausted: a malformed row
    /// still consumes one of the SQL engine's <c>LIMIT</c> slots even though it never reaches
    /// <c>Rows</c>, so a caller checking <c>Rows.Count == limit</c> instead could be fooled into
    /// thinking fewer raw rows existed than actually did.
    /// </summary>
    (IReadOnlyList<OpenCodeSessionRow> Rows, int RawCount) QuerySessions(string whereClause, string? parent, int? limit = null) {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            $"SELECT {SessionColumns} " +
            $"FROM {SessionSource} WHERE {whereClause} ORDER BY time_created, id" +
            (limit is not null ? " LIMIT $limit" : "");
        if (parent is not null) cmd.Parameters.AddWithValue("$parent", parent);
        if (limit is not null) cmd.Parameters.AddWithValue("$limit", limit.Value);

        var rows     = new List<OpenCodeSessionRow>();
        var rawCount = 0;
        using var r = cmd.ExecuteReader();
        while (r.Read()) {
            rawCount++;
            OpenCodeSessionRow row;
            try {
                row = new OpenCodeSessionRow(
                    Id:          r.GetString(0),
                    ParentId:    r.IsDBNull(1) ? null : r.GetString(1),
                    Directory:   r.IsDBNull(2) ? null : r.GetString(2),
                    Title:       r.IsDBNull(3) ? "" : r.GetString(3),
                    TimeCreated: r.IsDBNull(4) ? null : NonZero(r.GetInt64(4)),
                    TimeUpdated: r.IsDBNull(5) ? null : NonZero(r.GetInt64(5)));
            } catch {
                continue; // skip a malformed / schema-drifted row rather than abort the scan
            }
            rows.Add(row);
        }
        return (rows, rawCount);
    }

    // A 0 epoch is observed alongside NULL for an unset time_created/time_updated
    // column — both mean "unknown", not the 1970 epoch.
    static long? NonZero(long v) => v == 0 ? null : v;

    /// <summary>
    /// Streams one reconstructed line per message: the session's v1 messages, then the v2 messages the v1 tables do
    /// not hold. Bounded memory: holds only the current message's parts at a time.
    /// </summary>
    public IEnumerable<string> SynthesizeLines(string sessionId) {
        if (_v1)
            foreach (var line in V1Lines(sessionId)) yield return line;
        if (_v2)
            foreach (var line in V2Lines(sessionId)) yield return line;
    }

    /// <summary>
    /// One <c>{info,parts}</c> line per v1 message, in message chronological order, parts in (time_created,id) order.
    /// A LEFT JOIN keeps messages that have no parts.
    /// </summary>
    IEnumerable<string> V1Lines(string sessionId) {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT m.id, m.data, p.id, p.message_id, p.session_id, p.data " +
            "FROM message m LEFT JOIN part p ON p.message_id = m.id " +
            "WHERE m.session_id = $s " +
            "ORDER BY m.time_created, m.id, p.time_created, p.id";
        cmd.Parameters.AddWithValue("$s", sessionId);

        using var r = cmd.ExecuteReader();

        string?    curMsgId   = null;
        string     curMsgData = "{}";
        JsonArray? parts      = null;

        while (r.Read()) {
            var msgId = r.GetString(0);
            if (msgId != curMsgId) {
                if (curMsgId is not null) yield return BuildLine(curMsgId, sessionId, curMsgData, parts!);
                curMsgId   = msgId;
                curMsgData = r.GetString(1);
                parts      = new JsonArray();
            }
            // p.* columns are null for a message with no parts (LEFT JOIN).
            if (!r.IsDBNull(2)) {
                var partNode = JsonNode.Parse(r.GetString(5))!.AsObject();
                partNode["id"]        = r.GetString(2);
                partNode["messageID"] = r.GetString(3);
                partNode["sessionID"] = r.GetString(4);
                // Cast to JsonNode so the non-generic Add(JsonNode?) overload is chosen —
                // the generic Add<T>(T) trips IL2026/IL3050 under AOT (see AGENTS.md).
                parts!.Add((JsonNode)partNode);
            }
        }
        if (curMsgId is not null) yield return BuildLine(curMsgId, sessionId, curMsgData, parts!);
    }

    /// <summary>
    /// One line per v2 user, assistant or shell message, in sequence order: its <c>data</c> with its id and type, and an
    /// assistant's content as v2 reads it back, its <c>session_part</c> rows in ordinal order and then any content the
    /// row still holds inline. The other message types carry no conversation.
    /// </summary>
    IEnumerable<string> V2Lines(string sessionId) {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT m.id, m.type, m.data, " + (_v2Parts ? "p.data " : "NULL ") +
            "FROM session_message m " +
            (_v2Parts ? "LEFT JOIN session_part p ON p.message_id = m.id " : "") +
            "WHERE m.session_id = $s AND m.type IN ('user', 'assistant', 'shell') " +
            (_v1 ? "AND m.id NOT IN (SELECT id FROM message) " : "") +
            "ORDER BY m.seq" + (_v2Parts ? ", p.ordinal" : "");
        cmd.Parameters.AddWithValue("$s", sessionId);

        using var r = cmd.ExecuteReader();

        string?    curId   = null;
        string     curType = "";
        string     curData = "{}";
        JsonArray? parts   = null;

        while (r.Read()) {
            var id = r.GetString(0);
            if (id != curId) {
                if (curId is not null) yield return BuildV2Line(curId, curType, curData, parts!);
                (curId, curType, curData, parts) = (id, r.GetString(1), r.GetString(2), new JsonArray());
            }
            if (!r.IsDBNull(3)) parts!.Add((JsonNode?)JsonNode.Parse(r.GetString(3)));
        }
        if (curId is not null) yield return BuildV2Line(curId, curType, curData, parts!);
    }

    /// <summary>A line <see cref="SynthesizeLines"/> rebuilt from v2's tables, which only the canonical route reads.</summary>
    public static bool IsV2Line(string line) => !line.StartsWith("{\"info\":", StringComparison.Ordinal);

    static string BuildV2Line(string id, string type, string data, JsonArray parts) {
        var message = JsonNode.Parse(data)!.AsObject();
        message["id"]   = id;
        message["type"] = type;
        if (type == "assistant" && parts.Count > 0) {
            if (message["content"] is JsonArray inline)
                foreach (var part in inline) parts.Add(part?.DeepClone());
            message["content"] = parts;
        }
        return message.ToJsonString();
    }

    // Merge the row's key columns back onto the message's data JSON to reproduce the
    // live SDK's `info` object: {id (= message.id), sessionID (= the session row id)}.
    static string BuildLine(string msgId, string sessionId, string msgData, JsonArray parts) {
        var info = JsonNode.Parse(msgData)!.AsObject();
        info["id"]        = msgId;
        info["sessionID"] = sessionId;
        return new JsonObject { ["info"] = info, ["parts"] = parts }.ToJsonString();
    }

    /// <summary>True when a reconstructed line projects to at least one canonical event: these lines gate
    /// <c>TooShort</c>, so a line that maps to nothing must not count.</summary>
    public static bool IsImportRelevantLine(string line) {
        var projection = OpenCodeTranscriptEvents.Instance;
        return projection.Project(line, 0, DateTimeOffset.UnixEpoch, projection.CreateContext("", null)).Events.Count > 0;
    }

    public void Dispose() => _conn.Dispose();
}
