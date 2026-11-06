using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.PrDetection;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The reads a session in a repository with no remote makes by its repository — the memories a session start
/// injects, <c>kcap recap</c> and <c>kcap sessions</c> — use the hash the server resolves for the caller, since the
/// server stores that repository under an owner bound to whoever sent it.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class LocalRepositoryLookupTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Bound = "bound-local-hash";

    [Before(Test)]
    public Task ReportToAServerThatStoresLocalRepositories() {
        // A URL override in the environment is a server too, so it reports the same version.
        foreach (var server in new[] { ResolvingServer.BaseUrl, ProfileOverrides.FromEnvironment().Url }.OfType<string>())
            ServerVersionStore.Set(server, LocalRepositorySupport.MinimumServerVersion, Config.Root, TimeProvider.System);

        return ConfigMutator.MutateAsync(Config.Root, c => c with { Profiles = new() { ["p"] = new Profile { ServerUrl = ResolvingServer.BaseUrl } } });
    }

    [Test]
    public async Task Session_start_memories_are_read_by_the_resolved_hash() {
        using var repo   = GitRepo.Create();
        var       server = new ResolvingServer(Bound);
        var resolver = new SessionStartMemoryScopeResolver(
            new GitProviderRouter(), Config.Root, new WorkingDirectory(repo.Path), TimeProvider.System, _ => Task.FromResult(server.Client()));

        var scope = await resolver.ResolveAsync(repo.Path, ResolvingServer.BaseUrl, TimeSpan.FromSeconds(30), CancellationToken.None);

        await Assert.That(scope.RepoHash).IsEqualTo(Bound);
        await Assert.That(SessionStartMemoryContextProvider.BuildUrl(ResolvingServer.BaseUrl, scope)).Contains($"repo={Bound}");
    }

    /// <summary>A resolve that does not answer gives up within half the session start's budget, so the reads that need
    /// the hash are not left without time; the scope then names no repository rather than the unbound one.</summary>
    [Test]
    public async Task A_resolve_that_does_not_answer_leaves_the_session_start_half_its_budget() {
        using var repo   = GitRepo.Create();
        var       server = new ResolvingServer(Bound) { HangResolve = true };
        var resolver = new SessionStartMemoryScopeResolver(
            new GitProviderRouter(), Config.Root, new WorkingDirectory(repo.Path), TimeProvider.System, _ => Task.FromResult(server.Client()));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var scope = await resolver.ResolveAsync(repo.Path, ResolvingServer.BaseUrl, TimeSpan.FromSeconds(4), deadline.Token);

        await Assert.That(scope.RepoHash).IsNull();
        await Assert.That(server.Resolves).IsNotEmpty();
    }

    [Test]
    public async Task Recap_reads_the_resolved_hash() {
        using var repo   = GitRepo.Create();
        var       server = new ResolvingServer(Bound);
        var       http   = new RecordingCapacitorHttpClient(server);
        var api = new RepositoriesApi(http, new CapacitorServer(ResolvingServer.BaseUrl, Config.Root, Resolutions.At(ResolvingServer.BaseUrl, Config.Root)), TimeProvider.System);

        await new RecapCommand(Config.Root, null!, api, new GitProviderRouter(), new WorkingDirectory(repo.Path), TimeProvider.System).HandleRepoRecap();

        await Assert.That(server.Requests).Contains(r => r.StartsWith($"/api/repositories/{Bound}/recaps", StringComparison.Ordinal));
    }

    [Test]
    public async Task Sessions_lists_the_resolved_hash() {
        using var repo   = GitRepo.Create();
        var       server = new ResolvingServer(Bound, otherBody: """{"items":[],"total":0,"limit":20,"offset":0}""");

        await new SessionsCommand(
            Config.Root, Resolutions.At(ResolvingServer.BaseUrl, Config.Root), new RecordingCapacitorHttpClient(server),
            new GitProviderRouter(), new WorkingDirectory(repo.Path), TimeProvider.System).HandleAsync(["sessions"]);

        await Assert.That(server.Requests).Contains(r => r.StartsWith($"/api/repositories/{Bound}/sessions", StringComparison.Ordinal));
    }
}
