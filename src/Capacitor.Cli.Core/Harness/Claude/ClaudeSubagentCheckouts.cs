using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Harness.Claude;

/// <summary>
/// Feeds the checkouts a session's subagents mutate into the session watcher's secondary roots, so
/// a PR on a branch a subagent worked on links to the session. The parent probes each checkout once
/// however many subagents worked in it; a subagent watcher probes none. Each transcript is read from
/// where the previous pass stopped.
/// </summary>
public sealed class ClaudeSubagentCheckouts(string parentTranscriptPath) {
    readonly Dictionary<string, JsonlTail> _tails = new(StringComparer.Ordinal);

    public void FeedInto(SecondaryRepoRoots roots) {
        foreach (var path in ClaudeSubagentFiles.Transcripts(parentTranscriptPath)) {
            if (roots.IsFull) return;

            if (!_tails.TryGetValue(path, out var tail)) _tails[path] = tail = new JsonlTail(path);

            foreach (var line in tail.ReadAppended().Lines) roots.OnLine("claude", line);
        }
    }
}
