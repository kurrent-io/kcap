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
    /// run first, and a resumed or forked session has its own dir whatever else its payload names.</summary>
    public static bool Is(Guid session, string? parentAgentId, string? transcriptPath, CopilotPaths paths) {
        var transcriptDir = string.IsNullOrEmpty(transcriptPath) ? null : Path.GetDirectoryName(transcriptPath);

        var namesAParent        = !string.IsNullOrEmpty(parentAgentId) && !(Guid.TryParse(parentAgentId, out var parent) && parent == session);
        var readsAnotherSession = Guid.TryParse(Path.GetFileName(transcriptDir), out var owner) && owner != session;

        return (namesAParent || readsAnotherSession) && !HasStateDir(session.ToString("D"), transcriptDir, paths);
    }

    // Beside the reported transcript too: the hook's COPILOT_HOME can differ from the running Copilot's.
    static bool HasStateDir(string dashedSessionId, string? transcriptDir, CopilotPaths paths) =>
        paths.SessionStateDirs.Any(root => Directory.Exists(Path.Combine(root, dashedSessionId)))
     || (Path.GetDirectoryName(transcriptDir) is { Length: > 0 } sibling && Directory.Exists(Path.Combine(sibling, dashedSessionId)));
}
