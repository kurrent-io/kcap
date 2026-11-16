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

    /// <summary>A definite no holds for the process, so a producer with many streams asks once.</summary>
    [Test]
    [Arguments(404, "")]
    [Arguments(200, """{"canonical_format":0}""")]
    public async Task A_definite_no_is_asked_once(int status, string body) {
        Reply(status, body);
        var capability = Capability();

        await Assert.That(await capability.SupportsCanonicalAsync(CancellationToken.None)).IsFalse();
        await Assert.That(await capability.SupportsCanonicalAsync(CancellationToken.None)).IsFalse();
        await Assert.That(_server.LogEntries.Count).IsEqualTo(1);
    }

    /// <summary>A failure says nothing about the server, so the next call asks again.</summary>
    [Test]
    [Arguments(500, "")]
    [Arguments(200, "not json")]
    public async Task A_failed_answer_is_not_cached(int status, string body) {
        Reply(status, body);
        var capability = Capability();

        await Assert.That(await capability.SupportsCanonicalAsync(CancellationToken.None)).IsFalse();

        _server.Reset();
        Reply(200, """{"canonical_format":1}""");

        await Assert.That(await capability.SupportsCanonicalAsync(CancellationToken.None)).IsTrue();
    }
}
