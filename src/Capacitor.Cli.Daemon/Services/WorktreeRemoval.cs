namespace Capacitor.Cli.Daemon.Services;

/// <summary>What <see cref="WorktreeManager.RemoveAsync"/> did: removed the worktree, or kept it and
/// its branch because they held work that exists nowhere else. <see cref="TreeRemoved"/> says whether the
/// worktree's directory went, true even when cleanup stopped after removing it.</summary>
public sealed record WorktreeRemoval(string? KeptBecause, bool TreeRemoved) {
    public static readonly WorktreeRemoval Removed = new(null, true);

    public static WorktreeRemoval Keep(string reason) => new(reason, false);

    /// <summary>The worktree went, but cleanup stopped before its branch or fetched ref.</summary>
    public static WorktreeRemoval KeepAfterRemoving(string reason) => new(reason, true);

    public bool Kept => KeptBecause is not null;
}
