namespace Capacitor.Cli.Recording.Harness.MistralVibe;

/// <summary>Follows a Mistral Vibe session's subagents and records each one's store as its own source.</summary>
internal static class MistralVibeSubagentScan {
    /// <summary>
    /// Vibe fires no hooks for a subagent; the child runs in its own <c>child-&lt;hex&gt;</c> store
    /// beside the parent's, named only by the parent's <c>subagent.spawn</c> effect. The parent
    /// watcher registers each one (<c>subagent-start</c>, fail-closed), keeps a copy of its store, and
    /// spawns a child watcher to stream that copy. A child whose store is not there yet, or whose
    /// registration failed, is retried on the next tick.
    /// </summary>
    public static async Task ScanMistralVibeSubagents(
            SubagentScanContext       context,
            string                    sessionId,
            string                    transcriptPath,
            List<MistralVibeSubagent> found,
            HashSet<string>           seen,
            ICollection<string>       spawnedChildKeys,
            CancellationToken         ct
        ) {
        if (found.Count == 0 || MistralVibeLiveTranscript.SourceOf(transcriptPath) is not { } source) return;
        if (Path.GetDirectoryName(source.SessionDir) is not { } storeRoot) return;

        // Every refresh re-reports each spawn the store holds, so one child arrives many times.
        var pending = found.DistinctBy(s => s.ChildSessionId, StringComparer.Ordinal).ToList();
        found.Clear();

        foreach (var subagent in pending) {
            if (ct.IsCancellationRequested) return;
            if (subagent.AgentId is not { } agentId || seen.Contains(agentId)) continue;

            var childDir = Path.Combine(storeRoot, subagent.ChildSessionId);
            if (!Directory.Exists(childDir)) { found.Add(subagent); continue; }

            var key       = $"{sessionId}-{agentId}";
            var childCopy = MistralVibeLiveTranscript.PathFor(context.Config, key);
            MistralVibeLiveTranscript.Follow(childDir, childCopy, source.Model);

            if (!await context.PostSubagentStartAsync(sessionId, agentId, subagent.AgentType ?? "subagent", childCopy, ct)) {
                found.Add(subagent);
                continue;
            }

            seen.Add(agentId);
            await context.Host.StartChildAsync(new RecordingSource(sessionId, agentId, childCopy, "mistral-vibe", null));
            spawnedChildKeys.Add(key);

            context.Log($"Mistral Vibe subagent {agentId} registered + child watcher spawned");
        }
    }
}
