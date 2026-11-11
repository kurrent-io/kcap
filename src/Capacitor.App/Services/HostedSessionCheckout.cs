namespace Capacitor.App.Services;

internal sealed record HostedSessionCheckout(
        string? RepoPath, string? WorktreePath, string? WorkLocation, string? BorrowedFrom,
        string? Branch, bool FromLocal, bool BranchFromDaemon) {
    public static HostedSessionCheckout Capture(HostedSessionCheckout? previous, AgentRow row, string? daemonBranch, string? listedBranch) {
        var fromLocal = row.Origin == AgentOrigin.Local;
        if (!fromLocal && previous?.FromLocal == true) return previous;
        var fromDaemon = !string.IsNullOrWhiteSpace(daemonBranch);
        var reported = fromDaemon ? daemonBranch : row.Branch ?? listedBranch;
        var sameCheckout = string.Equals(row.WorktreePath ?? row.RepoPath, previous?.WorktreePath ?? previous?.RepoPath, StringComparison.Ordinal);
        var branch = reported ?? (sameCheckout ? previous?.Branch : null);
        var branchFromDaemon = reported is not null ? fromDaemon : sameCheckout && previous?.BranchFromDaemon == true;
        return new(row.RepoPath, row.WorktreePath, row.WorkLocation, row.BorrowedFrom, branch, fromLocal, branchFromDaemon);
    }

    public AgentRow Apply(AgentRow listing) {
        // A local row's absent worktree means in-place. Only a registry row can borrow the
        // listing's worktree, and paths compare on the daemon's machine rather than this host.
        var listingWorktree = !FromLocal && WorktreePath is null && listing.WorktreePath is { } named
            && (RepoPath is null || SameRemotePath(RepoPath, listing.RepoPath)) ? named : null;
        return listing with {
            RepoPath = RepoPath ?? listing.RepoPath,
            WorktreePath = WorktreePath ?? listingWorktree,
            WorkLocation = WorkLocation,
            BorrowedFrom = BorrowedFrom,
        };
    }

    static bool SameRemotePath(string a, string? b) =>
        b is not null && string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.Ordinal);
}
