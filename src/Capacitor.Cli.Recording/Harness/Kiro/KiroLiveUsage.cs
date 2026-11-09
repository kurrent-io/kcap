using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Kiro;

namespace Capacitor.Cli.Recording.Harness.Kiro;

/// <summary>
/// Kiro's per-turn credits and context percentage live in the sidecar <c>{id}.json</c>, not the
/// <c>.jsonl</c> a recorder tails, and Kiro flushes the sidecar after a live drain has usually sent the
/// turn's line. So a recorder emits a synthetic <c>KiroUsageBackfilled</c> line per turn anchor, which the
/// server folds into a backfill event keyed on the anchor.
/// </summary>
internal static class KiroLiveUsage {

    // A high band clear of real line numbers and of Antigravity's synthetic USAGE band.

    const long KiroUsageLineBase = 2_000_000_000L;

    /// <summary>
    /// Builds the synthetic JSONL line the server recognizes as a Kiro usage backfill for one
    /// turn anchor (the turn's final <c>message_id</c>). <paramref name="contextPct"/> is omitted
    /// when absent, mirroring <see cref="KiroUsage.EnrichLine"/>'s optional-field handling.
    /// </summary>
    internal static string BuildKiroUsageBackfillLine(string anchor, double credits, double? contextPct) {
        var data = new JsonObject {
            ["message_id"] = anchor,
            ["credits"]    = credits,
        };
        if (contextPct is { } pct) data["context_usage_percentage"] = pct;

        var root = new JsonObject {
            ["kind"] = "KiroUsageBackfilled",
            ["data"] = data,
        };
        return root.ToJsonString();
    }

    /// <summary>
    /// Appends a synthetic line for each turn anchor not yet in
    /// <see cref="WatchState.KiroUsageEmittedAnchors"/>, staging the anchors on
    /// <see cref="WatchState.KiroUsagePendingAnchors"/> for the caller to commit after a successful send. Returns
    /// the count staged, 0 on a missing or malformed sidecar. Never throws: usage is best-effort.
    /// </summary>
    internal static long AppendKiroUsageBackfillLines(WatchState state, List<string> newLines, List<int> newLineNumbers, string transcriptPath, Action<string> log) {
        state.KiroUsagePendingAnchors.Clear();
        var staged = 0L;
        try {
            var metaPath = Path.ChangeExtension(transcriptPath, ".json");
            if (!File.Exists(metaPath)) return 0;

            var anchors = KiroUsage.AnchorMap(File.ReadAllTextShared(metaPath)); // never lock Kiro out of its own sidecar
            var offset  = 0;

            foreach (var (anchor, usage) in anchors) {
                if (state.KiroUsageEmittedAnchors.Contains(anchor)) { offset++; continue; }

                // Tokens stay upstream-blocked, so a turn with only zero token counts has nothing to backfill.
                if (usage.Credits == 0 && usage.ContextPct is null) { offset++; continue; }

                newLines.Add(BuildKiroUsageBackfillLine(anchor, usage.Credits, usage.ContextPct));
                newLineNumbers.Add((int)(KiroUsageLineBase + offset));
                state.KiroUsagePendingAnchors.Add(anchor);
                staged++;
                offset++;
            }
        } catch (Exception ex) {
            // Usage is always best-effort — never let a sidecar read break the drain.
            log($"Kiro usage backfill poll failed: {ex.Message}");
        }
        return staged;
    }

    /// <summary>
    /// Stamps Kiro's live context percentage onto each AssistantMessage line from the sibling
    /// <c>{id}.json</c>, which is derived from the transcript path. It has to be on the first send: the server
    /// dedupes Kiro events by canonical id, so it can't be backfilled. Order-preserving, and never throws or
    /// drops a line.
    /// </summary>
    internal static List<string> EnrichKiroContextUsage(List<string> lines, string transcriptPath, OnceLog? warnings = null) {
        // Nothing to enrich unless the batch has an AssistantMessage line — skip the file read entirely.
        if (!lines.Any(static l => l.Contains("AssistantMessage", StringComparison.Ordinal))) return lines;
        try {
            var siblingJson = Path.ChangeExtension(transcriptPath, ".json");
            if (!File.Exists(siblingJson)) return lines;

            var anchors = KiroUsage.AnchorMap(File.ReadAllTextShared(siblingJson)); // never lock Kiro out of its own sidecar
            if (anchors.Count == 0) return lines;

            return lines.Select(l => KiroUsage.EnrichLine(l, anchors)).ToList();

        } catch (Exception ex) {
            // A persistently unreadable sibling would otherwise log one line per flush.
            warnings?.Write($"Kiro context-% enrichment failed (further failures suppressed): {ex.Message}");
            return lines;
        }
    }
}
