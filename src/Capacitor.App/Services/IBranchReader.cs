namespace Capacitor.App.Services;

/// Reads branch names for the launcher's "Branch from" picker. Every member answers null rather
/// than throwing when it cannot say.
public interface IBranchReader {
    /// A checkout on this machine: its own branches and origin's as last fetched, without the network.
    Task<BranchListing?> ListLocalAsync(string repoPath, CancellationToken ct);

    /// A repository by its remote URL, asked over the network with this machine's git credentials.
    Task<BranchListing?> ListRemoteAsync(string url, CancellationToken ct);

    /// The origin URL of a checkout on this machine.
    string? OriginUrlOf(string repoPath);
}
