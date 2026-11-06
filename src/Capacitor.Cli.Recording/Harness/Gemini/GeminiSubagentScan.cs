using Capacitor.Cli.Core.Harness.Gemini;

namespace Capacitor.Cli.Recording.Harness.Gemini;

/// <summary>Finds a Gemini session's subagents and records each one's transcript as its own source.</summary>
public static class GeminiSubagentScan {
    /// <summary>
    /// Gemini fires no subagent hooks, so the parent watcher discovers nested subagent
    /// transcripts itself (<c>chats/&lt;parentSessionId&gt;/&lt;subId&gt;.jsonl</c>). On first
    /// sight of a file it registers the subagent (<c>subagent-start</c>, fail-closed) then
    /// spawns a detached child watcher that streams it with the subagent's canonical agentId
    /// (→ <c>AgentSubsession-*</c>). Idempotent across ticks via <paramref name="seen"/>;
    /// deterministic server-side lifecycle ids make re-registration safe.
    /// </summary>
    public static async Task ScanGeminiSubagents(
            SubagentScanContext context,
            string              sessionId,
            string              transcriptPath,
            HashSet<string>     seen,
            ICollection<string> spawnedChildKeys,
            CancellationToken   ct
        ) {
        IReadOnlyList<string> subFiles;
        try {
            subFiles = GeminiSubagentDiscovery.EnumerateSubagentFiles(transcriptPath);
        } catch {
            return; // discovery is best-effort — never break the main drain loop
        }

        if (subFiles.Count == 0) return;

        IReadOnlyDictionary<string, string>? types = null;

        foreach (var subFile in subFiles) {
            if (ct.IsCancellationRequested) return;
            if (!seen.Add(subFile)) continue; // already registered + spawned

            var subId = Path.GetFileNameWithoutExtension(subFile);
            if (!Guid.TryParse(subId, out _)) continue; // not a <subId>.jsonl

            var agentId   = GeminiSubagentDiscovery.CanonicalAgentId(subId);
            types       ??= GeminiSubagentDiscovery.ResolveAgentTypes(transcriptPath);
            var agentType = types.GetValueOrDefault(subId) ?? "subagent";

            // Fail-closed: register the subagent (→ SubagentStarted) before its child watcher
            // streams content. On POST failure, drop from `seen` so the next tick retries.
            if (!await context.PostSubagentStartAsync(sessionId, agentId, agentType, subFile, ct)) {
                seen.Remove(subFile);
                continue;
            }

            var child = new RecordingSource(sessionId, agentId, subFile, "gemini", null);
            await context.Host.StartChildAsync(child);
            spawnedChildKeys.Add(child.Key);

            context.Log($"Gemini subagent {agentId} ({agentType}) registered + child watcher spawned");
        }
    }
}
