using Capacitor.Cli.Recording.PrDetection;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Recording.Tests.Unit.PrDetection;

public class GitProviderRouterTests {
    static GitProviderRouter Router => new();

    static CommandRunner Never => (_, _, _, _) => throw new InvalidOperationException("probe should not run for SaaS hosts");

    [Test]
    public async Task Saas_hosts_route_without_probing() {
        await Assert.That(await Router.ResolveAsync("github.com", "/c", TimeSpan.FromSeconds(2), Never)).IsEqualTo(GitProviderKind.GitHub);
        await Assert.That(await Router.ResolveAsync("gitlab.com", "/c", TimeSpan.FromSeconds(2), Never)).IsEqualTo(GitProviderKind.GitLab);
    }

    [Test]
    public async Task Custom_host_in_gh_auth_status_is_github() {
        CommandRunner fake = async (cmd, args, _, _) => {
            await Assert.That(cmd).IsEqualTo("gh");
            await Assert.That(args).IsEqualTo("auth status --json hosts");
            return "{\"hosts\":{\"github.com\":[],\"ghe.corp.com\":[]}}";
        };
        await Assert.That(await Router.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), fake)).IsEqualTo(GitProviderKind.GitHub);
    }

    [Test]
    public async Task Custom_host_not_in_gh_falls_back_to_gitlab() {
        CommandRunner fake = (_, _, _, _) => Task.FromResult<string?>("""{"hosts":{"github.com":[]}}""");
        await Assert.That(await Router.ResolveAsync("gitlab.corp.com", "/c", TimeSpan.FromSeconds(2), fake)).IsEqualTo(GitProviderKind.GitLab);
    }

    [Test]
    public async Task Probe_result_is_memoized_per_host() {
        var calls = 0;
        CommandRunner fake = (_, _, _, _) => { calls++; return Task.FromResult<string?>("""{"hosts":{"ghe.corp.com":[]}}"""); };
        var router = Router;
        await router.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), fake);
        await router.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), fake);
        await Assert.That(calls).IsEqualTo(1);
    }

    /// <summary>A probe that failed says nothing about the host, so a long-lived process must soon ask again, and a
    /// remembered answer must outlast the gap at which a failed one is asked again.</summary>
    [Test]
    public async Task A_probe_failure_is_not_remembered_for_as_long_as_a_good_one() {
        var time   = new FakeTimeProvider();
        var router = new GitProviderRouter(time);
        var calls  = 0;

        Task<string?> Fails(string cmd, string args, string cwd, TimeSpan cap) { calls++; return Task.FromResult<string?>(null); }
        Task<string?> Lists(string cmd, string args, string cwd, TimeSpan cap) { calls++; return Task.FromResult<string?>("""{"hosts":{"ghe.corp.com":[]}}"""); }

        await Assert.That(await router.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), Fails)).IsEqualTo(GitProviderKind.GitLab);
        await Assert.That(await router.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), Lists)).IsEqualTo(GitProviderKind.GitLab);
        await Assert.That(calls).IsEqualTo(1);

        time.Advance(GitProviderRouter.FailedProbeLifetime);

        await Assert.That(await router.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), Lists)).IsEqualTo(GitProviderKind.GitHub);
        await Assert.That(calls).IsEqualTo(2);

        time.Advance(GitProviderRouter.FailedProbeLifetime);

        await Assert.That(await router.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), Fails)).IsEqualTo(GitProviderKind.GitHub);
        await Assert.That(calls).IsEqualTo(2);
    }

    /// <summary>A probe that failed says nothing about the host, so what gh last answered still stands, for as long as
    /// a failed probe is remembered. Reading it as GitLab would run the wrong detector on a GitHub host.</summary>
    [Test]
    public async Task A_failed_probe_leaves_an_answered_kind_standing() {
        var time   = new FakeTimeProvider();
        var router = new GitProviderRouter(time);
        var calls  = 0;

        Task<string?> Fails(string cmd, string args, string cwd, TimeSpan cap) { calls++; return Task.FromResult<string?>(null); }
        Task<string?> Lists(string cmd, string args, string cwd, TimeSpan cap) { calls++; return Task.FromResult<string?>("""{"hosts":{"ghe.corp.com":[]}}"""); }
        Task<string?> Omits(string cmd, string args, string cwd, TimeSpan cap) { calls++; return Task.FromResult<string?>("""{"hosts":{}}"""); }

        await Assert.That(await router.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), Lists)).IsEqualTo(GitProviderKind.GitHub);

        time.Advance(GitProviderRouter.AnswerLifetime);
        await Assert.That(await router.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), Fails)).IsEqualTo(GitProviderKind.GitHub);
        await Assert.That(calls).IsEqualTo(2);

        time.Advance(GitProviderRouter.FailedProbeLifetime - TimeSpan.FromSeconds(1));
        await Assert.That(await router.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), Fails)).IsEqualTo(GitProviderKind.GitHub);
        await Assert.That(calls).IsEqualTo(2);

        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(await router.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), Fails)).IsEqualTo(GitProviderKind.GitHub);
        await Assert.That(calls).IsEqualTo(3);

        time.Advance(GitProviderRouter.FailedProbeLifetime);
        await Assert.That(await router.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), Omits)).IsEqualTo(GitProviderKind.GitLab);
    }

    [Test]
    public async Task A_remembered_host_expires() {
        var time    = new FakeTimeProvider();
        var router  = new GitProviderRouter(time);
        var listed  = true;
        var calls   = 0;

        Task<string?> Probe(string cmd, string args, string cwd, TimeSpan cap) {
            calls++;

            return Task.FromResult<string?>(listed ? """{"hosts":{"ghe.corp.com":[]}}""" : """{"hosts":{}}""");
        }

        await Assert.That(await router.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), Probe)).IsEqualTo(GitProviderKind.GitHub);

        listed = false;
        time.Advance(GitProviderRouter.AnswerLifetime - TimeSpan.FromSeconds(1));

        await Assert.That(await router.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), Probe)).IsEqualTo(GitProviderKind.GitHub);
        await Assert.That(calls).IsEqualTo(1);

        time.Advance(TimeSpan.FromSeconds(1));

        await Assert.That(await router.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), Probe)).IsEqualTo(GitProviderKind.GitLab);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task Null_host_is_unknown() {
        await Assert.That(await Router.ResolveAsync(null, "/c", TimeSpan.FromSeconds(2), Never)).IsEqualTo(GitProviderKind.Unknown);
    }

    [Test]
    public async Task Malformed_gh_json_falls_back_to_gitlab() {
        // `gh auth status --json hosts` returned junk → JsonNode.Parse throws → we can't confirm
        // the host is a GitHub host, so best-effort GitLab.
        CommandRunner fake = (_, _, _, _) => Task.FromResult<string?>("{not valid json");
        await Assert.That(await Router.ResolveAsync("malformed-json.example.com", "/c", TimeSpan.FromSeconds(2), fake)).IsEqualTo(GitProviderKind.GitLab);
    }

    [Test]
    public async Task Null_probe_result_falls_back_to_gitlab() {
        // Probe failed / timed out (runner returned null) → best-effort GitLab.
        CommandRunner fake = (_, _, _, _) => Task.FromResult<string?>(null);
        await Assert.That(await Router.ResolveAsync("null-probe.example.com", "/c", TimeSpan.FromSeconds(2), fake)).IsEqualTo(GitProviderKind.GitLab);
    }
}
