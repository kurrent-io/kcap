using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>What a hook says about a transcript, checked ahead of the daemon reading it. The daemon outlives its agent and
/// reads whatever its user can, and what it reads goes to the server under the session the hook names, so the hook's word
/// is not enough. Links are followed first: one under the projects directory is how a path outside it reads as inside.</summary>
/// <param name="Path">The file with its links followed, which is what to read: a link swapped in under the hook's name
/// later reaches nothing through this.</param>
/// <param name="Project">The Claude project directory it sits in.</param>
internal sealed record HandedTranscript(string Path, string Project) {
    /// <summary>Where <paramref name="path"/> is, as a transcript of <paramref name="session"/> or of its
    /// <paramref name="subagent"/> where Claude files one. Null when it isn't one.</summary>
    public static HandedTranscript? Locate(string path, string session, string? subagent, string projects) {
        if (!System.IO.Path.IsPathFullyQualified(path) || System.IO.Path.GetExtension(path) != ".jsonl") return null;
        if (!CanonicalPath.TryResolve(path, out var resolved) || !CanonicalPath.TryResolve(projects, out var root)) return null;

        var named = subagent is null ? resolved : ClaudeSubagentFiles.SessionDirectoryOf(resolved, subagent);
        if (named is null || System.IO.Path.GetExtension(resolved) != ".jsonl") return null;

        var name    = subagent is null ? System.IO.Path.GetFileNameWithoutExtension(named) : System.IO.Path.GetFileName(named);
        var project = System.IO.Path.GetDirectoryName(named);

        return SessionIds.Canonical(name) == session && System.IO.Path.GetDirectoryName(project) is { } parent && PathComparison.Equal(parent, root)
            ? new HandedTranscript(resolved, project!)
            : null;
    }
}
