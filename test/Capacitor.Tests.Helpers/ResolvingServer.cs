using System.Net;
using System.Text;

namespace Capacitor.Tests.Helpers;

/// <summary>A server that answers <c>/api/repositories/resolve</c> with <paramref name="resolvedHash"/>, or with
/// <paramref name="resolveStatus"/> when that is not OK, and every other request with <paramref name="otherBody"/>,
/// recording each request's path and query.</summary>
public sealed class ResolvingServer(
        string resolvedHash, HttpStatusCode resolveStatus = HttpStatusCode.OK, string otherBody = "[]") : HttpMessageHandler {
    public const string BaseUrl = "https://kcap.example";

    public List<string> Requests { get; } = [];

    public IEnumerable<string> Resolves => Requests.Where(r => r.StartsWith("/api/repositories/resolve", StringComparison.Ordinal));

    public HttpClient Client() => new(this) { BaseAddress = new Uri(BaseUrl) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        var path = request.RequestUri!.PathAndQuery;
        lock (Requests) Requests.Add(path);

        var resolving = path.StartsWith("/api/repositories/resolve", StringComparison.Ordinal);
        var status    = resolving ? resolveStatus : HttpStatusCode.OK;
        var body      = resolving ? $$"""{"repo_hash":"{{resolvedHash}}"}""" : otherBody;

        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
