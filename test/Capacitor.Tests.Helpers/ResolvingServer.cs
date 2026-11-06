using System.Net;
using System.Text;

namespace Capacitor.Tests.Helpers;

/// <summary>A server that answers <c>/api/repositories/resolve</c> with <paramref name="resolvedHash"/>, or with
/// <paramref name="resolveStatus"/> when that is not OK, and every other request with <paramref name="otherBody"/>,
/// recording each request's path and query. <see cref="HangResolve"/> keeps a resolve unanswered until it is cancelled.</summary>
public sealed class ResolvingServer(
        string resolvedHash, HttpStatusCode resolveStatus = HttpStatusCode.OK, string otherBody = "[]") : HttpMessageHandler {
    public const string BaseUrl = "https://kcap.example";

    public List<string> Requests { get; } = [];

    public bool HangResolve { get; init; }

    public IEnumerable<string> Resolves => Requests.Where(r => r.StartsWith("/api/repositories/resolve", StringComparison.Ordinal));

    public HttpClient Client() => new(this) { BaseAddress = new Uri(BaseUrl) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        var path = request.RequestUri!.PathAndQuery;
        lock (Requests) Requests.Add(path);

        var resolving = path.StartsWith("/api/repositories/resolve", StringComparison.Ordinal);
        if (resolving && HangResolve) await Task.Delay(Timeout.Infinite, cancellationToken);

        var status = resolving ? resolveStatus : HttpStatusCode.OK;
        var body   = resolving ? $$"""{"repo_hash":"{{resolvedHash}}"}""" : otherBody;

        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
