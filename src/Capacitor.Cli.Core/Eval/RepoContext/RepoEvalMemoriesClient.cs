using System.Net;
using System.Text.Json;

namespace Capacitor.Cli.Core.Eval.RepoContext;

/// <summary>Reads the memories section a server eval of a session shows, rendered by the server, so a local eval shows the same.
/// Every failure reads as no section: the eval then goes on with the instructions alone.</summary>
public static class RepoEvalMemoriesClient {
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<string> ForSessionAsync(HttpClient client, string baseUrl, string sessionId, IEvalObserver observer, TimeProvider time,
            CancellationToken ct) {
        try {
            using var response = await client.GetOnceAsync(
                $"{baseUrl.TrimEnd('/')}/api/repos/eval-memories?session_id={Uri.EscapeDataString(sessionId)}", time, Timeout, ct);

            // A server without the route, or one that has not projected the session yet.
            if (response.StatusCode == HttpStatusCode.NotFound) return "";
            if (!response.IsSuccessStatusCode) {
                observer.OnInfo($"repository memories not read: HTTP {(int)response.StatusCode}");
                return "";
            }

            return JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(ct), RepoEvalContextJsonContext.Default.RepoEvalMemoriesResponse)?.Memories ?? "";
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            observer.OnInfo($"repository memories not read: {ex.Message}");
            return "";
        }
    }
}
