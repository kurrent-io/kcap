using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>What a hook says about a transcript, checked ahead of the daemon reading it. The daemon outlives its agent and
/// reads whatever its user can, and what it reads goes to the server under the session the hook names, so the hook's word
/// is not enough. Links are followed first: one under the projects directory is how a path outside it reads as inside.</summary>
internal static class HandedTranscript {
    /// <summary>The project directory <paramref name="path"/> sits in as a transcript of <paramref name="session"/>, or of
    /// its <paramref name="subagent"/>, as Claude files one. Null when it isn't one.</summary>
    public static string? ProjectOf(string path, string session, string? subagent, string projects) {
        if (!Path.IsPathFullyQualified(path) || Path.GetExtension(path) != ".jsonl") return null;
        if (!CanonicalPath.TryResolve(path, out var resolved) || !CanonicalPath.TryResolve(projects, out var root)) return null;

        var named = subagent is null ? resolved : ClaudeSubagentFiles.SessionDirectoryOf(resolved, subagent);
        if (named is null || Path.GetExtension(resolved) != ".jsonl") return null;

        var name    = subagent is null ? Path.GetFileNameWithoutExtension(named) : Path.GetFileName(named);
        var project = Path.GetDirectoryName(named);

        return SessionIds.Canonical(name) == session && Path.GetDirectoryName(project) is { } parent && PathComparison.Equal(parent, root) ? project : null;
    }
}
