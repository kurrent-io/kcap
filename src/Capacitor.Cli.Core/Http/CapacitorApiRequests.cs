using System.Net;

namespace Capacitor.Cli.Core.Http;

/// <summary>
/// The wire-error handling every API implementation in this namespace shares: a transport fault
/// or the client's own timeout becomes a status-less <see cref="CapacitorApiException"/>, and a
/// non-success response becomes one carrying the server's own text for a 401 or a generic status line
/// otherwise. Callers act on a response only when it is a success or a status they branch on;
/// everything else goes through <see cref="FailureAsync"/>.
/// </summary>
static class CapacitorApiRequests {
    /// <param name="quiet">Leaves stderr untouched: no re-auth hint, and an unusable server URL is a failure
    /// rather than a refusal the host prints. For a caller whose whole output is its own answer.</param>
    public static async Task<HttpResponseMessage> SendAsync(
            ICapacitorHttpClient http, CapacitorServer server,
            Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct, bool quiet = false) {
        if (quiet && !server.Usable) throw new CapacitorApiException(null, server.UnusableHint);

        using var client = quiet ? await http.ForBackgroundAsync(ct) : await http.ForCommandAsync(ct);

        try {
            return await send(client, ct);
        } catch (HttpRequestException ex) {
            throw new CapacitorApiException(null, HttpClientExtensions.UnreachableErrorText(server.Url, ex), ex);
        } catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) {
            // The caller's token is untouched, so this is the client's timeout: the request may have landed.
            throw new CapacitorApiException(null, $"{server.Url} did not answer in time.", ex);
        }
    }

    public static async Task<CapacitorApiException> FailureAsync(HttpResponseMessage response) =>
        new((int)response.StatusCode,
            response.StatusCode == HttpStatusCode.Unauthorized
                ? await HttpClientExtensions.UnauthorizedMessageAsync(response)
                : $"Server returned HTTP {(int)response.StatusCode}");
}
