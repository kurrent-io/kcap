using Capacitor.Cli.Core.Eval.RepoContext;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.RepoContext;

public class RepoEvalMemoriesClientTests : IDisposable {
    const string Session = "0199a3b2-1c2d-7e8f-9a0b-1c2d3e4f5a6b";

    readonly WireMockServer _server = WireMockServer.Start();
    readonly HttpClient _http = new();
    readonly RecordingEvalObserver _observer = new();

    public void Dispose() {
        _http.Dispose();
        _server.Dispose();
    }

    void Answer(int status, string body = "") =>
        _server.Given(Request.Create().WithPath("/api/repos/eval-memories").WithParam("session_id", Session).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

    Task<string> ReadAsync(string? baseUrl = null) =>
        RepoEvalMemoriesClient.ForSessionAsync(_http, baseUrl ?? _server.Url!, Session, _observer, TimeProvider.System, CancellationToken.None);

    [Test]
    public async Task The_servers_section_is_handed_back_as_sent() {
        Answer(200, """{"memories":"<<<repository-memories\n### note\nabout it\nrepository-memories>>>\n\n"}""");

        await Assert.That(await ReadAsync()).IsEqualTo("<<<repository-memories\n### note\nabout it\nrepository-memories>>>\n\n");
    }

    /// <summary>A server without the route answers 404, which is not worth reporting.</summary>
    [Test]
    public async Task Not_found_reads_as_no_section_and_says_nothing() {
        Answer(404);

        await Assert.That(await ReadAsync()).IsEmpty();
        await Assert.That(_observer.Info).IsEmpty();
    }

    [Test]
    public async Task A_failed_read_is_no_section_and_is_reported() {
        Answer(500);

        await Assert.That(await ReadAsync()).IsEmpty();
        await Assert.That(_observer.Info.Single()).Contains("HTTP 500");
    }

    [Test]
    public async Task An_unreachable_server_is_no_section_and_is_reported() {
        await Assert.That(await ReadAsync("http://127.0.0.1:1")).IsEmpty();
        await Assert.That(_observer.Info).HasSingleItem();
    }
}
