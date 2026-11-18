using System.Net;
using Capacitor.Cli.Core.Transcripts;
using Microsoft.Extensions.Time.Testing;
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

    /// <summary>A server slower than the caller's budget is a no for now, and the next call asks again.</summary>
    [Test]
    public async Task An_answer_slower_than_the_budget_is_a_no_that_is_not_cached() {
        var       time    = new FakeTimeProvider();
        using var handler = new FirstProbeHangs();
        using var client  = new HttpClient(handler);
        var capability = new IngestCapability(client, "http://server", time, TimeSpan.FromSeconds(2));

        var first = capability.SupportsCanonicalAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(2));

        await Assert.That(await first.WaitAsync(TimeSpan.FromSeconds(30))).IsFalse();
        await Assert.That(await capability.SupportsCanonicalAsync(CancellationToken.None)).IsTrue();
        await Assert.That(handler.Probes).IsEqualTo(2);
    }

    /// Holds the first probe until the caller gives up and answers every later one with format 1.
    sealed class FirstProbeHangs : HttpMessageHandler {
        public int Probes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            if (++Probes == 1) await Task.Delay(Timeout.Infinite, ct);

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"canonical_format":1}""") };
        }
    }
}
