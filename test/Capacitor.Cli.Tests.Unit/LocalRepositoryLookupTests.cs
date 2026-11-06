using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.PrDetection;
using Capacitor.Cli.SessionStartMemory;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

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

    static JsonObject Call(string tool) => new() { ["params"] = new JsonObject { ["name"] = tool } };

    /// <summary>A long-lived server asks for the hash only for a tool that reads the working directory's repository, so a
    /// slow lookup holds no other call.</summary>
    [Test]
    [Arguments("search_sessions", true)]
    [Arguments("list_repo_sessions", true)]
    [Arguments("list_repo_plans", true)]
    [Arguments("get_session_summary", false)]
    [Arguments("get_turn", false)]
    [Arguments("list_turns", false)]
    public async Task Only_a_sessions_tool_that_reads_the_repository_looks_it_up(string tool, bool reads) =>
        await Assert.That(McpSessionsServer.ReadsTheCwdRepository(Call(tool))).IsEqualTo(reads);

    [Test]
    [Arguments("query_analytics", true)]
    [Arguments("get_analytics_schema", false)]
    public async Task Only_an_analytics_query_looks_the_repository_up(string tool, bool reads) =>
        await Assert.That(McpAnalyticsServer.ReadsTheCwdRepository(Call(tool))).IsEqualTo(reads);

    /// <summary>A lookup the server does not answer gives up after <see cref="CwdRepository.ResolveTimeout"/>, so a tool
    /// call waits that long at most and names no repository.</summary>
    [Test]
    public async Task A_lookup_that_does_not_answer_gives_up_in_time() {
        using var repo   = GitRepo.Create();
        var       clock  = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var       server = new ResolvingServer(Bound) { HangResolve = true };
        var       cwd    = new CwdRepository(Config.Root, repo.Path, new GitProviderRouter(), clock);

        var lookup = cwd.GetHashAsync(server.Client(), ResolvingServer.BaseUrl).AsTask();
        for (var i = 0; i < 300 && !server.Resolves.Any(); i++) await Task.Delay(20);
        clock.Advance(CwdRepository.ResolveTimeout);

        await Assert.That(await lookup.WaitAsync(TimeSpan.FromSeconds(10))).IsNull();
    }

    /// <summary>The hash is bound to whoever is signed in, which a login can change under a long-lived server, so a
    /// resolved one is asked for again once <see cref="CwdRepository.ResolvedLifetime"/> passes.</summary>
    [Test]
    public async Task A_resolved_hash_is_asked_for_again_once_it_ages() {
        using var repo   = GitRepo.Create();
        var       clock  = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var       server = new ResolvingServer(Bound);
        var       cwd    = new CwdRepository(Config.Root, repo.Path, new GitProviderRouter(), clock);

        await cwd.GetHashAsync(server.Client(), ResolvingServer.BaseUrl);
        await cwd.GetHashAsync(server.Client(), ResolvingServer.BaseUrl);
        await Assert.That(server.Resolves.Count()).IsEqualTo(1);

        clock.Advance(CwdRepository.ResolvedLifetime);
        await Assert.That(await cwd.GetHashAsync(server.Client(), ResolvingServer.BaseUrl)).IsEqualTo(Bound);
        await Assert.That(server.Resolves.Count()).IsEqualTo(2);
    }

    /// <summary>Only an older server's 404 reads as unresolved; a refused sign-in or a failed server is reported as the
    /// failure it is, so a command can say to sign in again rather than that the server may need updating.</summary>
    [Test]
    [Arguments(HttpStatusCode.NotFound, false)]
    [Arguments(HttpStatusCode.Unauthorized, true)]
    [Arguments(HttpStatusCode.InternalServerError, true)]
    public async Task A_command_lookup_reports_a_failure_other_than_an_older_server(HttpStatusCode status, bool throws) {
        var server = new ResolvingServer(Bound, status);
        var api    = new RepositoriesApi(new RecordingCapacitorHttpClient(server),
            new CapacitorServer(ResolvingServer.BaseUrl, Config.Root, Resolutions.At(ResolvingServer.BaseUrl, Config.Root)), TimeProvider.System);

        if (throws) await Assert.That(async () => await api.HashForAsync("local~0123456789abcdef0123", "demo")).Throws<CapacitorApiException>();
        else await Assert.That(await api.HashForAsync("local~0123456789abcdef0123", "demo")).IsNull();
    }
}
