using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli;

/// <summary>
/// The repository a long-lived server's working directory belongs to, resolved on first use and
/// held for the life of the process. Pull-request detection is skipped: callers need owner and
/// name only, and the provider round-trip would otherwise run in every agent session that spawns
/// the server, tool call or not. Concurrent first callers share one resolution.
/// </summary>
sealed class CwdRepository(
        ConfigRoot config, string cwd, GitProviderRouter router, TimeProvider time, CommandRunner? run = null) {
    readonly Lock             _lock = new();
    Task<RepositoryPayload?>? _detection;
    string?                   _hash;

    /// <summary>Owner, name, host and branch of the checkout, or null outside one. A failed resolution is retried.</summary>
    public async ValueTask<RepositoryPayload?> GetAsync() {
        Task<RepositoryPayload?> detection;
        lock (_lock) {
            if (_detection is null || _detection.IsFaulted || _detection.IsCanceled)
                _detection = RepositoryDetection.DetectRepositoryAsync(router, config, cwd, time, detectPullRequest: false, run: run);
            detection = _detection;
        }

        return await detection;
    }

    /// <summary>The hash the caller reads the checkout's repository by (<see cref="RepositoryHashes"/>), or null
    /// outside one or when one with no remote cannot be resolved. A resolved hash is kept; a failed one is asked again.</summary>
    public async ValueTask<string?> GetHashAsync(HttpClient client, string baseUrl) {
        if (_hash is { } known) return known;

        var repo = await GetAsync();

        if (repo?.Owner is null || repo.RepoName is null) return null;

        return _hash = await RepositoryHashes.ForAsync(client, baseUrl, repo.Owner, repo.RepoName);
    }
}
