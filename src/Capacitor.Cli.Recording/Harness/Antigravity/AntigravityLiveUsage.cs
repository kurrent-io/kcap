using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Antigravity;

namespace Capacitor.Cli.Recording.Harness.Antigravity;

/// <summary>What a recorder reads from Antigravity transcript steps as they arrive.</summary>
internal static class AntigravityLiveUsage {
    // Synthetic USAGE line numbers live in a high band so they never collide with real
    // transcript line numbers (which start at 0). The server derives the USAGE event id
    // from line CONTENT (which carries gen_row), so the line number is only an ordering
    // hint — a stable, non-colliding value keeps re-sends idempotent.
    const long AntigravityUsageLineBase = 1_000_000_000L;

    /// <summary>
    /// Appends synthetic USAGE lines for gen_metadata rows past the committed watermark, and
    /// returns the max row idx staged (-1 if none). It does NOT advance the watermark — the
    /// caller commits it only after the batch send succeeds, so a failed send re-reads the
    /// rows next drain. The db is the sibling of the transcript (its path carries the real
    /// conversation id, even when the session id is the canonical dashless form).
    /// </summary>
    internal static long AppendAntigravityUsageLines(
            WatchState state, List<string> newLines, List<int> newLineNumbers, string transcriptPath, string? createdAt,
            Func<string, long, string?, IReadOnlyList<(long Idx, string Line)>> readUsage, Action<string> log) {
        var maxIdx = -1L;
        try {
            if (AntigravityPaths.ConversationDbFromTranscript(transcriptPath) is not { } dbPath) return -1L;
            var rows = readUsage(dbPath, state.LastAntigravityGenIdx, createdAt);

            foreach (var (idx, line) in rows) {
                newLines.Add(line);
                newLineNumbers.Add((int)(AntigravityUsageLineBase + idx));
                if (idx > maxIdx) maxIdx = idx;
            }
        } catch (Exception ex) {
            // Cost is always best-effort — never let a db read break the drain.
            log($"Antigravity usage poll failed: {ex.Message}");
        }
        return maxIdx;
    }


    /// <summary>
    /// Subagent-orchestration tool calls whose result arrives ASYNCHRONOUSLY via a separate
    /// conversation (the child reports back through <c>brain/&lt;parent&gt;/.system_generated/
    /// messages</c>, not as a result STEP in the parent's own transcript). They therefore never
    /// produce a decrement step, so counting them as "in flight" would pin
    /// <see cref="WatchState.PendingAntigravityToolCalls"/> above zero forever and permanently
    /// suppress the parent's idle-end — a subagent-invoking conversation would only end when
    /// Antigravity quits. They do not block the parent's idleness, so they are excluded
    /// from the pending count.
    /// </summary>
    static readonly HashSet<string> AsyncAntigravityToolCalls =
        new(StringComparer.OrdinalIgnoreCase) { "invoke_subagent", "define_subagent" };

    /// <summary>
    /// Tracks Antigravity tool calls in flight from a transcript line: a PLANNER_RESPONSE adds
    /// its tool_calls count (excluding async subagent-orchestration calls, see
    /// <see cref="AsyncAntigravityToolCalls"/>); a result step (RUN_COMMAND/VIEW_FILE/
    /// LIST_DIRECTORY/CODE_ACTION) removes one. Safe to call for any line — non-matching /
    /// malformed lines are ignored — so the count reflects whether a (possibly long-running)
    /// tool is awaiting its in-transcript result.
    /// </summary>
    internal static void UpdateAntigravityPendingToolCalls(WatchState state, string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            switch (root.Str("type")) {
                case "PLANNER_RESPONSE":
                    if (root.Arr("tool_calls") is { } calls)
                        state.PendingAntigravityToolCalls += calls.EnumerateArray()
                            .Count(tc => tc.Str("name") is { } name && !AsyncAntigravityToolCalls.Contains(name));
                    break;
                case "RUN_COMMAND" or "VIEW_FILE" or "LIST_DIRECTORY" or "CODE_ACTION":
                    if (state.PendingAntigravityToolCalls > 0) state.PendingAntigravityToolCalls--;
                    break;
            }
        } catch {
            // Never break the drain loop on a malformed line.
        }
    }

    internal static string? TryGetAntigravityCreatedAt(string line) {
        try {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Str("created_at") : null;
        } catch {
            return null;
        }
    }
}
