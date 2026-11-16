using System.Net;
using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core.Transcripts;

/// <summary>Posts one transcript batch over HTTP: canonical while the source's route is active, raw lines otherwise. The
/// caller gets the final answer and classifies it as it does a raw post's.</summary>
public static class TranscriptPost {
    public const string RawPath = "/hooks/transcript";

    /// The <c>error</c> in the 422 of a server that stored nothing of a canonical batch it cannot take.
    const string UnsupportedCode = "canonical_unsupported";

    /// <summary>The route for a stream whose lines go out from <paramref name="firstLine"/>, or null when they go raw.
    /// The lines before it are the server's already, and are projected first so the rest get the state an
    /// uninterrupted run gives them. A stream goes raw when that replay cannot be whole: no transcript, a missing or
    /// shorter one, a replay that fails or one that overruns <paramref name="replayBudget"/>. Projected without that
    /// state, a usage repeated across the start would count twice where the raw path counts it once.</summary>
    public static async Task<CanonicalRoute?> RouteAsync(
            IngestCapability? capability, string? vendor, string sessionId, string? agentId, string? transcriptPath, int firstLine,
            TimeSpan replayBudget, TimeProvider time, CancellationToken ct
        ) {
        if (capability is null || !CanonicalVendors.Contains(vendor) || firstLine > 0 && transcriptPath is null) return null;
        if (CanonicalRoute.For(vendor, sessionId, agentId, await capability.SupportsCanonicalAsync(ct)) is not { } route) return null;
        if (firstLine <= 0) return route;

        using var budget = new CancellationTokenSource(replayBudget, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);

        try {
            return await route.ReplayAsync(transcriptPath!, firstLine, linked.Token) ? route : null;
        } catch (CanonicalEncodingException) {
            return null;
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            return null;
        }
    }

    /// <summary>Posts <paramref name="batch"/> with <c>PostWithRetryAsync</c>. A refusal as unsupported, or a batch that
    /// cannot be projected, falls the route back and posts the batch raw, once; a canonical batch the server stored is
    /// acknowledged on the route.</summary>
    public static Task<HttpResponseMessage> SendAsync(
            HttpClient client, string baseUrl, TranscriptBatch batch, CanonicalRoute? route, TimeProvider time, Func<bool>? mayAttempt, CancellationToken ct
        ) =>
        SendAsync(batch, rawJson: null, route, time, (path, content) => client.PostWithRetryAsync($"{baseUrl}{path}", content, time, ct: ct, mayAttempt: mayAttempt), ct);

    /// <summary>As <c>SendAsync</c>, with each post a single attempt within <paramref name="timeout"/>, and
    /// <paramref name="rawJson"/> posted as it is when the batch goes raw, so a body another build wrote reaches the
    /// server unchanged.</summary>
    public static Task<HttpResponseMessage> SendOnceAsync(
            HttpClient client, string baseUrl, TranscriptBatch batch, string rawJson, CanonicalRoute? route, TimeProvider time, TimeSpan timeout, CancellationToken ct
        ) =>
        SendAsync(batch, rawJson, route, time, (path, content) => client.PostOnceAsync($"{baseUrl}{path}", content, time, timeout, ct), ct);

    static async Task<HttpResponseMessage> SendAsync(
            TranscriptBatch batch, string? rawJson, CanonicalRoute? route, TimeProvider time, Func<string, HttpContent, Task<HttpResponseMessage>> post, CancellationToken ct
        ) {
        if (route is { Active: true } && Encode(route, batch, time) is { } canonical) {
            using var content  = Json(JsonSerializer.Serialize(canonical, CapacitorJsonContext.Default.CanonicalTranscriptBatch));
            var       response = await post(CanonicalRoute.HttpPath, content);

            if (response.IsSuccessStatusCode) {
                route.Acknowledged();

                return response;
            }

            if (!await IsUnsupportedAsync(response, ct)) return response;

            response.Dispose();
            route.FallBack();
        }

        using var raw = Json(rawJson ?? JsonSerializer.Serialize(batch, CapacitorJsonContext.Default.TranscriptBatch));

        return await post(RawPath, raw);
    }

    static CanonicalTranscriptBatch? Encode(CanonicalRoute route, TranscriptBatch batch, TimeProvider time) {
        try {
            return route.Encode(batch, time.GetUtcNow());
        } catch (CanonicalEncodingException) {
            return null;
        }
    }

    static async Task<bool> IsUnsupportedAsync(HttpResponseMessage response, CancellationToken ct) {
        if (response.StatusCode != HttpStatusCode.UnprocessableEntity) return false;

        try {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            return body.RootElement.Str("error") == UnsupportedCode;
        } catch (JsonException) {
            return false;
        }
    }

    static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
}
