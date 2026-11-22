using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.Cursor;

/// <summary>
/// Live-flow wrapper over <see cref="CursorSubagentCorrelator"/>, using the same prompt-hash matching
/// and session ids as import so a live-then-import session converges on one
/// <c>AgentSubsession-{parent}-{child}</c> stream.
/// <para>Nothing writes a marker: the writer requires a <c>sessionStart</c> with a
/// <c>transcript_path</c>, and Cursor sends neither for a subagent. <see cref="TryLoadLink"/> still runs
/// on every event, so a marker persisted elsewhere is consumed. Subagent nesting is done by
/// <c>kcap import --cursor</c> and the server's adoption sweep over complete transcripts.</para>
/// <para>Kept for Cursor's native <c>subagentStart</c> hook, which carries a parent id. Wiring it means
/// triggering from the parent's subagentStart/subagentStop, not the child's lifecycle, and adding the
/// event to CursorHooksParser.CursorHookEvents and CursorHookEventMap.</para>
/// </summary>
public static class CursorLiveSubagentLinker {
    // Bounds the sibling-transcript scan so a workspace with a very long history can't blow
    // the hook dispatcher's ~2s wall-clock budget (CursorHookCommand.DispatcherBudget).
    const int MaxCandidates = 500;

    static string MarkerDir(ConfigRoot config) => config.Path("cursor-subagent-links");

    /// <summary>
    /// Resolves <paramref name="childSessionId"/> to a parent by running
    /// <see cref="CursorSubagentCorrelator.Correlate"/> over the child plus
    /// <paramref name="candidateParents"/> (the other Cursor session transcripts discovered
    /// on disk), returning the link for the child if one was found (null otherwise —
    /// including when the correlator finds the match ambiguous across two distinct
    /// parents, which it already refuses to attribute).
    ///
    /// <para>
    /// NOT AN EVENTUAL-CONSISTENCY RACE. Measurement
    /// shows the parent's <c>Task</c>/<c>Agent</c> tool_use stays unflushed for the WHOLE of the
    /// child's hook window and lands only after the child's final hook, so correlation here is
    /// not merely often unavailable, it is never available live. (The child's own side does
    /// appear partway through; the parent's does not.) Correlation succeeds only over complete
    /// transcripts — i.e. on the <c>kcap import --cursor</c> path plus the server-side adoption
    /// sweep, which is where subagent nesting actually happens.
    /// </para>
    /// </summary>
    public static CursorSubagentCorrelator.SubagentLink? ResolveParent(
        string                                                   childSessionId,
        string                                                   childTranscriptPath,
        IEnumerable<(string SessionId, string TranscriptPath)>   candidateParents
    ) {
        var sessions = new List<(string SessionId, string TranscriptPath)> {
            (childSessionId, childTranscriptPath),
        };
        sessions.AddRange(candidateParents);

        var links = CursorSubagentCorrelator.Correlate(sessions);
        return links.TryGetValue(childSessionId, out var link) ? link : null;
    }

    /// <summary>
    /// Enumerates sibling session transcripts under the same Cursor
    /// <c>agent-transcripts/&lt;sid&gt;/&lt;sid&gt;.jsonl</c> workspace directory as
    /// <paramref name="transcriptPath"/> — the bounded candidate-parent pool fed to
    /// <see cref="ResolveParent"/>. Each sibling's id is dashless (matching the convention
    /// used everywhere else on both the live and import paths). Fail-open: a missing or
    /// unreadable directory yields no candidates rather than throwing.
    /// </summary>
    public static IReadOnlyList<(string SessionId, string TranscriptPath)> DiscoverSiblingTranscripts(
        string transcriptPath
    ) {
        try {
            var sessionDir = Path.GetDirectoryName(transcriptPath);
            if (string.IsNullOrEmpty(sessionDir)) return [];

            var transcriptsRoot = Path.GetDirectoryName(sessionDir);
            if (string.IsNullOrEmpty(transcriptsRoot) || !Directory.Exists(transcriptsRoot)) return [];

            var results = new List<(string SessionId, string TranscriptPath)>();
            foreach (var dir in Directory.EnumerateDirectories(transcriptsRoot)) {
                if (string.Equals(dir, sessionDir, StringComparison.Ordinal)) continue;

                var name  = Path.GetFileName(dir);
                var jsonl = Path.Combine(dir, name + ".jsonl");
                if (!File.Exists(jsonl)) continue;

                results.Add((CursorImportSource.NormalizeCursorSessionId(name), jsonl));
                if (results.Count >= MaxCandidates) break;
            }
            return results;
        } catch {
            return []; // Fail-open: a locked/unreadable directory must not abort the hook.
        }
    }

    public readonly record struct LinkMarker(string ParentSessionId, string SubagentType);

    /// <summary>
    /// Loads a previously-persisted link decision for <paramref name="childSessionId"/>, if any.
    /// <see cref="CursorHookCommand"/> is a fresh process per hook invocation, so the decision is
    /// written to a small on-disk marker that every later hook call for the same session can
    /// consult without re-running the correlator — and, more importantly, without risking a
    /// different answer once the top-level-vs-subagent choice has already been acted on.
    ///
    /// <para>
    /// THIS METHOD IS LIVE even though <see cref="SaveLink"/> has no producer: it runs on EVERY
    /// event, so a marker persisted by another surface or an older build is still consumed and
    /// still activates the divert. It is the only one of the three durable artifacts that
    /// affects classification.
    /// </para>
    /// </summary>
    public static LinkMarker? TryLoadLink(ConfigRoot config, string childSessionId) {
        try {
            var path = Path.Combine(MarkerDir(config), childSessionId);
            if (!File.Exists(path)) return null;

            var lines = File.ReadAllLines(path);
            return lines.Length >= 2 && !string.IsNullOrEmpty(lines[0])
                ? new LinkMarker(lines[0], lines[1])
                : null;
        } catch {
            return null; // Fail-open: treat an unreadable marker as "not linked".
        }
    }

    /// <summary>
    /// Persists a link decision. NO PRODUCER today — its only caller sits behind a guard that
    /// requires both a <c>sessionStart</c> event and a non-empty <c>transcript_path</c>, and a
    /// Cursor <c>sessionStart</c> never carries one. Note it also returns void and swallows
    /// write failures; see the catch below for why that matters to the caller.
    /// </summary>
    public static void SaveLink(ConfigRoot config, string childSessionId, string parentSessionId, string subagentType) {
        try {
            Directory.CreateDirectory(MarkerDir(config));
            File.WriteAllLines(Path.Combine(MarkerDir(config), childSessionId), [parentSessionId, subagentType]);
        } catch {
            // Fail-open, but the consequence depends on WHEN the write failed, and the
            // optimistic reading is only half the story:
            //
            //  - Failure with no start side effect yet: later hooks miss TryLoadLink and treat
            //    the child as top-level. Recovered by a later `kcap import --cursor`.
            //  - Failure followed by a start POST or spool: the caller assigned
            //    subagentParentId BEFORE calling this method, so the divert still runs. A
            //    successful start marks the ack and spawns the {parent}-{child} watcher; a
            //    failed one spools an entry whose later drain does the same. Either way the
            //    child transcript can be routed BOTH under the parent and as its own
            //    top-level session — duplication, not a graceful fallback.
            //
            // A known, accepted corrupt-state risk; remedies are recorded in
            // docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md.
            // It has no producer on the measured cursor-agent contract, because the
            // only caller sits behind a guard that never opens there.
        }
    }
}
