namespace Capacitor.App.Services;

/// A repository's branch names, sorted, and the one its origin's HEAD names when that is known.
public sealed record BranchListing(string? DefaultBranch, IReadOnlyList<string> Branches);
