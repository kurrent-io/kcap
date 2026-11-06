using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording.Harness.Antigravity;

/// <summary>Links Antigravity subagents to their parent as they are spawned.</summary>
public static class AntigravitySubagentLinks {
    /// <summary>
    /// Links subagents from the parent transcript's INVOKE_SUBAGENT steps (the
    /// spawn-time signal). For each child id not already
    /// posted, POST the link once via <paramref name="post"/> (returns true on success); a failed
    /// POST is left un-posted so a later scan retries. Pure over its inputs for unit testing.
    /// </summary>
    internal static async Task ExtractAndPostSubagentLinks(
            IEnumerable<string> lines, HashSet<string> posted, Func<string, Task<bool>> post, Action<string> log) {
        foreach (var line in lines) {
            var children = AntigravitySubagents.ChildConversationIdsFromLine(line);
            if (children.Count == 0) {
                if (AntigravitySubagents.IsInvokeSubagentLine(line))
                    log("Antigravity INVOKE_SUBAGENT step had no parseable child conversationId (format drift?)");
                continue;
            }
            foreach (var child in children) {
                if (posted.Contains(child)) continue;
                if (await post(child)) posted.Add(child);   // fail-open: leave un-posted to retry
            }
        }
    }

    /// <summary>
    /// Live subagent nesting: links a subagent from its parent transcript's
    /// INVOKE_SUBAGENT steps — the spawn-time signal — rather than waiting for the child to
    /// report back. <paramref name="drainedLines"/> are the lines the watcher just drained from
    /// the parent transcript this tick. Each newly-seen child is POSTed to
    /// <c>/hooks/antigravity/subagent-link</c> and only added to <paramref name="posted"/> on
    /// success, so a failed POST retries on the next scan (fail-open — never breaks the drain
    /// loop).
    /// </summary>
    public static Task ScanAntigravitySubagentLinks(
            SubagentScanContext context, string sessionId, IReadOnlyList<string> drainedLines,
            HashSet<string> posted, CancellationToken ct) =>
        ExtractAndPostSubagentLinks(drainedLines, posted,
            child => PostAntigravitySubagentLinkAsync(context, sessionId, child, ct), context.Log);

    static async Task<bool> PostAntigravitySubagentLinkAsync(
        SubagentScanContext context, string sessionId, string childId, CancellationToken ct
    ) {
        try {
            using var client  = await context.Http.ForBackgroundAsync(ct);
            var       payload = new JsonObject {
                ["hook_event_name"] = "subagent-link",
                ["session_id"]      = sessionId,
                ["agent_id"]        = childId,
            };
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{context.Url}/hooks/antigravity/subagent-link", content, context.Time, ct: ct);

            return resp.IsSuccessStatusCode;
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            context.Log($"Antigravity subagent-link POST failed for child {childId}: {ex.Message}");
            return false;
        }
    }
}
