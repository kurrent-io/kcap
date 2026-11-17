using System.Text.Json;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Harness.Muse;

/// <summary>
/// The PRs a Muse session creates. Each result of a <c>tool_result_batch_committed</c> run event carries, as JSON in
/// its <c>text</c>, the shell command it ran with its <c>exit_code</c>, <c>terminal_status</c> and <c>output</c>, so no
/// pairing with the call is needed. Truncated output may have lost its true last line, so it links nothing. A
/// <c>retained_frame</c> line holds whole records as JSON strings under <c>children[].record_json</c>, read as if each
/// were its own line. Subagents write their own <c>subagent/&lt;id&gt;/session.jsonl</c>, read by a child watcher.
/// </summary>
public sealed class MuseCreatedPullRequestAdapter : ICreatedPullRequestAdapter {
    public static MuseCreatedPullRequestAdapter Instance { get; } = new();

    MuseCreatedPullRequestAdapter() { }

    public void Observe(string line, CreatedPullRequestEvidence evidence) {
        if (!line.Contains("tool_result_batch_committed", StringComparison.Ordinal)) return;

        try {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.Arr("children") is { } children && root.Prop("retained_frame") is not null) {
                foreach (var child in children.EnumerateArray()) {
                    if (child.Str("record_json") is not { } json) continue;

                    try {
                        using var inner = JsonDocument.Parse(json);
                        Record(inner.RootElement, evidence);
                    } catch (JsonException) {
                        // one unreadable record costs only itself
                    }
                }

                return;
            }

            Record(root, evidence);
        } catch (JsonException) {
            // fail-open: an unreadable line links nothing
        }
    }

    static void Record(JsonElement record, CreatedPullRequestEvidence evidence) {
        if (record.Str("payload_type") != "runtime.session"
         || record.Obj("payload")?.Obj("event") is not { } evt
         || evt.Str("kind") != "tool_result_batch_committed"
         || evt.Arr("results") is not { } results) {
            return;
        }

        foreach (var result in results.EnumerateArray()) {
            if (result.Str("text") is not { } text || !text.Contains("\"command\"", StringComparison.Ordinal)) continue;

            try {
                using var run = JsonDocument.Parse(text);
                if (run.RootElement.Str("command") is not { } command) continue;

                evidence.Completed(result.Str("tool_call_id"), command, run.RootElement.Str("output"),
                    succeeded: run.RootElement.Num("exit_code") == 0 && run.RootElement.Str("terminal_status") is null or "completed"
                            && run.RootElement.Bool("truncated") != true);
            } catch (JsonException) {
                // a result whose text is not a shell report
            }
        }
    }
}
