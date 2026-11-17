using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Harness.Pi;

/// <summary>
/// The PRs a Pi session creates: an assistant <c>toolCall</c> to <c>bash</c> paired with its <c>toolResult</c> by call
/// id, and the user's own <c>!</c> shell, a <c>bashExecution</c> entry carrying its command, output and exit code.
/// A subagent's run has no file of its own: its messages arrive inside the <c>subagent</c> tool's result, under
/// <c>details.results[].messages</c>, and are read the same way.
/// </summary>
public sealed class PiCreatedPullRequestAdapter : ICreatedPullRequestAdapter {
    public static PiCreatedPullRequestAdapter Instance { get; } = new();

    PiCreatedPullRequestAdapter() { }

    public void Observe(string line, CreatedPullRequestEvidence evidence) {
        var call    = line.Contains("\"toolCall\"", StringComparison.Ordinal);
        var result  = evidence.HasPending && line.Contains("\"toolResult\"", StringComparison.Ordinal);
        var userRun = line.Contains("\"bashExecution\"", StringComparison.Ordinal);
        if (!call && !result && !userRun) return;

        try {
            using var doc = JsonDocument.Parse(line);

            if (doc.RootElement.Str("type") == "message" && doc.RootElement.Obj("message") is { } message) Message(message, evidence);
        } catch (JsonException) {
            // fail-open: an unreadable line links nothing
        }
    }

    static void Message(JsonElement message, CreatedPullRequestEvidence evidence) {
        switch (message.Str("role")) {
            case "assistant" when message.Arr("content") is { } content:
                foreach (var block in content.EnumerateArray()) {
                    if (block.Str("type") == "toolCall" && block.Str("name") == "bash"
                     && block.Str("id") is { Length: > 0 } id && block.Obj("arguments")?.Str("command") is { } command) {
                        evidence.Call(id, command);
                    }
                }

                break;
            case "toolResult":
                if (message.Obj("details")?.Arr("results") is { } runs) {
                    foreach (var run in runs.EnumerateArray()) {
                        if (run.Arr("messages") is not { } messages) continue;

                        foreach (var child in messages.EnumerateArray()) Message(child, evidence);
                    }
                }

                if (message.Str("toolCallId") is { Length: > 0 } callId) {
                    evidence.Result(callId, Text(message.Prop("content")), succeeded: message.Bool("isError") == false);
                }

                break;
            case "bashExecution" when message.Str("command") is { } command:
                evidence.Completed(null, command, message.Str("output"), succeeded: message.Num("exitCode") == 0);
                break;
        }
    }

    static string? Text(JsonElement? content) {
        if (content is not { } c) return null;
        if (c.IsString) return c.GetString();
        if (!c.IsArray) return null;

        var text = new StringBuilder();
        foreach (var block in c.EnumerateArray()) {
            if (block.Str("type") == "text" && block.Str("text") is { } part) text.Append(part).Append('\n');
        }

        return text.ToString();
    }
}
