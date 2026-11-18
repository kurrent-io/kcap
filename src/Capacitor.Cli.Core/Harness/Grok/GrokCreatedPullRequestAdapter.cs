using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Harness.Grok;

/// <summary>
/// The PRs a Grok session creates. A finished <c>tool_call_update</c> carries a shell command whole in its
/// <c>rawOutput</c>: a <c>run_terminal_command</c> as <c>{type:"Bash", command, exit_code}</c> with its output as the
/// update's text content, and a backgrounded command, read later through <c>get_command_or_subagent_output</c>, as
/// <c>{Result:{command, exit_code, output}}</c>. Truncated output may have lost its true last line, so it links nothing.
/// Subagents are sibling sessions, each read by its own child watcher.
/// </summary>
public sealed class GrokCreatedPullRequestAdapter : ICreatedPullRequestAdapter {
    public static GrokCreatedPullRequestAdapter Instance { get; } = new();

    GrokCreatedPullRequestAdapter() { }

    public void Observe(string line, CreatedPullRequestEvidence evidence) {
        if (!line.Contains("\"tool_call_update\"", StringComparison.Ordinal) || !line.Contains("\"exit_code\"", StringComparison.Ordinal)) return;

        try {
            using var doc = JsonDocument.Parse(line);

            if (doc.RootElement.Obj("params")?.Obj("update") is not { } update
             || update.Str("sessionUpdate") != "tool_call_update"
             || update.Obj("rawOutput") is not { } raw) {
                return;
            }

            var status = update.Str("status");
            if (status is not ("completed" or "failed")) return;

            var callId = update.Str("toolCallId");

            if (raw.Obj("Result") is { } background) {
                if (background.Str("command") is { } command) {
                    evidence.Completed(callId, command, background.Str("output"), succeeded: status == "completed" && background.Num("exit_code") == 0 && background.Bool("truncated") != true);
                }
            } else if (raw.Str("type") == "Bash" && raw.Str("command") is { } command) {
                evidence.Completed(callId, command, Text(update.Arr("content")), succeeded: status == "completed" && raw.Num("exit_code") == 0 && raw.Bool("truncated") != true);
            }
        } catch (JsonException) {
            // fail-open: an unreadable line links nothing
        }
    }

    static string? Text(JsonElement? content) {
        if (content is not { } c) return null;

        var text = new StringBuilder();
        foreach (var block in c.EnumerateArray()) {
            if (block.Obj("content")?.Str("text") is { } part) text.Append(part);
        }

        return text.ToString();
    }
}
