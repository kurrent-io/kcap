using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

/// <summary>The two server calls every routed import source makes: the transcript watermark probe and a synthetic hook post.</summary>
internal sealed class RoutedImportClient(HttpClient http, TimeProvider time, string baseUrl) {
    /// <summary>The highest line the server holds for the session, or for one agent's stream within it; null when it holds none.</summary>
    /// <exception cref="SessionDeletedException">The server deleted the session.</exception>
    /// <exception cref="HttpRequestException">Any other unsuccessful status, carried on the exception.</exception>
    public async Task<int?> FetchServerLastLineAsync(string sessionId, CancellationToken ct, string? agentId = null) {
        var url = $"{baseUrl}/api/sessions/{sessionId}/last-line" + (agentId is not null ? $"?agentId={Uri.EscapeDataString(agentId)}" : "");
        using var resp = await http.GetWithRetryAsync(url, time, ct: ct);

        SessionDeletedException.ThrowIfDeleted(resp, sessionId);
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"watermark probe returned {(int)resp.StatusCode}", null, resp.StatusCode);

        var       body = await resp.Content.ReadAsStringAsync(ct);
        using var doc  = JsonDocument.Parse(body);

        return doc.RootElement.TryGetProperty("last_line_number", out var ln) && ln.ValueKind == JsonValueKind.Number
            ? ln.GetInt32()
            : null;
    }

    /// <summary>Posts <paramref name="payload"/> to <c>/hooks/{route}</c>; true only on a success status.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled: a cancelled import, not a failed post.</exception>
    public async Task<bool> PostSyntheticHookAsync(string route, JsonObject payload, CancellationToken ct) {
        try {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await http.PostWithRetryAsync($"{baseUrl}/hooks/{route}", content, time, ct: ct);
            return resp.IsSuccessStatusCode;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch {
            return false;
        }
    }
}
