using System.Net;
using System.Text;

namespace Capacitor.Tests.Helpers;

/// <summary>A server that answers <c>/api/repositories/resolve</c> with <paramref name="resolvedHash"/>, or with
/// <paramref name="resolveStatus"/> when that is not OK, and every other request with <paramref name="otherBody"/>,
/// recording each request's path and query. <see cref="HangResolve"/> keeps a resolve unanswered until it is cancelled.</summary>
public sealed class ResolvingServer(
        string resolvedHash, HttpStatusCode resolveStatus = HttpStatusCode.OK, string otherBody = "[]") : HttpMessageHandler {
    public const string BaseUrl = "https://kcap.example";

    readonly List<string>         _requests = [];
    readonly TaskCompletionSource _resolved = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Every request's path and query so far, read under the lock the handler records them under.</summary>
    public IReadOnlyList<string> Requests {
        get {
            lock (_requests) return [.. _requests];
        }
    }

    public bool HangResolve { get; init; }

    public IReadOnlyList<string> Resolves => [.. Requests.Where(r => r.StartsWith("/api/repositories/resolve", StringComparison.Ordinal))];

    /// <summary>Completes once a resolve has reached the handler.</summary>
    public Task ResolveReceived => _resolved.Task;

    public HttpClient Client() => new(this) { BaseAddress = new Uri(BaseUrl) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        var path = request.RequestUri!.PathAndQuery;
        lock (_requests) _requests.Add(path);

        var resolving = path.StartsWith("/api/repositories/resolve", StringComparison.Ordinal);
        if (resolving) _resolved.TrySetResult();
        if (resolving && HangResolve) await Task.Delay(Timeout.Infinite, cancellationToken);

        var status = resolving ? resolveStatus : HttpStatusCode.OK;
        var body   = resolving ? $$"""{"repo_hash":"{{resolvedHash}}"}""" : otherBody;

        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
