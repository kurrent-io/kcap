using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.OpenCode;

namespace Capacitor.Cli.Recording.Harness.OpenCode;

/// <summary>Finds an OpenCode session's subagents and records each one's file as its own source.</summary>
internal static class OpenCodeSubagentScan {
    /// <summary>
    /// OpenCode fires no subagent hooks; its subagents live in its SQLite db, so the kcap
    /// plugin fetches each child session via the SDK and writes its <c>{info,parts}</c> JSONL
    /// into <c>&lt;cacheDir&gt;/&lt;parentSid&gt;/&lt;childSid&gt;.jsonl</c>. The parent watcher discovers
    /// those files (mirroring <c>GeminiSubagentScan.ScanGeminiSubagents</c>): on first sight it registers
    /// the subagent (<c>subagent-start</c>, fail-closed) then spawns a detached child watcher
    /// that streams it with the canonical agentId (= childSid) → <c>AgentSubsession-*</c>, which
    /// lines up with the agentId the server surfaced from the parent's <c>task</c> tool call.
    /// Idempotent across ticks via <paramref name="seen"/>; deterministic server-side lifecycle
    /// ids make re-registration safe.
    /// </summary>
    public static async Task ScanOpenCodeSubagents(
            SubagentScanContext context,
            string              sessionId,
            string              transcriptPath,
            HashSet<string>     seen,
            ICollection<string> spawnedChildKeys,
            CancellationToken   ct
        ) {
        IReadOnlyList<string> subFiles;
        try {
            subFiles = OpenCodeSubagentDiscovery.EnumerateSubagentFiles(transcriptPath);
        } catch {
            return; // discovery is best-effort — never break the main drain loop
        }

        if (subFiles.Count == 0) return;

        foreach (var subFile in subFiles) {
            if (ct.IsCancellationRequested) return;
            if (!seen.Add(subFile)) continue; // already registered + spawned

            var childId   = Path.GetFileNameWithoutExtension(subFile);
            var agentId   = OpenCodeSubagentDiscovery.CanonicalAgentId(childId);
            var agentType = OpenCodeSubagentDiscovery.ResolveAgentType(subFile);

            // Fail-closed: register the subagent (→ SubagentStarted) before its child watcher
            // streams content. On POST failure, drop from `seen` so the next tick retries.
            if (!await PostOpenCodeSubagentStartAsync(context, sessionId, agentId, agentType, subFile, ct)) {
                seen.Remove(subFile);
                continue;
            }

            var child = new RecordingSource(sessionId, agentId, subFile, "opencode", null);
            await context.Host.StartChildAsync(child);
            spawnedChildKeys.Add(child.Key);

            context.Log($"OpenCode subagent {agentId} ({agentType}) registered + child watcher spawned");
        }
    }

    static async Task<bool> PostOpenCodeSubagentStartAsync(
        SubagentScanContext context, string sessionId, string agentId, string agentType, string subFile, CancellationToken ct
    ) {
        try {
            using var client  = await context.Http.ForBackgroundAsync(ct);
            var       payload = OpenCodeSubagentDiscovery.BuildStartPayload(sessionId, agentId, agentType, subFile);
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{context.Url}/hooks/subagent-start", content, context.Time, ct: ct);

            return resp.IsSuccessStatusCode;
        } catch {
            return false;
        }
    }
}
