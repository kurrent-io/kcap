using System.Globalization;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Harness.Titles;

namespace Capacitor.Cli.Harness.Antigravity;

/// <summary>
/// Discover + classify + import historical Google Antigravity conversations.
/// Each conversation is a brain dir under
/// <c>~/.gemini/antigravity/brain/&lt;id&gt;/.system_generated/logs/transcript_full.jsonl</c> —
/// the same lines the live watcher tails — so historical and live import converge on the
/// server's <c>AntigravityTranscriptNormalizer</c> (routed phase, like Gemini).
///
/// <para>The conversation id is the brain dir name (a dashed UUID). The session id we surface
/// is its DASHLESS canonical form — matching live capture (the Antigravity hook and
/// <c>kcap watch</c> strip dashes) so a session captured live and later re-imported dedupes to
/// one stream, and so <c>kcap import --antigravity --session &lt;id&gt;</c> filtering is
/// format-insensitive. The dashed conversation id is kept only for filesystem paths
/// (the brain-dir transcript). Subagents are separate conversations linked via
/// <c>INVOKE_SUBAGENT</c> steps in the parent's transcript (the spawn-time signal;
/// see <see cref="AntigravitySubagents"/>); roots are conversations
/// that are never a child, and their children are imported as subagents under them — each with
/// the DASHLESS child conversation id as its <c>agent_id</c> (the server canonicalizes agent_id
/// to dashless on both ingest and watermark read, so this matches live routing; mirrors
/// <c>GeminiImportSource</c>). Antigravity records no machine-readable workspace path in the
/// transcript, so import leaves <c>cwd</c> null (no repo enrichment / exclusion on historical
/// import — a v1 limitation shared with Gemini; live capture gets cwd from the hook payload).</para>
///
/// <para>Token/model cost lives off-transcript in each conversation's <c>gen_metadata</c> db; the
/// live path streams it as synthetic USAGE lines and import injects it via
/// <see cref="PostUsageLinesAsync"/>. **Subagents are the exception**: each child is its own
/// conversation with its own db, and <see cref="ImportChildrenAsync"/> posts child content without a
/// usage pass, so imported subagents carry content but not cost.</para>
/// </summary>
internal sealed class AntigravityImportSource : IImportSource {
    static readonly HashSet<string> RelevantTypes = new(StringComparer.Ordinal) {
        "USER_INPUT", "PLANNER_RESPONSE", "RUN_COMMAND", "VIEW_FILE", "LIST_DIRECTORY", "CODE_ACTION"
    };

    readonly AntigravityPaths _paths;

    readonly TimeProvider _time;

    public AntigravityImportSource(AntigravityPaths paths, TimeProvider time) {
        _paths = paths;
        _time  = time;
    }

    // Both product roots' brain dirs (GUI + agy CLI), in fixed order. Import enumerates every one
    // that exists — an agy-only machine has only the CLI root, and before this it was invisible.
    IReadOnlyList<string> BrainRoots =>
        _paths.BrainProductRoots.Select(r => Path.Combine(r, "brain")).ToList();

    public HarnessId Vendor => HarnessId.Antigravity;
    public bool   IsAvailable => BrainRoots.Any(Directory.Exists);
    public bool   SupportsTitleGeneration => false; // server computes a fallback title at session-end
    public bool   AttachesChildContentOnReplay => true;  // AlreadyLoaded repair branch imports children

    static void ReportDrift(TextWriter? stderr, int danglingChild, int msgButNoInvoke) {
        var drift = new List<string>();

        if (danglingChild > 0)
            drift.Add($"{danglingChild} invoked subagent(s) have no conversation directory");

        if (msgButNoInvoke > 0)
            drift.Add($"{msgButNoInvoke} conversation(s) have messages but no invoke edge, so their subagents may import as separate sessions");

        if (drift.Count > 0) (stderr ?? Console.Error).WriteLine($"! Antigravity: {string.Join("; ", drift)}.");
    }

    public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        var result = new List<DiscoveredSession>();

        // Normalize the --session filter to the dashless canonical form so it matches the
        // dashless session id we surface, whether the user passed a dashed or dashless id
        // (mirrors GeminiImportSource).
        var sessionFilter = filters.FilterSession is { } fs ? ImportCommand.NormalizeGuid(fs) : null;

