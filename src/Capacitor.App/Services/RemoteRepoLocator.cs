using System.Text.RegularExpressions;
using Capacitor.Cli.Core.Config;

namespace Capacitor.App.Services;

/// Where a repository on another machine is hosted. The machine reports only its folder paths,
/// so the repository is learned from a session that ran in that folder there.
public static partial class RemoteRepoLocator {
    const string RepoKeyPrefix = "repo:";

    /// "owner/name" from the newest row that ran under <paramref name="repoPath"/> on
    /// <paramref name="machine"/>, or null when none names a repository.
    public static string? OwnerRepoOn(IEnumerable<AgentRow> rows, string machine, string? viewerId, string repoPath) {
        if (repoPath.Length == 0) return null;
        return rows
            .Where(r => r.RepoGroupKey.StartsWith(RepoKeyPrefix, StringComparison.Ordinal)
                && (r.MachineBadge == machine || r.Recording?.DaemonName == machine)
                && (viewerId is null || r.OwnerUserId is null || r.OwnerUserId == viewerId)
                && r.RepoPath is { } path && IsUnder(path, repoPath))
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => r.RepoGroupLabel)
            .FirstOrDefault(IsOwnerRepo);
    }

    /// The origin URL of a local clone of the same repository, whose host and transport are the
    /// ones this machine's credentials already work with; otherwise the repository on GitHub.
    public static string UrlFor(string ownerRepo, IEnumerable<string?> localOriginUrls) {
        foreach (var url in localOriginUrls) {
            if (url is null || url.StartsWith('-')) continue;
            if (RemoteMatcher.NormalizeRemoteUrl(url) is { } normalized
                && string.Equals(RemoteMatcher.PathAfterHost(normalized), ownerRepo, StringComparison.OrdinalIgnoreCase))
                return url;
        }
        return $"https://github.com/{ownerRepo}";
    }

    static bool IsUnder(string path, string root) =>
        path.StartsWith(root, StringComparison.Ordinal)
        && (path.Length == root.Length || path[root.Length] is '/' or '\\' || root[^1] is '/' or '\\');

    static bool IsOwnerRepo(string label) => OwnerRepoRegex().IsMatch(label) && !label.Split('/').Any(s => s is "." or "..");

    [GeneratedRegex(@"^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)+$")]
    private static partial Regex OwnerRepoRegex();
}
