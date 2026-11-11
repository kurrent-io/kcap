using System.Text.Json.Nodes;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.Cursor;

/// <summary>
/// Complete transcripts establish prompt-hash parent correlation; a child does not emit sessionStart,
/// and the parent's invocation record can remain unflushed throughout the child's live hook window.
/// <see cref="CursorHookCommand"/> therefore cannot produce marker writes, but reads must retain decisions from
/// other surfaces or older builds. Preserve dashless ids shared with import so both paths converge
/// on the same parent/child stream. Native integration must be driven by parent subagent events.
/// </summary>
public static class CursorLiveSubagentLinker {
    // Bounds the sibling-transcript scan so a workspace with a very long history can't blow
    // the hook dispatcher's ~2s wall-clock budget (CursorHookCommand.DispatcherBudget).
    const int MaxCandidates = 500;

    static string MarkerDir(ConfigRoot config) => config.Path("cursor-subagent-links");

    /// <summary>
    /// The parent's tool-use record can remain unflushed throughout the child's hook window, so live
    /// correlation cannot rely on waiting briefly for it. Complete-transcript import and server adoption
    /// establish nesting; ambiguous matches across parents must remain unattributed.
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
    /// Persisted decisions keep later hook processes from changing an already acted-on routing choice.
    /// Read markers even without a live producer: another surface or an older build can leave a marker
    /// that must still activate the subagent divert.
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
            // docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md
            //It has no producer on the measured cursor-agent contract, because the
            // only caller sits behind a guard that never opens there.
        }
    }

    /// <summary>
    /// Mirrors the shape of <c>CursorImportSource.SendSubagentLifecycleAsync</c>'s
    /// <c>subagent-start</c> POST (session_id=parent, agent_id=child) so live and import
    /// converge on the same <c>AgentSubsession-{parent}-{child}</c> stream.
    /// </summary>
    internal static JsonObject BuildSubagentStartPayload(
        string parentSessionId, string childSessionId, string subagentType, string transcriptPath
    ) => new() {
        ["hook_event_name"] = "subagent_start",
        ["session_id"]      = parentSessionId,
        ["agent_id"]        = childSessionId,
        ["agent_type"]      = subagentType,
        ["transcript_path"] = transcriptPath, // required by HookBase
        ["cwd"]             = "",             // required by HookBase
        ["strict"]          = true,           // fail-closed: 500 if SubagentStarted isn't persisted
    };

    /// <summary>
    /// Mirrors the shape of <c>CursorImportSource.SendSubagentLifecycleAsync</c>'s
    /// <c>subagent-stop</c> POST.
    /// </summary>
    internal static JsonObject BuildSubagentStopPayload(
        string parentSessionId, string childSessionId, string subagentType, string transcriptPath
    ) => new() {
        ["hook_event_name"]        = "subagent_stop",
        ["session_id"]             = parentSessionId,
        ["agent_id"]               = childSessionId,
        ["agent_type"]             = subagentType,
        ["transcript_path"]        = transcriptPath, // required by HookBase
        ["cwd"]                    = "",              // required by HookBase
        ["stop_hook_active"]       = false,
        ["agent_transcript_path"]  = transcriptPath,
        ["last_assistant_message"] = "",
        ["strict"]                 = true,            // fail-closed: 500 if SubagentCompleted isn't persisted
    };
}
