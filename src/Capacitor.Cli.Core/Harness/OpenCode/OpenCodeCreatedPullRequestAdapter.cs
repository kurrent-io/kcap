using System.Text.Json;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Harness.OpenCode;

/// <summary>
/// The PRs an OpenCode session creates. kcap's plugin writes each message as <c>{info, parts}</c>, and a settled
/// <c>bash</c> tool part carries the command (<c>state.input.command</c>), its output (<c>state.output</c>) and its exit
/// code (<c>state.metadata.exit</c>); a pending or running snapshot carries no result yet. OpenCode settles a command
/// that exited non-zero as <c>completed</c>, so the exit code decides. Truncated output may have lost its true last
/// line, so it links nothing. Subagents are child sessions, each read by its own child watcher.
/// </summary>
public sealed class OpenCodeCreatedPullRequestAdapter : ICreatedPullRequestAdapter {
    public static OpenCodeCreatedPullRequestAdapter Instance { get; } = new();

    OpenCodeCreatedPullRequestAdapter() { }

    public void Observe(string line, CreatedPullRequestEvidence evidence) {
        if (!line.Contains("\"bash\"", StringComparison.Ordinal) || !line.Contains("\"parts\"", StringComparison.Ordinal)) return;

        try {
            using var doc = JsonDocument.Parse(line);

            if (doc.RootElement.Arr("parts") is not { } parts) return;

            foreach (var part in parts.EnumerateArray()) {
                if (part.Str("type") != "tool" || part.Str("tool") != "bash" || part.Obj("state") is not { } state) continue;
                if (state.Str("status") is not ("completed" or "error") || state.Obj("input")?.Str("command") is not { } command) continue;

                evidence.Completed(part.Str("callID"), command, state.Str("output"),
                    succeeded: state.Str("status") == "completed" && state.Obj("metadata") is { } metadata && metadata.Num("exit") == 0 && metadata.Bool("truncated") != true);
            }
        } catch (JsonException) {
            // fail-open: an unreadable line links nothing
        }
    }
}
