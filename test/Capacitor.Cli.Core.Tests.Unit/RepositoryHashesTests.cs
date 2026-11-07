using System.Net;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>The hash a client reads a repository's data by. One with a remote is computed here, as every client
/// computes it; one with no remote is stored under an owner the server binds to the caller, so only the server can
/// say, and a client that cannot ask reads nothing rather than the unbound hash.</summary>
public class RepositoryHashesTests {
    const string Local = "local~0123456789abcdef0123";

    [Test]
    public async Task A_local_repository_is_resolved_by_the_server() {
        var server = new ResolvingServer("0b0d10ca15a5e0e1");

        var hash = await RepositoryHashes.ForAsync(server.Client(), ResolvingServer.BaseUrl, Local, "demo");

        await Assert.That(hash).IsEqualTo("0b0d10ca15a5e0e1");
        await Assert.That(server.Resolves.Single()).IsEqualTo($"/api/repositories/resolve?owner={Uri.EscapeDataString(Local)}&name=demo");
    }

    [Test]
    public async Task A_hosted_repository_is_computed_without_asking() {
        var server = new ResolvingServer("unused");

        var hash = await RepositoryHashes.ForAsync(server.Client(), ResolvingServer.BaseUrl, "acme", "widgets");

        await Assert.That(hash).IsEqualTo(RepoHashHelper.ComputeRepoHash("acme", "widgets"));
        await Assert.That(server.Requests).IsEmpty();
    }

    [Test]
    [Arguments(HttpStatusCode.NotFound)]
    [Arguments(HttpStatusCode.InternalServerError)]
    public async Task A_server_that_cannot_resolve_leaves_no_hash(HttpStatusCode status) =>
        await Assert.That(await RepositoryHashes.ForAsync(new ResolvingServer("x", status).Client(), ResolvingServer.BaseUrl, Local, "demo"))
            .IsNull();

    /// <summary>The hash names files on this machine, so a server answer of any other shape is read as no hash at all.</summary>
    [Test]
    [Arguments("../../etc/passwd")]
    [Arguments("0B0D10CA15A5E0E1")]
    [Arguments("0b0d10ca15a5e0e")]
    public async Task A_resolved_value_that_is_not_a_repository_hash_leaves_no_hash(string answered) =>
        await Assert.That(await RepositoryHashes.ForAsync(new ResolvingServer(answered).Client(), ResolvingServer.BaseUrl, Local, "demo"))
            .IsNull();

    /// <summary>A lookup that never reached the server is reported as that, apart from an older server's answer.</summary>
    [Test]
    public async Task A_lookup_that_cannot_reach_the_server_says_so() {
        HttpRequestException? unreachable = null;
        using var client = new HttpClient(new Unreachable());

        var hash = await RepositoryHashes.ForAsync(client, ResolvingServer.BaseUrl, Local, "demo", onUnreachable: e => unreachable = e);

        await Assert.That(hash).IsNull();
        await Assert.That(unreachable).IsNotNull();
    }

    sealed class Unreachable : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
