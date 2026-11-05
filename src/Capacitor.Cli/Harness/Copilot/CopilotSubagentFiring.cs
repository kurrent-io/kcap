using Capacitor.Cli.Core.Harness.Copilot;

namespace Capacitor.Cli.Harness.Copilot;

/// <summary>
/// Recognises a hook firing that a subagent made under its own agent id. That id can be a UUID, so
/// its shape does not separate it from a session, and treating it as one starts a second watcher on
/// the parent's transcript, which stores a duplicate session.
/// </summary>
static class CopilotSubagentFiring {
    /// <summary>Takes positive evidence and the lack of a state dir together, because dropping a real
    /// session's firing is the worse mistake. A missing dir alone proves nothing, since sessionStart can
    /// run first, and a resumed or forked session has its own dir whatever else its payload names. For
    /// the same reason a parent id alone is no evidence on sessionStart: a new session's dir may not exist
    /// yet, and dropping that firing loses the whole session.</summary>
    public static bool Is(string eventName, Guid session, string? parentAgentId, string? transcriptPath, CopilotPaths paths) {
        var transcriptDir = string.IsNullOrEmpty(transcriptPath) ? null : Path.GetDirectoryName(transcriptPath);

        var namesAParent = eventName != "sessionStart"
                        && !string.IsNullOrEmpty(parentAgentId)
                        && !(Guid.TryParse(parentAgentId, out var parent) && parent == session);

        return (namesAParent || ReadsAnotherSession(session, transcriptDir, paths))
            && !HasStateDir(session.ToString("D"), transcriptDir, paths);
    }

    // A UUID dir is a session's wherever it sits; any other name counts only directly under a state root.
    static bool ReadsAnotherSession(Guid session, string? transcriptDir, CopilotPaths paths) {
        if (Path.GetFileName(transcriptDir) is not { Length: > 0 } owner) return false;

        return Guid.TryParse(owner, out var id)
            ? id != session
            : paths.SessionStateDirs.Any(root => string.Equals(Path.GetFullPath(root), Path.GetDirectoryName(Path.GetFullPath(transcriptDir!)), StringComparison.Ordinal));
    }

    // Beside the reported transcript too: the hook's COPILOT_HOME can differ from the running Copilot's.
    static bool HasStateDir(string dashedSessionId, string? transcriptDir, CopilotPaths paths) =>
        paths.SessionStateDirs.Any(root => Directory.Exists(Path.Combine(root, dashedSessionId)))
     || (Path.GetDirectoryName(transcriptDir) is { Length: > 0 } sibling && Directory.Exists(Path.Combine(sibling, dashedSessionId)));
}
