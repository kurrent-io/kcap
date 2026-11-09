namespace Capacitor.Cli.Core.Workspace;

/// <summary>How much one snapshot and one span may read. Claude's turn hooks run under a 5 s timeout, so
/// every bound holds before anything is computed; past any of them the result is marked truncated.</summary>
public sealed record WorkspaceBounds {
    public static WorkspaceBounds Default { get; } = new();

    public int StatusEntries { get; init; } = 2_000;

    public int HashedPaths { get; init; } = 1_000;

    public long HashedBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>A file over this counts as changed when its size or mtime moved, without being read.</summary>
    public long FileBytes { get; init; } = 16L * 1024 * 1024;

    public TimeSpan SnapshotBudget { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Reflog and stash entries read per span, together.</summary>
    public int HistoryEntries { get; init; } = 50;

    public int EventBytes { get; init; } = 1024 * 1024;
}
