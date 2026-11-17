using System.Text.Json;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Harness.Kiro;

/// <summary>
/// The PRs a Kiro session creates. A <c>ToolResults</c> record keys <c>data.results</c> by tool-use id, each entry
/// holding the call (<c>tool.kind.BuiltIn.ExecuteCmd.command</c>) and its result
/// (<c>result.Success.items[].Json.{stdout, exit_status}</c>), so no pairing is needed. Kiro reports a command that ran
/// as <c>Success</c> whatever its exit status, which reads <c>"exit status: N"</c>.
/// </summary>
public sealed class KiroCreatedPullRequestAdapter : ICreatedPullRequestAdapter {
    public static KiroCreatedPullRequestAdapter Instance { get; } = new();

    KiroCreatedPullRequestAdapter() { }

    public void Observe(string line, CreatedPullRequestEvidence evidence) {
        if (!line.Contains("\"ToolResults\"", StringComparison.Ordinal) || !line.Contains("\"ExecuteCmd\"", StringComparison.Ordinal)) return;

        try {
            using var doc = JsonDocument.Parse(line);

            if (doc.RootElement.Str("kind") != "ToolResults" || doc.RootElement.Obj("data")?.Obj("results") is not { } results) return;

            foreach (var entry in results.EnumerateObject()) {
                if (entry.Value.Obj("tool")?.Obj("kind")?.Obj("BuiltIn")?.Obj("ExecuteCmd")?.Str("command") is not { } command) continue;
                if (entry.Value.Obj("result")?.Obj("Success")?.Arr("items") is not { } items) continue;

                foreach (var item in items.EnumerateArray()) {
                    if (item.Obj("Json") is not { } run) continue;

                    evidence.Completed(entry.Name, command, run.Str("stdout"), succeeded: run.Str("exit_status") == "exit status: 0");
                }
            }
        } catch (JsonException) {
            // fail-open: an unreadable line links nothing
        }
    }
}
