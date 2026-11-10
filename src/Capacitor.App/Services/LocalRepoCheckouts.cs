using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// <summary>Finds this machine's checkout of a repository the server names by its hash, among the repositories the
/// launcher knows. Each checkout's origin is read once and kept.</summary>
public sealed class LocalRepoCheckouts(Func<Task<string[]>> knownRepos, RepoIdentityResolver identities) {
    public async Task<string?> FindAsync(string repoHash) {
        var paths = await knownRepos().ConfigureAwait(false);
        return await Task.Run(() => paths.FirstOrDefault(path => HashOf(path) == repoHash)).ConfigureAwait(false);
    }

    string? HashOf(string path) {
        var key = identities.ForLocalRoot(path).Key;
        if (!key.StartsWith("repo:", StringComparison.Ordinal)) return null;
        var ownerRepo = key["repo:".Length..];
        var slash = ownerRepo.LastIndexOf('/');
        return slash <= 0 ? null : RepoHashHelper.ComputeRepoHash(ownerRepo[..slash], ownerRepo[(slash + 1)..]);
    }
}
