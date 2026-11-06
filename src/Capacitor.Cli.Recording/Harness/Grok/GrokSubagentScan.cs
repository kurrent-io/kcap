using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Grok;

namespace Capacitor.Cli.Recording.Harness.Grok;

/// <summary>Follows a Grok session's subagents and records each one's transcript as its own source.</summary>
public static class GrokSubagentScan {
    /// <summary>
    /// Grok fires no hook for a subagent: it runs as a sibling session the parent's transcript names when
    /// it is spawned and when it finishes. A spawned child is registered (<c>subagent-start</c>,
    /// fail-closed) and streamed by a child watcher once its transcript exists, retried each tick until
    /// then; a finished one is drained and stopped.
    /// </summary>
    public static async Task ScanGrokSubagents(
            SubagentScanContext   context,
            string                sessionId,
            string                transcriptPath,
            IReadOnlyList<string> drained,
            GrokSubagentTracker   tracker,
            ICollection<string>   spawnedChildKeys,
            CancellationToken     ct
        ) {
        if (tracker.Seeded) tracker.Observe(GrokSubagents.Read(drained));
        else {
            try { tracker.Seed(GrokSubagents.Read(File.ReadLinesShared(transcriptPath))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { tracker.Observe(GrokSubagents.Read(drained)); }
        }

        foreach (var (child, type) in tracker.AwaitingStart()) {
            if (ct.IsCancellationRequested) return;

            var transcript = GrokSubagents.ChildTranscript(transcriptPath, child);
            if (!File.Exists(transcript)) continue;

            var agentId = GrokSubagents.AgentId(child);
            if (!await context.PostSubagentStartAsync(sessionId, agentId, type ?? "subagent", transcript, ct)) continue;

            tracker.Started(child);
            context.Log($"Grok subagent {agentId} ({type}) registered");
        }

        // Every tick, not only at registration: a child watcher that failed to spawn or exited early is
        // restarted, and a failed launch leaves the parent watcher running.
        foreach (var (child, _) in tracker.Active()) {
            if (ct.IsCancellationRequested) return;

            var agentId = GrokSubagents.AgentId(child);
            var source  = new RecordingSource(sessionId, agentId, GrokSubagents.ChildTranscript(transcriptPath, child), "grok", null);
            try {
                await context.Host.StartChildAsync(source);
                spawnedChildKeys.Add(source.Key);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                context.Log($"Grok subagent {agentId} child watcher failed to start: {ex.Message}");
            }
        }

        foreach (var (child, type) in tracker.AwaitingStop()) {
            if (ct.IsCancellationRequested) return;

            if (await context.Host.FinishGrokChildAsync(sessionId, transcriptPath, child, type))
                tracker.Stopped(child);
        }
    }
}
