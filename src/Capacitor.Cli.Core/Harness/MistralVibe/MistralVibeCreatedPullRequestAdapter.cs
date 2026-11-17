using System.Text.Json;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Harness.MistralVibe;

/// <summary>
/// The PRs a Mistral Vibe session creates, from either of its stores. In the unified store a settled <c>effect</c>
/// entry is self-contained: <c>detail.input.command</c>, and <c>state.output.structured_content</c> with the shell's
/// <c>stdout</c> and <c>exit_code</c> (<c>returncode</c> in older builds). Vibe returns a non-zero exit as a result,
/// not an error, so the exit code decides, and an effect without one links nothing. In legacy
/// <c>messages.jsonl</c> an assistant's <c>tool_calls[]</c> pairs by id with a <c>role:"tool"</c> message whose content
/// is the result as <c>key: value</c> lines. Subagents are read by a child watcher with this same adapter.
/// </summary>
public sealed partial class MistralVibeCreatedPullRequestAdapter : ICreatedPullRequestAdapter {
    public static MistralVibeCreatedPullRequestAdapter Instance { get; } = new();

    MistralVibeCreatedPullRequestAdapter() { }

    public void Observe(string line, CreatedPullRequestEvidence evidence) {
        var effect = line.Contains("\"effect\"", StringComparison.Ordinal) && line.Contains("bash", StringComparison.Ordinal);
        var call   = line.Contains("\"tool_calls\"", StringComparison.Ordinal);
        var result = evidence.HasPending && line.Contains("\"tool\"", StringComparison.Ordinal);
        if (!effect && !call && !result) return;

        try {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.Str("type") == "effect") {
                Effect(root, evidence);
                return;
            }

            switch (root.Str("role")) {
                case "assistant" when root.Arr("tool_calls") is { } calls:
                    foreach (var tc in calls.EnumerateArray()) {
                        if (tc.Str("id") is not { Length: > 0 } id || tc.Obj("function") is not { } function || !IsBash(function.Str("name"))) continue;
                        if (Command(function.Prop("arguments")) is { } command) evidence.Call(id, command);
                    }

                    break;
                case "tool" when root.Str("tool_call_id") is { Length: > 0 } callId:
                    var (stdout, exitCode, truncated) = Fields(root.Str("content") ?? "");
                    evidence.Result(callId, stdout, succeeded: exitCode == 0 && !truncated);
                    break;
            }
        } catch (JsonException) {
            // fail-open: an unreadable line links nothing
        }
    }

    static void Effect(JsonElement entry, CreatedPullRequestEvidence evidence) {
        if (entry.Obj("detail") is not { } detail || !IsBash(detail.Str("toolName")) || detail.Obj("input")?.Str("command") is not { } command) return;
        if (entry.Obj("state") is not { } state || state.Str("status") != "completed") return;
        if (state.Obj("output")?.Obj("structured_content") is not { } shell) return;

        var exitCode = shell.Num("exit_code") ?? shell.Num("returncode");
        evidence.Completed(entry.Str("id"), command, shell.Str("stdout"), succeeded: exitCode == 0 && shell.Bool("was_truncated") != true);
    }

    static bool IsBash(string? toolName) => toolName is "bash" || toolName?.EndsWith(".bash", StringComparison.Ordinal) == true;

    static string? Command(JsonElement? arguments) {
        if (arguments is not { } a) return null;
        if (a.IsObject) return a.Str("command") ?? a.Str("cmd");
        if (!a.IsString || a.GetString() is not { } raw) return null;

        try {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Str("command") ?? doc.RootElement.Str("cmd");
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>The <c>stdout</c> block, the exit code and whether output was truncated, out of a legacy result's
    /// <c>key: value</c> lines. A block runs until the next known key.</summary>
    static (string? Stdout, int? ExitCode, bool Truncated) Fields(string content) {
        string? stdout    = null;
        int?    exitCode  = null;
        var     truncated = false;
        string? key      = null;
        var     block    = new List<string>();

        foreach (var line in content.Split('\n').Append("end:")) {
            if (Field().Match(line) is not { Success: true } field) {
                block.Add(line);
                continue;
            }

            if (key == "stdout") stdout = string.Join('\n', block);
            if (key is "returncode" or "exit_code" && int.TryParse(string.Join('\n', block).Trim(), out var code)) exitCode = code;
            if (key == "was_truncated") truncated = !string.Equals(string.Join('\n', block).Trim(), "false", StringComparison.OrdinalIgnoreCase);

            key = field.Groups["key"].Value;
            block.Clear();
            block.Add(field.Groups["value"].Value);
        }

        return (stdout, exitCode, truncated);
    }

    [GeneratedRegex(@"^(?<key>command|shell|stdout|stderr|returncode|exit_code|was_truncated|end): ?(?<value>.*)$")]
    private static partial Regex Field();
}
