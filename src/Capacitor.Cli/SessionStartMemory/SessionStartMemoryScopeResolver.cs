using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.SessionStartMemory;

internal sealed class SessionStartMemoryScopeResolver(
        GitProviderRouter router, ConfigRoot config, WorkingDirectory workdir, TimeProvider time,
        Func<CancellationToken, Task<HttpClient>> client)
        : ISessionStartMemoryScopeResolver {
    public async Task<SessionStartMemoryScope> ResolveAsync(string? cwd, string baseUrl, TimeSpan budget, CancellationToken ct) {
        var started = time.GetTimestamp();
        TimeSpan Remaining() {
            var value = budget - time.GetElapsedTime(started);
            return value > TimeSpan.Zero ? value : TimeSpan.Zero;
        }

        string? repoHash = null;
        string? machine = null;
        try {
            var path = string.IsNullOrWhiteSpace(cwd) ? workdir.Path : cwd;
            var repo = await RepositoryDetection.DetectRepositoryAsync(router, config, path, time, Remaining(), detectPullRequest: false);
            if (repo?.Owner is not null && repo.RepoName is not null)
                repoHash = await RepositoryHashes.ForAsync(await client(ct), baseUrl, repo.Owner, repo.RepoName, ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch { }
        ct.ThrowIfCancellationRequested();
        if (Remaining() <= TimeSpan.Zero) throw new OperationCanceledException(ct);
        try { machine = await MachineIdProvider.GetOrCreateAsync(config, ct); } catch (OperationCanceledException) { throw; } catch { }
        return new SessionStartMemoryScope(repoHash, machine);
    }
}
