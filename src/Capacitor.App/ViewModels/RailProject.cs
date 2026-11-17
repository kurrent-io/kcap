using Capacitor.App.Services;
using Capacitor.Cli.Core;

namespace Capacitor.App.ViewModels;

/// A project the rail can narrow to: its sessions are the rows whose repository is one of its repos.
/// Value-equal on the repo hashes as a sequence, so a persisted copy compares equal to a fresh read.
public sealed record RailProject(string Id, string Name, IReadOnlyList<string> RepoHashes) {
    public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";

    public bool Holds(AgentRow row) => RepoHashOf(row) is { } hash && RepoHashes.Contains(hash, StringComparer.Ordinal);

    /// A daemon row names its repository only by owner and name, which hash the way the server does;
    /// one with no remote has an owner only the server can hash, so it belongs to no project.
    internal static string? RepoHashOf(AgentRow row) =>
        row.RepoHash ?? (row.RepoGroupKey.StartsWith("repo:", StringComparison.Ordinal)
            && RepoHashHelper.TryParseRepoRef(row.RepoGroupKey["repo:".Length..], out var hash) ? hash : null);

    public bool Equals(RailProject? other) =>
        other is not null && Id == other.Id && Name == other.Name && RepoHashes.SequenceEqual(other.RepoHashes, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Id, Name, RepoHashes.Count);
}
