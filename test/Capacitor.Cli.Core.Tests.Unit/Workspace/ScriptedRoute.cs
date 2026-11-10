using System.Net;
using System.Text.Json;
using Capacitor.Cli.Core.Workspace;

namespace Capacitor.Cli.Core.Tests.Unit.Workspace;

/// <summary>Answers the workspace route from a script: posts by the posted session, the probe by its own
/// answer. A null status throws, as a request that never got an answer does.</summary>
sealed class ScriptedRoute(Func<string, HttpStatusCode?> post, Func<HttpStatusCode?>? probe = null) : HttpMessageHandler {
    public List<string> Posted { get; } = [];

    public int Probes { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        if (request.Method == HttpMethod.Get) {
            Probes++;
            return Answer(probe is null ? HttpStatusCode.NoContent : probe());
        }

        var body    = JsonSerializer.Deserialize(await request.Content!.ReadAsStringAsync(ct), WorkspaceJsonContext.Default.WorkspaceChangesRequest)!;
        Posted.Add(body.SessionId);
        return Answer(post(body.SessionId));
    }

    static HttpResponseMessage Answer(HttpStatusCode? status) =>
        status is { } code ? new HttpResponseMessage(code) : throw new HttpRequestException("no answer");

    public HttpClient Client() => new(this, disposeHandler: false);
}
