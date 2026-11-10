namespace Capacitor.Cli.Recording;

/// <summary>Which child rollouts have moved since a scan last looked.</summary>
internal static class ChildRolloutMtimes {
    /// <summary>
    /// Fires on first sight of a rollout and thereafter only when its mtime advances, recording the mtime it
    /// fired for, so a child reaped at the codex-child ceiling stays reaped while its rollout is quiet but is
    /// re-ensured the moment its subagent re-engages (the fresh watcher resumes from the server frontier).
    /// </summary>
    public static bool Advanced(Dictionary<string, DateTime> lastMtimes, string filePath, DateTime currentMtime) {
        if (lastMtimes.TryGetValue(filePath, out var prev) && prev == currentMtime) return false;

        lastMtimes[filePath] = currentMtime;

        return true;
    }
}
