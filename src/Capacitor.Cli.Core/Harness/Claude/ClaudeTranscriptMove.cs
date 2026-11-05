namespace Capacitor.Cli.Core.Harness.Claude;

/// Claude Code files a transcript under a projects folder named after the session's working directory. When a
/// running session switches directory (EnterWorktree), it moves the transcript, and its subagents' transcripts,
/// to the folder named after the new one and deletes the old file.
public static class ClaudeTranscriptMove {
    /// Where the transcript that was at <paramref name="lostPath"/> lives now: the same file under another
    /// project folder beside its own, the most recently written if there are several. Null when there is none.
    public static string? Find(string lostPath) {
        var full = Path.GetFullPath(lostPath);
        if (ProjectFolderOf(full) is not { } project || Path.GetDirectoryName(project) is not { } projects) return null;

        var      withinProject = Path.GetRelativePath(project, full);
        string?  found         = null;
        DateTime newest        = default;

        try {
            foreach (var folder in Directory.EnumerateDirectories(projects)) {
                var candidate = new FileInfo(Path.Combine(folder, withinProject));
                if (!candidate.Exists || (found is not null && candidate.LastWriteTimeUtc <= newest)) continue;

                found  = candidate.FullName;
                newest = candidate.LastWriteTimeUtc;
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return null;
        }

        return found;
    }

    // A session's `<project>/<session>.jsonl`, or its subagent's `<project>/<session>/subagents/agent-<id>.jsonl`.
    static string? ProjectFolderOf(string transcript) {
        var folder = Path.GetDirectoryName(transcript);
        if (folder is not null && Path.GetFileName(folder) == ClaudeSubagentFiles.DirectoryName)
            folder = Path.GetDirectoryName(Path.GetDirectoryName(folder));

        return folder;
    }
}
