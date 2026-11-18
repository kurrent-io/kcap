using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;

namespace Capacitor.Cli.Core.Http;

internal sealed class SharingApi(ICapacitorHttpClient http, CapacitorServer server, TimeProvider time) : ISharingApi {
    internal const string CatchingUpMessage = "The server is catching up and cannot change sharing yet; try again in a minute.";

    public Task<SharingRead<SharingDto>> GetAsync(string objectKey, CancellationToken ct = default) =>
        ReadAsync(SharingUrl(server.Url, objectKey), CapacitorJsonContext.Default.SharingDto, ct);

    public Task<SharingRead<SharingCandidatesDto>> CandidatesAsync(string objectKey, string? query, CancellationToken ct = default) =>
        ReadAsync($"{SharingUrl(server.Url, objectKey)}/candidates?query={Uri.EscapeDataString(query ?? "")}",
                  CapacitorJsonContext.Default.SharingCandidatesDto, ct);

    public async Task<SharingWriteResult> ShareAsync(string objectKey, string audienceKey, string relation,
                                                     CancellationToken ct = default) {
        using var content = JsonContent.Create(new SetSharingGrantBody(relation), CapacitorJsonContext.Default.SetSharingGrantBody);

        return await WriteAsync((c, token) => c.PutWithRetryAsync(GrantUrl(server.Url, objectKey, audienceKey), content, time, ct: token), ct);
    }

    public Task<SharingWriteResult> UnshareAsync(string objectKey, string audienceKey, CancellationToken ct = default) =>
        WriteAsync((c, token) => c.DeleteWithRetryAsync(GrantUrl(server.Url, objectKey, audienceKey), time, ct: token), ct);

    /// <summary>Both keys are opaque and each travels as one escaped path segment.</summary>
    /// <exception cref="ArgumentException">A key that is empty, or <c>.</c> or <c>..</c>, which escaping leaves to walk out of the route.</exception>
    internal static string SharingUrl(string baseUrl, string objectKey) => $"{baseUrl}/api/sharing/{Segment(objectKey, "object key")}";

    internal static string GrantUrl(string baseUrl, string objectKey, string audienceKey) =>
        $"{SharingUrl(baseUrl, objectKey)}/grants/{Segment(audienceKey, "audience key")}";

    static string Segment(string key, string what) =>
        key is "" or "." or ".." ? throw new ArgumentException($"\"{key}\" is not an {what}.") : Uri.EscapeDataString(key);

    async Task<SharingRead<T>> ReadAsync<T>(string url, JsonTypeInfo<T> type, CancellationToken ct) {
        using var response = await CapacitorApiRequests.SendAsync(http, server, (c, token) => c.GetWithRetryAsync(url, time, ct: token), ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return new SharingRead<T>.NotFound();
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable) throw new CapacitorApiException(503, CatchingUpMessage);

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity
         && await ReadErrorAsync(response, ct) is { } error)
            throw new CapacitorApiException((int)response.StatusCode, error.Message);

        if (response.StatusCode != HttpStatusCode.OK) throw await CapacitorApiRequests.FailureAsync(response);

        var value = await response.Content.ReadFromJsonAsync(type, ct)
                 ?? throw new CapacitorApiException(200, "The server's answer could not be read.");

        return new SharingRead<T>.Found(value);
    }

    async Task<SharingWriteResult> WriteAsync(Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct) {
        using var response = await CapacitorApiRequests.SendAsync(http, server, send, ct);

        switch (response.StatusCode) {
            case HttpStatusCode.NoContent or HttpStatusCode.OK:
                return new SharingWriteResult.Applied();

            case HttpStatusCode.NotFound:
                return new SharingWriteResult.NotFound();

            case HttpStatusCode.ServiceUnavailable:
                return new SharingWriteResult.Refused(new("catching_up", CatchingUpMessage));

            case HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest: {
                var error = await ReadErrorAsync(response, ct);

                return new SharingWriteResult.Refused(error ?? new("refused", $"Server returned HTTP {(int)response.StatusCode}"));
            }

            default:
                throw await CapacitorApiRequests.FailureAsync(response);
        }
    }

    /// <summary>Null unless the body is the sharing error shape: any other JSON, such as a ProblemDetails, binds to empty fields.</summary>
    static async Task<SharingErrorDto?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct) {
        try {
            var error = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.SharingErrorDto, ct);
            return error is { Code.Length: > 0, Message.Length: > 0 } ? error : null;
        } catch (System.Text.Json.JsonException) {
            return null;
        }
    }
}
