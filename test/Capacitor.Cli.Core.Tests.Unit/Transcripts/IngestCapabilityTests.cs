using Capacitor.Cli.Core.Transcripts;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Transcripts;

public class IngestCapabilityTests : IDisposable {
    const string Path = "/api/ingest/capabilities";

    readonly WireMockServer _server = WireMockServer.Start();
    readonly HttpClient     _client = new();

    public void Dispose() {
        _client.Dispose();
        _server.Stop();
    }

    IngestCapability Capability() => new(_client, _server.Urls[0], TimeProvider.System);

    void Reply(int status, string body = "") =>
        _server.Given(Request.Create().WithPath(Path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    [Test]
    public async Task A_supported_answer_is_asked_once() {
        Reply(200, """{"canonical_format":1}""");
        var capability = Capability();

        await Assert.That(await capability.SupportsCanonicalAsync(CancellationToken.None)).IsTrue();
        await Assert.That(await capability.SupportsCanonicalAsync(CancellationToken.None)).IsTrue();
        await Assert.That(_server.LogEntries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_server_without_the_route_does_not_support_it() {
        Reply(404);

        await Assert.That(await Capability().SupportsCanonicalAsync(CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task An_unknown_format_does_not_support_it() {
        Reply(200, """{"canonical_format":0}""");

        await Assert.That(await Capability().SupportsCanonicalAsync(CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task A_server_fault_is_not_cached() {
        Reply(500);
        var capability = Capability();

        await Assert.That(await capability.SupportsCanonicalAsync(CancellationToken.None)).IsFalse();

        _server.Reset();
        Reply(200, """{"canonical_format":1}""");

        await Assert.That(await capability.SupportsCanonicalAsync(CancellationToken.None)).IsTrue();
    }
}
