using System.Net;
using System.Text.Json;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>What the server's anonymous <c>/healthz/embeddings</c> says about the configured embedding provider.</summary>
/// <param name="Provider">The configured provider token, such as <c>builtin</c> or <c>ollama</c>.</param>
/// <param name="Usable">False for a built-in model that cannot embed.</param>
/// <param name="Reason">Why it cannot; null when usable or not yet loaded.</param>
/// <param name="Vectors">Null from a server that does not report it.</param>
public sealed record EmbeddingHealthAnswer(string Provider, bool Usable, string? Reason, EmbeddingVectors? Vectors = null) {
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3);

    static HttpClient Probe => field ??= new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false });

    /// <summary>Null for a body that is not the report.</summary>
    public static EmbeddingHealthAnswer? Parse(string body) {
        try {
            using var doc  = JsonDocument.Parse(body);
            var       root = doc.RootElement;

            if (root.Str("provider") is not { } provider || root.Bool("usable") is not { } usable) return null;

            return new(provider, usable, root.Str("reason"), EmbeddingVectors.From(root));
        } catch (JsonException) {
            return null;
        }
    }

    public static Task<EmbeddingHealthAnswer?> ReadAsync(string baseUrl, CancellationToken ct) => ReadAsync(Probe, baseUrl, TimeProvider.System, ct);

    /// <summary>Null for an older server's 404, a failed or silent request, or a body that is not the report. Never
    /// throws except <see cref="OperationCanceledException"/> from <paramref name="ct"/>. The client must not follow
    /// redirects.</summary>
    public static async Task<EmbeddingHealthAnswer?> ReadAsync(HttpClient http, string baseUrl, TimeProvider time, CancellationToken ct) {
        using var attempt = new CancellationTokenSource(ReadTimeout, time);
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, attempt.Token);

        try {
            using var response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/healthz/embeddings", linked.Token);

            if (response.StatusCode != HttpStatusCode.OK) return null;

            return Parse(await response.Content.ReadAsStringAsync(linked.Token));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        }
        catch (Exception) {
            return null;
        }
    }
}
