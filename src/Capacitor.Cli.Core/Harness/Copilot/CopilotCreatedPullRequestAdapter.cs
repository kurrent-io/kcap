using System.Text.Json;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Harness.Copilot;

/// <summary>
/// The PRs a Copilot session creates: a <c>bash</c> entry in an <c>assistant.message</c>'s <c>toolRequests</c>, paired by
/// <c>toolCallId</c> with its <c>tool.execution_complete</c>. Copilot ends a shell result with
/// <c>&lt;shellId: N completed with exit code E&gt;</c>, which gives the exit status. A command still running when the
/// call returns answers <c>&lt;command with shellId: N is still running …&gt;</c> instead; its output and status arrive
/// later in a <c>read_bash</c> result carrying that same shell id. Subagents write into the session's own file.
/// </summary>
public sealed partial class CopilotCreatedPullRequestAdapter : ICreatedPullRequestAdapter {
    public static CopilotCreatedPullRequestAdapter Instance { get; } = new();

    CopilotCreatedPullRequestAdapter() { }

    public void Observe(string line, CreatedPullRequestEvidence evidence) {
        var request  = line.Contains("\"assistant.message\"", StringComparison.Ordinal) && line.Contains("create", StringComparison.Ordinal);
        var complete = evidence.HasPending && line.Contains("\"tool.execution_complete\"", StringComparison.Ordinal);
        if (!request && !complete) return;

        try {
            using var doc = JsonDocument.Parse(line);

            if (doc.RootElement.Obj("data") is not { } data) return;

            switch (doc.RootElement.Str("type")) {
                case "assistant.message" when data.Arr("toolRequests") is { } requests:
                    foreach (var call in requests.EnumerateArray()) {
                        if (call.Str("name") == "bash" && call.Str("toolCallId") is { Length: > 0 } id && Command(call.Prop("arguments")) is { } command) {
                            evidence.Call(id, command);
                        }
                    }

                    break;
                case "tool.execution_complete" when data.Str("toolCallId") is { Length: > 0 } callId:
                    Complete(callId, data, evidence);
                    break;
            }
        } catch (JsonException) {
            // fail-open: an unreadable line links nothing
        }
    }

    static void Complete(string callId, JsonElement data, CreatedPullRequestEvidence evidence) {
        var result = data.Obj("result");
        var text   = result?.Str("content") ?? result?.Str("detailedContent");
        if (text is null) {
            evidence.Result(callId, null, succeeded: false);
            return;
        }

        if (Running().Match(text) is { Success: true } running) {
            evidence.Continue(callId, ShellKey(running.Groups["shell"].Value));
            return;
        }

        if (Completed().Match(text) is { Success: true } completed) {
            var output    = text[..completed.Index];
            var succeeded = data.Bool("success") != false && completed.Groups["code"].Value == "0";
            evidence.Result(callId, output, succeeded);
            evidence.Result(ShellKey(completed.Groups["shell"].Value), output, succeeded);
            return;
        }

        // The tool reports success whatever the shell's exit; without the trailer nothing says gh succeeded.
        evidence.Result(callId, text, succeeded: false);
    }

    static string ShellKey(string shellId) => "copilot-shell:" + shellId;

    static string? Command(JsonElement? arguments) {
        if (arguments is not { } a) return null;
        if (a.IsObject) return a.Str("command");
        if (!a.IsString || a.GetString() is not { } raw) return null;

        try {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Str("command");
        } catch (JsonException) {
            return null;
        }
    }

    [GeneratedRegex(@"<shellId: (?<shell>\w+) completed with exit code (?<code>-?\d+)>\s*$")]
    private static partial Regex Completed();

    [GeneratedRegex(@"<command with shellId: (?<shell>\w+) is still running[^>]*>\s*$")]
    private static partial Regex Running();
}
