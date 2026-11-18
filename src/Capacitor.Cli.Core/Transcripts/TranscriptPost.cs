using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core.Transcripts;

/// <summary>Posts one transcript batch over HTTP: canonical while the source's route is active, raw lines otherwise. The
/// caller gets the final answer and classifies it as it does a raw post's.</summary>
public static class TranscriptPost {
    public const string RawPath = "/hooks/transcript";

    /// <summary>The route for a stream whose lines go out from <paramref name="firstLine"/>, or null when they go raw.
    /// The lines before it are the server's already, and are projected first so the rest get the state an
    /// uninterrupted run gives them. A stream goes raw when that replay cannot be whole: no transcript, a missing or
    /// shorter one, a replay that fails or one that overruns <paramref name="replayBudget"/>, the redaction of large
    /// lines by <paramref name="decide"/> included. Projected without that state, a usage repeated across the start would
    /// count twice where the raw path counts it once.</summary>
    public static async Task<CanonicalRoute?> RouteAsync(
            IngestCapability? capability, string? vendor, string sessionId, string? agentId, string? transcriptPath, int firstLine,
            TimeSpan replayBudget, LineCaptureDecider? decide, TimeProvider time, CancellationToken ct
        ) {
        if (capability is null || !CanonicalVendors.Contains(vendor) || firstLine > 0 && transcriptPath is null) return null;
        if (CanonicalRoute.For(vendor, sessionId, agentId, await capability.SupportsCanonicalAsync(ct), decide) is not { } route) return null;
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

    /// <summary>Posts <paramref name="batch"/> with <c>PostWithRetryAsync</c>. A canonical batch the server stored is
    /// acknowledged on the route. A refusal a retry would not change, or a batch that cannot be projected, falls the route
    /// back and posts the batch raw, once: the refusal is the canonical encoding's, so only the raw answer is the verdict
    /// on the lines. A retryable answer comes back as it is.</summary>
    public static Task<HttpResponseMessage> SendAsync(
            HttpClient client, string baseUrl, TranscriptBatch batch, CanonicalRoute? route, TimeProvider time, Func<bool>? mayAttempt, CancellationToken ct
        ) =>
        SendAsync(batch, rawJson: null, route, time, (path, content) => client.PostWithRetryAsync($"{baseUrl}{path}", content, time, ct: ct, mayAttempt: mayAttempt));

    /// <summary>As <c>SendAsync</c>, with each post a single attempt within <paramref name="timeout"/>, and
    /// <paramref name="rawJson"/> posted as it is when the batch goes raw, so a body another build wrote reaches the
    /// server unchanged.</summary>
    public static Task<HttpResponseMessage> SendOnceAsync(
            HttpClient client, string baseUrl, TranscriptBatch batch, string rawJson, CanonicalRoute? route, TimeProvider time, TimeSpan timeout, CancellationToken ct
        ) =>
        SendAsync(batch, rawJson, route, time, (path, content) => client.PostOnceAsync($"{baseUrl}{path}", content, time, timeout, ct));

    static async Task<HttpResponseMessage> SendAsync(
            TranscriptBatch batch, string? rawJson, CanonicalRoute? route, TimeProvider time, Func<string, HttpContent, Task<HttpResponseMessage>> post
        ) {
        if (route is { Active: true } && Encode(route, batch, time) is { } canonical) {
            using var content  = Json(JsonSerializer.Serialize(canonical, CapacitorJsonContext.Default.CanonicalTranscriptBatch));
            var       response = await post(CanonicalRoute.HttpPath, content);

            if (response.IsSuccessStatusCode) {
                route.Acknowledged();

                return response;
            }

            if (HookSpool.IsRetryable((int)response.StatusCode)) return response;

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

    static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
}
