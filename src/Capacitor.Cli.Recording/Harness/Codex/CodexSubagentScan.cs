using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Recording.Harness.Codex;

/// <summary>Finds a Codex session's collab subagents and records each one's rollout as its own source.</summary>
internal static class CodexSubagentScan {
    /// <summary>
    /// Codex fires no subagent hooks; its collab subagents (Codex 0.146+, multi-agent v2) each
    /// fork into their OWN rollout in the shared <c>~/.codex/sessions</c> tree, linked back via
    /// the child <c>session_meta</c>'s <c>parent_thread_id</c> / <c>thread_source: "subagent"</c>.
    /// The parent watcher scans that tree (day dirs from the parent's date forward, one header
    /// read per new file — see <see cref="CodexSubagentDiscovery.EnumerateSubagentRollouts"/>):
    /// on first sight it registers the subagent (<c>subagent-start</c>, fail-closed) then spawns
    /// a detached child watcher streaming the child rollout under the child's canonical agentId
    /// (→ <c>AgentSubsession-*</c>). Idempotent across ticks via <c>seen</c>;
    /// deterministic server-side lifecycle ids make re-registration safe. Disk enumeration (not
    /// the parent's in-band <c>sub_agent_activity</c> events) so a restarted parent watcher
    /// still recovers already-spawned children.
    /// </summary>
    public static async Task ScanCodexSubagents(
            SubagentScanContext          context,
            string                       sessionId,
            string                       transcriptPath,
            HashSet<string>              seen,
            HashSet<string>              ruledOut,
            Dictionary<string, DateTime> rolloutMtimes,
            ICollection<string>          spawnedChildKeys,
            CancellationToken            ct
        ) {
        List<CodexSubagentDiscovery.SubagentRollout> subs;
        try {
            subs = CodexSubagentDiscovery.EnumerateSubagentRollouts(transcriptPath, sessionId, ruledOut);
        } catch {
            return; // discovery is best-effort — never break the main drain loop
        }

        IReadOnlyDictionary<string, string>? spawnCalls = null;

        foreach (var sub in subs) {
            if (ct.IsCancellationRequested) return;

            // Both gates evaluate before branching so first sight also records the mtime. An
            // already-seen rollout that hasn't grown keeps its watcher state as-is — alive and
            // streaming, or reaped-idle — rather than respawning a reaped child for a finished rollout.
            var advanced = ChildRolloutMtimes.Advanced(rolloutMtimes, sub.FilePath, File.GetLastWriteTimeUtc(sub.FilePath));
            var isNew    = seen.Add(sub.FilePath);

            if (!isNew && !advanced) continue;

            var childAgentId = sub.ChildDashlessId;
            var agentType    = CodexSubagentDiscovery.AgentTypeFrom(sub.AgentPath, sub.AgentNickname);

            // Fail-closed: register the subagent (→ SubagentStarted) before its child watcher
            // streams content. On POST failure, drop from `seen` so the next tick retries.
            if (isNew
             && !await PostCodexSubagentStartAsync(context, sessionId, childAgentId, agentType, sub.FilePath,
                    CodexSubagentDiscovery.SpawnCallFor(spawnCalls ??= CodexSubagentDiscovery.ReadSpawnCalls(transcriptPath), sub.AgentPath), ct)) {
                seen.Remove(sub.FilePath);
                continue;
            }

            var child = new RecordingSource(sessionId, childAgentId, sub.FilePath, "codex", null);
            await context.Host.StartChildAsync(child);
            spawnedChildKeys.Add(child.Key);

            if (isNew) context.Log($"Codex subagent {childAgentId} ({agentType}) registered + child watcher spawned");
        }
    }

    static async Task<bool> PostCodexSubagentStartAsync(
        SubagentScanContext context, string sessionId, string agentId, string agentType, string subFile, string? spawnCallId, CancellationToken ct
    ) {
        try {
            using var client  = await context.Http.ForBackgroundAsync(ct);
            var       payload = CodexSubagentDiscovery.BuildStartPayload(sessionId, agentId, agentType, subFile, spawnCallId);
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{context.Url}/hooks/subagent-start", content, context.Time, ct: ct);

            return resp.IsSuccessStatusCode;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Overall budget for one live child-stop POST attempt (auth client creation + the POST
    /// itself). Must stay well inside <see cref="WatcherHeartbeat.Threshold"/> (20s): the
    /// main loop touches the heartbeat once per iteration and awaits this call inline, so a
    /// slow attempt on the default 30s retry budget could mark the watcher stale and churn
    /// it exactly at turn completion. Retries ride later loop iterations instead, on the
    /// <see cref="CodexSubagentStopRetryInterval"/> cadence.
    /// </summary>
    static readonly TimeSpan CodexSubagentStopPostBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Minimum spacing between live child-stop POST attempts, so a persistent failure
    /// (outage, auth trouble) never hammers <c>/hooks/subagent-stop</c> at the loop's 1s
    /// cadence. Deliberately no terminal give-up: an auth refresh can heal a 401 and the
    /// parent-end teardown dedupes whatever this never manages to post.
    /// </summary>
    public static readonly TimeSpan CodexSubagentStopRetryInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The live per-child stop, posted by the CHILD watcher itself once its turn
    /// completed and idled — the same wire shape <c>CodexSubagentTeardown</c> posts at
    /// parent end (which stays the backstop; the server dedupes the duplicate stop via its
    /// deterministic lifecycle event ids). Bounded by
    /// <see cref="CodexSubagentStopPostBudget"/>; failures are logged and reported to the
    /// caller, never thrown.
    /// </summary>
    public static async Task<bool> PostCodexSubagentStopAsync(
        SubagentScanContext context, string sessionId, string agentId, string agentType, string subFile, CancellationToken ct
    ) {
        try {
            using var budgetCap = new CancellationTokenSource(CodexSubagentStopPostBudget, context.Time);
            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct, budgetCap.Token);

            using var client  = await context.Http.ForBackgroundAsync(budgetCts.Token);
            var       payload = CodexSubagentDiscovery.BuildStopPayload(sessionId, agentId, agentType, subFile);
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{context.Url}/hooks/subagent-stop", content, context.Time, timeout: CodexSubagentStopPostBudget, ct: budgetCts.Token);

            if (!resp.IsSuccessStatusCode) {
                context.Log($"Codex subagent {agentId} stop POST returned {(int)resp.StatusCode}; "
                  + $"retrying in {CodexSubagentStopRetryInterval.TotalSeconds:F0}s");
            }

            return resp.IsSuccessStatusCode;
        } catch (Exception ex) {
            context.Log($"Codex subagent {agentId} stop POST failed: {ex.Message}; "
              + $"retrying in {CodexSubagentStopRetryInterval.TotalSeconds:F0}s");

            return false;
        }
    }

    /// <summary>
    /// Agent type for a child watcher's own stop payload, read from the child rollout's
    /// <c>session_meta</c> header — the child watcher isn't handed the type at spawn (only the
    /// parent's scan derives it), and <see cref="CodexSubagentDiscovery.ReadHeader"/> never
    /// throws, so a still-unreadable header just yields the generic fallback.
    /// </summary>
    public static string ResolveCodexChildAgentType(string transcriptPath) {
        var (_, meta) = CodexSubagentDiscovery.ReadHeader(transcriptPath);

        return CodexSubagentDiscovery.AgentTypeFrom(meta.AgentPath, meta.AgentNickname);
    }
}
