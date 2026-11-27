using System.Collections.Concurrent;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core;

/// <summary>Finds this machine's checkout of a repository the server names by its hash, among the repositories this
/// machine knows. Each checkout's origin is read once and kept.</summary>
public sealed class LocalRepoCheckouts(Func<Task<string[]>> knownRepos, Func<string, string?>? readOriginUrl = null) {
    readonly Func<string, string?> _readOriginUrl = readOriginUrl ?? GitRemoteReader.ReadOriginUrl;
    readonly ConcurrentDictionary<string, string?> _hashes = new(StringComparer.Ordinal);

    public Task<string?> FindAsync(string repoHash) => FindAsync(repoHash, CancellationToken.None);

    public async Task<string?> FindAsync(string repoHash, CancellationToken ct) {
        var paths = await knownRepos().WaitAsync(ct).ConfigureAwait(false);
        return await Task.Run(() => paths.FirstOrDefault(path => _hashes.GetOrAdd(path, HashOf) == repoHash), ct).WaitAsync(ct).ConfigureAwait(false);
    }

    string? HashOf(string path) {
        var url = path.Length > 0 ? _readOriginUrl(path) : null;
        if (url is null || RemoteMatcher.NormalizeRemoteUrl(url) is not { } normalized || RemoteMatcher.PathAfterHost(normalized) is not { } ownerRepo) return null;

        var slash = ownerRepo.LastIndexOf('/');
        return slash <= 0 ? null : RepoHashHelper.ComputeRepoHash(ownerRepo[..slash], ownerRepo[(slash + 1)..]);
    }
}
