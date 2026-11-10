using Capacitor.Cli.Core;
using Capacitor.Cli.Recording.PrDetection;
using Capacitor.Cli.Recording;

namespace Capacitor.Cli;

/// <summary>
/// The repository a long-lived server's working directory belongs to, resolved on first use and
/// held for the life of the process. Pull-request detection is skipped: callers need owner and
/// name only, and the provider round-trip would otherwise run in every agent session that spawns
/// the server, tool call or not. Concurrent first callers share one resolution.
/// </summary>
sealed class CwdRepository(
        ConfigRoot config, string cwd, GitProviderRouter router, TimeProvider time, CommandRunner? run = null) {
    /// <summary>How long a lookup of a repository with no remote may take, so a tool call that needs its hash is held
    /// for at most this when the server does not answer.</summary>
    internal static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a hash the server resolved is kept. It is bound to whoever is signed in, which a login can
    /// change while this process lives.</summary>
    internal static readonly TimeSpan ResolvedLifetime = TimeSpan.FromMinutes(5);

    readonly Lock             _lock = new();
    Task<RepositoryPayload?>? _detection;
    string?                   _hash;
    long                      _resolvedAt;

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
    /// outside one or when one with no remote cannot be resolved in time. A resolved hash is kept for
    /// <see cref="ResolvedLifetime"/>; a failed one is asked again.</summary>
    public async ValueTask<string?> GetHashAsync(HttpClient client, string baseUrl, CancellationToken ct = default) {
        if (_hash is { } known && time.GetElapsedTime(_resolvedAt) < ResolvedLifetime) return known;

        var repo = await GetAsync();

        if (repo?.Owner is null || repo.RepoName is null) return null;

        using var timeout = new CancellationTokenSource(ResolveTimeout, time);
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        string? hash;
        try {
            hash = await RepositoryHashes.ForAsync(client, baseUrl, repo.Owner, repo.RepoName, ct: linked.Token);
        } catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested) {
            return null;
        }
        if (hash is not null) (_hash, _resolvedAt) = (hash, time.GetTimestamp());

        return hash;
    }
}
