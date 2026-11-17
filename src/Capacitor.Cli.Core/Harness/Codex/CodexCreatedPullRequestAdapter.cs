using System.Text.Json;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>
/// The PRs a Codex session creates, read from the records that carry a finished shell command whole: an
/// <c>exec_command_end</c> event, and an <c>item_completed</c> event whose item is a <c>CommandExecution</c>. Each holds
/// the command, its exit code and its aggregated output. The <c>function_call_output</c> is not used: a command still
/// running when its yield time lapses answers "Process running with session ID …", and its URL arrives in a later,
/// unrelated <c>write_stdin</c> call's output. The GitHub connector's <c>_create_pull_request</c> is not read: no
/// successful result has been seen to fix its shape.
/// </summary>
public sealed class CodexCreatedPullRequestAdapter : ICreatedPullRequestAdapter {
    public static CodexCreatedPullRequestAdapter Instance { get; } = new();

    CodexCreatedPullRequestAdapter() { }

    public void Observe(string line, CreatedPullRequestEvidence evidence) {
        if (!line.Contains("\"event_msg\"", StringComparison.Ordinal)
         || !line.Contains("\"exec_command_end\"", StringComparison.Ordinal) && !line.Contains("\"CommandExecution\"", StringComparison.Ordinal)) {
            return;
        }

        try {
            using var doc = JsonDocument.Parse(line);

            if (doc.RootElement.Str("type") != "event_msg" || doc.RootElement.Obj("payload") is not { } payload) return;

            var run = payload.Str("type") switch {
                "exec_command_end"                                                       => payload,
                "item_completed" when payload.Obj("item") is { } item
                                   && item.Str("type") == "CommandExecution"             => item,
                _                                                                        => (JsonElement?)null,
            };

            if (run is not { } r || Command(r.Prop("command")) is not { } command) return;

            var callId = r.Str("call_id") ?? r.Str("id");
            evidence.Completed(callId, command, r.Str("aggregated_output") ?? r.Str("stdout"), succeeded: r.Num("exit_code") == 0);
        } catch (JsonException) {
            // fail-open: an unreadable line links nothing
        }
    }

    /// <summary>The command as a shell line. Codex records an argv, normally <c>[shell, -lc, script]</c>; each word is
    /// single-quoted so the matcher reads exactly that argv, and finds the script through its <c>-c</c>.</summary>
    static string? Command(JsonElement? command) {
        if (command is not { } c) return null;
        if (c.IsString) return c.GetString();
        if (!c.IsArray) return null;

        var argv = c.EnumerateArray().Where(a => a.IsString).Select(a => "'" + a.GetString()!.Replace("'", "'\\''") + "'").ToArray();
        return argv.Length == 0 ? null : string.Join(' ', argv);
    }
}