        var sinceUtc = filters.Since is { } since
            ? new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero)
            : (DateTimeOffset?)null;

        // Each product root is scanned independently: chains never cross roots (a child's
        // messages/ linkage lives beside its parent under one root) and conversation ids are
        // unique UUIDs, so results concatenate with no cross-root dedup.
        foreach (var brainRoot in BrainRoots) {
            ct.ThrowIfCancellationRequested();
            DiscoverInRoot(brainRoot, sessionFilter, sinceUtc, result, filters.Stderr, ct);
        }

        return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);
    }

    void DiscoverInRoot(
            string brainRoot, string? sessionFilter, DateTimeOffset? sinceUtc,
            List<DiscoveredSession> result, TextWriter? stderr, CancellationToken ct) {
        if (!Directory.Exists(brainRoot)) return;

        // The product root this brain dir belongs to (brainRoot is "<productRoot>/brain"), carried
        // onto each session so ImportChildrenAsync resolves child transcripts under the SAME root.
        var productRoot = Path.GetDirectoryName(brainRoot)!;

        // Resolve every conversation to its top-level ancestor: roots (no parent) import as
        // sessions; ALL transitive descendants (children, grandchildren, …) import as their
        // root's subagents. Cycle-/non-tree-safe (BuildRootDescendants) so a deep chain isn't
        // lost and a cycle imports standalone rather than vanishing. Linkage is complete on disk.
        var parentMap = AntigravitySubagents.BuildParentMapUnder(brainRoot, ct);
        List<string> convIds;
        try {
            convIds = Directory.EnumerateDirectories(brainRoot).Select(Path.GetFileName).OfType<string>().ToList();
        } catch {
            // A hostile/inaccessible brain root must not abort the whole import pass.
            convIds = [];
        }
        var byRoot    = AntigravitySubagents.BuildRootDescendants(convIds, parentMap);

        // Format drift shows as linkage the invoke edges no longer explain; a clean tree says nothing, since this
        // prints into the setup wizard and `import --discover`. Sets keep the counts O(n) on large brain trees.
        var allConversationIds = convIds.ToHashSet(StringComparer.Ordinal);
        var linkedChildIds     = parentMap.Values.ToHashSet(StringComparer.Ordinal);
        var danglingChild = parentMap.Keys.Count(c => !allConversationIds.Contains(c));
        var msgButNoInvoke = convIds.Count(id =>
            Directory.Exists(_paths.MessagesDirUnder(productRoot, id))
            && !parentMap.ContainsKey(id) && !linkedChildIds.Contains(id));
        ReportDrift(stderr, danglingChild, msgButNoInvoke);

        foreach (var (convId, descendants) in byRoot) {
            ct.ThrowIfCancellationRequested();

            // Dashless canonical id — matches live capture and fixes format-sensitive --session
            // filtering. The dashed convId still resolves the on-disk brain-dir transcript path.
            var sessionId = ImportCommand.NormalizeGuid(convId);

            if (sessionFilter is not null && !string.Equals(sessionId, sessionFilter, StringComparison.Ordinal)) continue;

            var transcript = _paths.TranscriptFullPathUnder(productRoot, convId);
            if (!File.Exists(transcript)) continue;

            var firstTimestamp = ReadFirstTimestamp(transcript);
            if (firstTimestamp is null) {
                try { firstTimestamp = File.GetCreationTimeUtc(transcript); } catch { /* best effort */ }
            }
            if (sinceUtc is { } cutoff && firstTimestamp is { } ts && ts < cutoff) continue;

            result.Add(new DiscoveredSession(
                SessionId:      sessionId,
                Vendor:         Vendor,
                Cwd:            null,
                FirstTimestamp: firstTimestamp,
                SourceMeta:     new Dictionary<string, object?> {
                    ["TranscriptPath"] = transcript,
                    // The product root this session lives under, so ImportChildrenAsync resolves
                    // its children under the same root rather than defaulting to the GUI's.
                    ["ProductRoot"]    = productRoot,
                    // Dashed conversation ids — kept dashed because they resolve on-disk brain-dir
                    // transcript paths in ImportChildrenAsync; the server-facing agent_id is
                    // canonicalized to dashless there.
                    ["Children"]       = descendants, // all transitive descendants, nested under this root
                }));
        }
    }

    public Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
            IReadOnlyList<DiscoveredSession> sessions, ClassifyContext ctx, CancellationToken ct) =>
        RoutedImportClassifier.ClassifyAsync(sessions, ctx, new RoutedClassifyOptions(Vendor, _time, IsImportRelevantLine, ImportPaths.TryGetLastWriteUtc) {
            RethrowProbeCancellation = true,
        }, ct);

    public async Task<ImportSessionResult> ImportSessionAsync(
            ImportCommand.SessionClassification c, ImportContext ctx, CancellationToken ct) {
        var transcriptPath = (string)c.SourceMeta!["TranscriptPath"]!;
        if (!File.Exists(transcriptPath)) return ImportOutcome.Failed;

        var client = Client(ctx.HttpClient, ctx.BaseUrl);

        // An AlreadyLoaded root's parent transcript is fully imported, but a prior run may have
        // advanced the transcript watermark and then FAILED a lifecycle POST — most damagingly
        // session-end, leaving the session with no end event and stuck "running" (the HWM tracks
        // only content lines, not lifecycle). The import pipeline routes AlreadyLoaded here
        // precisely so a source can re-assert lifecycle. Re-post session-start, repair children (a
        // once-skipped subagent would otherwise be lost forever), then re-post session-end — all
        // idempotent server-side (deterministic lifecycle ids → no-ops when they already
        // succeeded). If either lifecycle POST fails, return Failed so a re-run retries the repair
        // instead of reporting a falsely-complete Skipped (mirrors Cursor).
        if (c.Status == ImportCommand.ClassificationStatus.AlreadyLoaded) {
            var repairPayload = SyntheticHookPayloads.SessionStart("sessionStart", c.SessionId, c.Meta.Cwd, c.Meta.FirstTimestamp, source: null);

            if (ctx.VisibilityStampFor(c.Status) is { } repairVisibility) {
                repairPayload["default_visibility"] = repairVisibility;
            }

            if (!await client.PostSyntheticHookAsync("session-start/antigravity", repairPayload, ct))
                return ImportOutcome.Failed;

            // Capture whether the repair actually attached new nested-child content — true
            // iff any child's SendTranscriptBatches sent >0 server-accepted lines. The
            // lifecycle-only repair path (subagent-start+stop, no content resend) contributes
            // false. This outcome stays hardcoded Skipped — only SentChildContent is threaded
            // through below.
            var sentChildContent = await ImportChildrenAsync(ctx.HttpClient, ctx.BaseUrl, c.SessionId, c.SourceMeta!, ctx.Progress, ct);

            // Explicit gen_metadata usage pass — even though AlreadyLoaded sends zero real
            // transcript lines (the content watermark is already at the tip), a session
            // re-imported after this feature shipped may still be missing its USAGE lines
            // (they were never emitted on the original import). Always attempted, always
            // before session-end.
            await PostUsageLinesAsync(ctx, c.SessionId, transcriptPath, c.Meta.FirstTimestamp, ct);
            await PostHarnessTitleAsync(ctx, c.SessionId, transcriptPath, ct);

            if (!await client.PostSyntheticHookAsync("session-end/antigravity",
                    SyntheticHookPayloads.SessionEnd("sessionEnd", c.SessionId, "antigravity-import", c.Meta.Cwd, c.Meta.LastTimestamp), ct))
                return ImportOutcome.Failed;

            return new ImportSessionResult(ImportOutcome.Skipped, sentChildContent);
        }

        // Lifecycle-before-transcript (mirrors Gemini): a transcript that advances the
        // watermark past a failed lifecycle POST would leave the session lifecycle-less.
        // Re-runs are idempotent server-side (deterministic lifecycle ids).
        var startPayload = SyntheticHookPayloads.SessionStart("sessionStart", c.SessionId, c.Meta.Cwd, c.Meta.FirstTimestamp, source: null);
        if (ctx.VisibilityStampFor(c.Status) is { } visibility) {
            startPayload["default_visibility"] = visibility;
        }

        if (!await client.PostSyntheticHookAsync("session-start/antigravity", startPayload, ct))
            return ImportOutcome.Failed;

        var startLine = c.Status == ImportCommand.ClassificationStatus.Partial ? c.ResumeFromLine : 0;

        int sent;
        try {
            sent = await SessionImporter.SendTranscriptBatches(
                httpClient: ctx.HttpClient, baseUrl: ctx.BaseUrl, sessionId: c.SessionId,
                filePath: transcriptPath, agentId: null, startLine: startLine, time: _time, vendor: Vendor, progress: ctx.Progress);
        } catch (OperationCanceledException) {
            throw;
        } catch {
            return ImportOutcome.Failed;
        }

        // Explicit gen_metadata usage pass — decodes the sibling conversation .db and posts the
        // synthetic USAGE lines the live watcher would have streamed, so a content-only import
        // still gains cost. Best-effort (cost is never load-bearing); always before
        // session-end.
        await PostUsageLinesAsync(ctx, c.SessionId, transcriptPath, c.Meta.FirstTimestamp, ct);

        // Children as subagents — BEFORE session-end so SubagentCompleted precedes SessionEnded.
        // Subagent failures don't fail the (already-imported) parent; a re-import retries.
        await ImportChildrenAsync(ctx.HttpClient, ctx.BaseUrl, c.SessionId, c.SourceMeta!, ctx.Progress, ct);
        await PostHarnessTitleAsync(ctx, c.SessionId, transcriptPath, ct);

        if (!await client.PostSyntheticHookAsync("session-end/antigravity",
                SyntheticHookPayloads.SessionEnd("sessionEnd", c.SessionId, "antigravity-import", c.Meta.Cwd, c.Meta.LastTimestamp), ct))
            return ImportOutcome.Failed;

        if (sent == 0) return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Skipped;
        return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Loaded;
    }

    /// <summary>
    /// Returns <c>true</c> iff any child's <see cref="SessionImporter.SendTranscriptBatches"/>
    /// call sent &gt;0 server-accepted lines during this pass — the <c>SentChildContent</c>
    /// signal threaded onto the <c>AlreadyLoaded</c> caller's <see cref="ImportSessionResult"/>.
    /// The lifecycle-only repair branch below (subagent-start+stop, no content resend)
    /// contributes <c>false</c> — it's a repair, not new content.
    /// </summary>
    async Task<bool> ImportChildrenAsync(
            HttpClient http, string baseUrl, string rootId,
            IReadOnlyDictionary<string, object?> sourceMeta, IProgress<ImportProgress>? progress, CancellationToken ct) {
        var client = Client(http, baseUrl);

        if (!sourceMeta.TryGetValue("Children", out var kidsObj) || kidsObj is not List<string> { Count: > 0 } children)
            return false;

        // The root this session was discovered under — children resolve under the SAME root. Falls
        // back to the GUI root for a pre-ProductRoot SourceMeta (there is no such caller in-tree,
        // but the fold stays truthful rather than silently mis-resolving a CLI child).
        var productRoot = sourceMeta.TryGetValue("ProductRoot", out var pr) && pr is string { Length: > 0 } p
            ? p
            : _paths.Root;

        var anyChildContentSent = false;

        foreach (var childId in children) {
            ct.ThrowIfCancellationRequested();

            // childId is the DASHED conversation id — it names the on-disk brain dir, so it
            // resolves the transcript path. The server-facing agent_id is its DASHLESS canonical
            // form: the server canonicalizes agent_id on both ingest and watermark read, so this
            // matches live routing/correlation and the dashless session ids used everywhere else
            // (mirrors GeminiImportSource).
            var childTranscript = _paths.TranscriptFullPathUnder(productRoot, childId);
            if (!File.Exists(childTranscript)) continue;

            var childAgentId = ImportCommand.NormalizeGuid(childId);

            // Where the child's own (rootId, childAgentId) stream is already ingested up to.
            int? chwm;
            try {
                chwm = await client.FetchServerLastLineAsync(rootId, ct, childAgentId);
            } catch (OperationCanceledException) {
                throw;
            } catch {
                continue; // watermark probe failed for this child — skip it, retry on re-import
            }

            // Compare the server HWM against the child's last IMPORT-RELEVANT line (same gate the
            // parent uses for AlreadyLoaded): trailing normalizer-skipped steps mustn't force an
            // endless re-send, and a fully-ingested child is a no-op we skip before touching hooks.
            int? childLastImportable;
            try {
                var (lastNonBlank, lastRelevant, _) = await TranscriptStats.ReadAsync(childTranscript, IsImportRelevantLine, ct);
                childLastImportable = lastRelevant ?? lastNonBlank;
            } catch {
                continue; // unreadable child transcript — retry on re-import
            }
            if (childLastImportable is null) continue;                       // empty child

            if (chwm is { } done && done >= childLastImportable) {
                // Content is fully ingested, but subagent-stop is best-effort below and may have
                // failed AFTER the content POST on a prior run — leaving the subagent with no
                // completion event (HWM only tracks content lines, not lifecycle). Repair it by
                // re-posting subagent-start THEN subagent-stop. The server's stop endpoint no-ops
                // unless the agent is marked active, and that mark is transient in-memory — lost on
                // a server restart between the failed stop and this re-import. Re-posting start
                // restores the active mark (idempotent: deterministic SubagentStarted id, and
                // already-active is a no-op) so the following stop clears it and appends the
                // (idempotent, deterministic) SubagentCompleted. Child content is not re-sent.
                //
                // strict=true on BOTH: the server's start endpoint returns a fail-open 200 even
                // when RecordAgentStartAsync throws and rolls back the active mark, so a non-strict
                // start would be treated as success and the following stop would silently no-op
                // (no active agent → no SubagentCompleted). strict surfaces the failure as non-2xx,
                // so we only post stop when start truly re-marked the agent, and a re-import retries
                // otherwise.
                if (await client.PostSyntheticHookAsync("subagent-start",
                        SubagentHookPayloads.Start(rootId, childAgentId, "subagent", childTranscript, strict: true), ct))
                    await client.PostSyntheticHookAsync("subagent-stop",
                        SubagentHookPayloads.Stop(rootId, childAgentId, "subagent", childTranscript, strict: true), ct);

                continue;
            }

            // Resume by FILE POSITION (startLine), numbering surviving lines by their true position
            // (offset 0) — matching the parent's resume and live capture, so line numbers stay
            // stable across re-imports. (Content-hashed event ids dedupe already-sent lines, but a
            // shifted line number would corrupt $lineNumber + the watermark for genuinely-new lines.)
            var childStartLine = chwm is { } v ? checked(v + 1) : 0;

            // Fail-closed: no content unless the subagent registered first.
            if (!await client.PostSyntheticHookAsync("subagent-start",
                    SubagentHookPayloads.Start(rootId, childAgentId, "subagent", childTranscript), ct))
                continue;

            int childSent;
            try {
                // failOnError: true — SentChildContent must reflect only server-ACCEPTED
                // batches; a non-2xx throws and is caught below, so a rejected batch
                // never flips the signal.
                childSent = await SessionImporter.SendTranscriptBatches(
                    httpClient: http, baseUrl: baseUrl, sessionId: rootId,
                    filePath: childTranscript, agentId: childAgentId, startLine: childStartLine, time: _time, vendor: Vendor,
                    failOnError: true, progress: progress);
            } catch (OperationCanceledException) {
                throw;
            } catch {
                continue; // leave subagent-stop unsent; a re-import retries (idempotent)
            }

            if (childSent > 0) anyChildContentSent = true;

            await client.PostSyntheticHookAsync("subagent-stop",
                SubagentHookPayloads.Stop(rootId, childAgentId, "subagent", childTranscript), ct);
        }

        return anyChildContentSent;
    }

    // Synthetic USAGE line numbers live in a high band so they never collide with real transcript
    // line numbers (which start at 0). MUST match the live path's
    // AntigravityLiveUsage.AntigravityUsageLineBase AND the server's
    // SessionWriter.AntigravityUsageLineBase: the server derives the USAGE event id from
    // line CONTENT (gen_row), not the line number, so the number is only a non-colliding
    // ordering hint.
    const long AntigravityUsageLineBase = 1_000_000_000L;

    /// <summary>
    /// Decodes the sibling conversation <c>.db</c>'s <c>gen_metadata</c> rows into synthetic
    /// USAGE transcript lines and posts them directly via <c>/hooks/transcript</c> — NOT through
    /// <see cref="SessionImporter.SendTranscriptBatches"/>, whose AlreadyLoaded-derived startLine
    /// would skip a USAGE line entirely (it lives past the real-content watermark, in the high
    /// band). Deterministic USAGE event ids (keyed on gen_row) dedupe server-side, so re-posting
    /// on every re-import is safe. Called for every classification (New/Partial AND
    /// AlreadyLoaded) so a session imported before this feature shipped — content but no cost —
    /// gains usage on a bare re-import even though zero physical transcript lines are (re)sent.
    /// Best-effort: cost is never load-bearing — a decode/post failure
    /// here must never fail the import.
    /// </summary>
    async Task<bool> PostUsageLinesAsync(
            ImportContext ctx, string sessionId, string transcriptPath, DateTimeOffset? createdAt, CancellationToken ct) {
        if (AntigravityPaths.ConversationDbFromTranscript(transcriptPath) is not { } dbPath) return true;

        var rows = AntigravityGenMetadataDb.ReadUsageLines(dbPath, afterIdx: -1, createdAt: createdAt?.ToString("O"));
        if (rows.Count == 0) return true;

        var lines       = new string[rows.Count];
        var lineNumbers = new int[rows.Count];
        for (var i = 0; i < rows.Count; i++) {
            lines[i]       = rows[i].Line;
            lineNumbers[i] = (int)(AntigravityUsageLineBase + rows[i].Idx);
        }

        return await PostTranscriptLinesAsync(ctx.HttpClient, ctx.BaseUrl, sessionId, lines, lineNumbers, ct);
    }

    async Task<bool> PostTranscriptLinesAsync(
            HttpClient client, string baseUrl, string sessionId, string[] lines, int[] lineNumbers, CancellationToken ct) {
        var batch = new TranscriptBatch {
            SessionId   = sessionId,
            Lines       = lines,
            LineNumbers = lineNumbers,
            Vendor      = "antigravity",
            Strict      = false,
        };
        var json = JsonSerializer.Serialize(batch, CapacitorJsonContext.Default.TranscriptBatch);

        try {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await client.PostWithRetryAsync($"{baseUrl}/hooks/transcript", content, _time, ct: ct);
            return resp.IsSuccessStatusCode;
        } catch (OperationCanceledException) {
            throw;
        } catch {
            return false; // cost is always best-effort — never let a post failure surface
        }
    }

    // Best-effort, before session-end like the usage pass; a store with no title sends nothing.
    Task PostHarnessTitleAsync(ImportContext ctx, string sessionId, string transcriptPath, CancellationToken ct) =>
        ImportHarnessTitle.PostFromStoreAsync(
            AntigravitySummaryTitle.ForTranscript(transcriptPath), ctx.HttpClient, _time, ctx.BaseUrl, sessionId, ctx.Progress, ct);

    RoutedImportClient Client(HttpClient http, string baseUrl) => new(http, _time, baseUrl);

    // ── transcript reading ──────────────────────────────────────────────────────

    static DateTimeOffset? ReadFirstTimestamp(string path) {
        try {
            foreach (var line in File.ReadLines(path)) {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                 && doc.RootElement.TryGetProperty("created_at", out var ca)
                 && ca.GetString() is { } s
                 && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
                    return ts;
                return null; // first non-blank line had no created_at — don't scan the whole file
            }
        } catch { /* unreadable → null */ }
        return null;
    }

    /// <summary>
    /// True when a line maps to a canonical event under AntigravityTranscriptNormalizer —
    /// a conversational or tool-result step. Noise steps (SYSTEM_*, CHECKPOINT,
    /// CONVERSATION_HISTORY, GENERIC, INVOKE_SUBAGENT) are skipped server-side and can never
    /// advance the watermark; keep in sync with the normalizer.
    /// </summary>
    internal static bool IsImportRelevantLine(string line) {
        try {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var t)
                && t.GetString() is { } type
                && RelevantTypes.Contains(type);
        } catch {
            return false;
        }
    }
}
