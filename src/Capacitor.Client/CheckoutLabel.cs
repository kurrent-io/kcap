using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Client;

/// The checkout a session is presented under, shared by the rail's grouping and the workspace
/// subtitle so the two cannot disagree: the checkout a reviewer borrowed comes first, so a
/// snapshot reviewer sits beside the session it reviews rather than under its private copy.
public static class CheckoutLabel {
    /// Null from an older daemon, whose RepoPath is the checkout.
    public static string? CheckoutPathFor(AgentStatusDto dto) => dto.BorrowedFrom ?? dto.WorktreePath;

    public static bool IsMain(string checkout, string repoRoot) =>
        PlatformPaths.Comparer.Equals(checkout, repoRoot);

    public static string Format(string checkout, string repoRoot) =>
        IsMain(checkout, repoRoot) ? "main checkout" : PlatformPaths.Leaf(checkout);
}
