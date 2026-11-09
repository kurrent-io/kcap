using Capacitor.Cli.Core.Harness.Muse;

namespace Capacitor.Cli.Recording.Harness.Muse;

/// <summary>Finds a Muse session's children, records each one's log as its own source, and stops the finished ones.</summary>
internal static class MuseSubagentScan {
    /// <summary>
    /// A Muse child writes its own <c>subagent/&lt;id&gt;/session.jsonl</c> beside the parent's log; the
    /// scan is the authority on which children exist. A child the parent log has not announced yet is
    /// held back for <see cref="MuseChildWatchers.AnnouncementGraceScans"/> scans, so its label is not
    /// frozen as the placeholder when the announcement is merely flushed late.
    /// </summary>
    public static async Task ScanMuseSubagents(
            SubagentScanContext context,
            string              sessionId,
            string              transcriptPath,
            HashSet<string>     seen,
            MuseChildWatchers   children,
            ICollection<string> spawnedChildKeys,
            CancellationToken   ct
        ) {
        IReadOnlyList<string> childLogs;
        try {
            childLogs = MuseSubagentDiscovery.EnumerateChildLogs(transcriptPath);
        } catch {
            return; // discovery is best-effort — never break the main drain loop
        }

        if (childLogs.Count == 0) return;

        IReadOnlyDictionary<string, MuseChildLabel>? labels = null;

        foreach (var childLog in childLogs) {
            if (ct.IsCancellationRequested) return;
            if (seen.Contains(childLog)) continue;

            var childSessionId = Path.GetFileName(Path.GetDirectoryName(childLog)!);
            if (!Guid.TryParse(childSessionId, out var childGuid)) continue;

            labels ??= MuseSubagentDiscovery.ResolveChildLabels(transcriptPath);
            if (children.ShouldHold(childLog, announced: labels.ContainsKey(childGuid.ToString("D")))) continue;

            var label = MuseSubagentDiscovery.LabelFor(childSessionId, labels);
            var key   = $"{sessionId}-{label.AgentId}";
            var now   = context.Time.GetUtcNow();

            if (!children.ShouldAttemptSpawn(key, now)) continue;

            // Fail-closed: register before streaming; a refused registration is retried next tick.
            if (!children.IsRegistered(key)) {
                if (!await context.PostSubagentStartAsync(sessionId, label.AgentId, label.AgentType, childLog, ct)) continue;

                children.Registered(key);
            }

            if (!await context.Host.StartChildAsync(new RecordingSource(sessionId, label.AgentId, childLog, "muse", null))) {
                var delay = children.SpawnFailed(key, now);
                context.Log($"Muse subagent {label.AgentId} child watcher did not start; retrying in {delay.TotalSeconds:0}s");
                continue;
            }

            seen.Add(childLog);
            spawnedChildKeys.Add(key);
            children.Spawned(key, childLog, context.Time.GetUtcNow());

            context.Log($"Muse subagent {label.AgentId} ({label.AgentType}) registered + child watcher spawned");
        }
    }

    /// <summary>
    /// Stops the child watcher of a Muse worker once the parent log reports its <c>result_ready</c>, and
    /// restarts one whose log grows afterwards. The stop is a SIGTERM, so the child runs its final drain
    /// first. Reminder children have no such record and run until the parent ends. The full-log read
    /// happens only while a result may be unhandled.
    /// </summary>
    public static async Task StopFinishedMuseChildren(
            SubagentScanContext   context,
            string                sessionId,
            string                transcriptPath,
            IReadOnlyList<string> drained,
            MuseChildWatchers     children
        ) {
        if (drained.Any(l => l.Contains("subagent.control.result_ready", StringComparison.Ordinal))) children.RecheckPending = true;

        try {
            var reengagedAt = context.Time.GetUtcNow();

            foreach (var key in children.Reengaged(File.GetLastWriteTimeUtc, reengagedAt)) {
                var started = false;

                try {
                    started = await context.Host.StartChildAsync(new RecordingSource(sessionId, key[(sessionId.Length + 1)..], children.LogOf(key), "muse", null));
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    context.Log($"Muse subagent {key} child watcher restart failed: {ex.Message}");
                }

                if (started) {
                    children.SpawnSucceeded(key);
                    context.Log($"Muse subagent {key} resumed; child watcher restarted");
                } else {
                    var delay = children.RestartFailed(key, reengagedAt);
                    context.Log($"Muse subagent {key} child watcher did not restart; retrying in {delay.TotalSeconds:0}s");
                }
            }

            if (!children.RecheckPending) return;

            var ready = MuseSubagentDiscovery.ResolveResultReadyCounts(transcriptPath)
                .ToDictionary(kv => $"{sessionId}-{Guid.Parse(kv.Key):N}", kv => kv.Value, StringComparer.Ordinal);

            var stops = children.SelectStops(ready, context.Time.GetUtcNow());
            if (stops.Count == 0) return;

            // Read before the kill: a write after the child's final drain then still advances it.
            foreach (var key in stops) children.RecordLogMtime(key, File.GetLastWriteTimeUtc(children.LogOf(key)));

            var stopped = await children.Stop(context.Host, stops, ready);
            context.Log($"Stopped {stopped} of {stops.Count} finished Muse child watcher(s)");
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // best-effort — the parent's teardown still stops every spawned child
        }
    }
}
