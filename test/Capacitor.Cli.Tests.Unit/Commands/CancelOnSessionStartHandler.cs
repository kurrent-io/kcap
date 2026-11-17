namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Answers every request, except that a session-start hook post cancels <paramref name="import"/>
/// and then hangs until the request's own token gives up — a user's cancel landing mid-post.</summary>
sealed class CancelOnSessionStartHandler(CancellationTokenSource import) : HttpMessageHandler {
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.StartsWith("/hooks/session-start", StringComparison.Ordinal)) {
            await import.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
        }

        return new(request.Method == HttpMethod.Get ? System.Net.HttpStatusCode.NotFound : System.Net.HttpStatusCode.OK) {
            Content = new StringContent("{}"),
        };
    }
}
