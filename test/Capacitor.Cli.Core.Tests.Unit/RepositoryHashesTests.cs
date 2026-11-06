using System.Net;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>The hash a client reads a repository's data by. One with a remote is computed here, as every client
/// computes it; one with no remote is stored under an owner the server binds to the caller, so only the server can
/// say, and a client that cannot ask reads nothing rather than the unbound hash.</summary>
public class RepositoryHashesTests {
    const string Local = "local~0123456789abcdef0123";

    [Test]
    public async Task A_local_repository_is_resolved_by_the_server() {
        var server = new ResolvingServer("bound-hash-01");

        var hash = await RepositoryHashes.ForAsync(server.Client(), ResolvingServer.BaseUrl, Local, "demo");

        await Assert.That(hash).IsEqualTo("bound-hash-01");
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
}
