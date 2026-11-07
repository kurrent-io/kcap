using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Capacitor.Cli.Core.Http;

internal sealed class PassesApi(ICapacitorHttpClient http, CapacitorServer server, TimeProvider time) : IPassesApi {
    public async Task<bool> PassesOfferedAsync(CancellationToken ct = default) {
        using var response = await CapacitorApiRequests.SendAsync(
            http, server, (c, token) => c.GetWithRetryAsync($"{server.Url}/api/users/me/surfaces", time, ct: token), ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (response.StatusCode != HttpStatusCode.OK) throw await CapacitorApiRequests.FailureAsync(response);

        return (await ReadAsync(response, CapacitorJsonContext.Default.UserSurfacesDto, ct))?.Passes == true;
    }

    public async Task<IReadOnlyList<UserCandidateDto>> SearchColleaguesAsync(string query, CancellationToken ct = default) {
        using var response = await CapacitorApiRequests.SendAsync(
            http, server,
            (c, token) => c.GetWithRetryAsync($"{server.Url}/api/users/search?q={Uri.EscapeDataString(query)}", time, ct: token), ct);

        if (response.StatusCode != HttpStatusCode.OK) throw await CapacitorApiRequests.FailureAsync(response);

        return (await ReadAsync(response, CapacitorJsonContext.Default.UserCandidatesDto, ct))?.Candidates ?? [];
    }

    public async Task<PassSendResult> SendAsync(StartPassBody body, CancellationToken ct = default) {
        using var content  = JsonContent.Create(body, CapacitorJsonContext.Default.StartPassBody);
        using var response = await CapacitorApiRequests.SendAsync(http, server, (c, token) => c.PostAsync($"{server.Url}/api/passes", content, token), ct);

        switch (response.StatusCode) {
            case HttpStatusCode.OK or HttpStatusCode.Accepted: {
                var pass = await ReadAsync(response, CapacitorJsonContext.Default.StartPassResponseDto, ct)
                        ?? throw new CapacitorApiException((int)response.StatusCode, "The server's answer could not be read.");

                return response.StatusCode == HttpStatusCode.OK ? new PassSendResult.Sent(pass) : new PassSendResult.AwaitingApproval(pass);
            }
            case HttpStatusCode.NotFound:
                return new PassSendResult.NotFound();
            case HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity or HttpStatusCode.TooManyRequests:
                return new PassSendResult.Refused(await RefusalAsync(response, ct));
            default:
                throw await CapacitorApiRequests.FailureAsync(response);
        }
    }

    public Task<PassAnswerResult> ApproveAsync(string passId, string via, CancellationToken ct = default) =>
        AnswerAsync(passId, "approve", JsonContent.Create(new ApprovePassBody(via), CapacitorJsonContext.Default.ApprovePassBody), ct);

    public Task<PassAnswerResult> DeclineAsync(string passId, CancellationToken ct = default) =>
        AnswerAsync(passId, "decline", null, ct);

    public async Task<PassReadResult> GetAsync(string passId, CancellationToken ct = default) {
        using var response = await CapacitorApiRequests.SendAsync(
            http, server, (c, token) => c.GetWithRetryAsync($"{server.Url}/api/passes/{Uri.EscapeDataString(passId)}", time, ct: token), ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return new PassReadResult.NotFound();
        if (response.StatusCode != HttpStatusCode.OK) throw await CapacitorApiRequests.FailureAsync(response);

        return await ReadAsync(response, CapacitorJsonContext.Default.PassDto, ct) is { } pass
            ? new PassReadResult.Found(pass)
            : throw new CapacitorApiException(200, "The server's answer could not be read.");
    }

    /// <summary>A 409 is an answer too: the call moved nothing, and its body says the pass's state.</summary>
    async Task<PassAnswerResult> AnswerAsync(string passId, string verb, HttpContent? body, CancellationToken ct) {
        using var content  = body;
        using var response = await CapacitorApiRequests.SendAsync(
            http, server, (c, token) => c.PostAsync($"{server.Url}/api/passes/{Uri.EscapeDataString(passId)}/{verb}", content, token), ct);

        switch (response.StatusCode) {
            case HttpStatusCode.OK or HttpStatusCode.Conflict: {
                var answer = await ReadAsync(response, CapacitorJsonContext.Default.PassApprovalResponseDto, ct)
                          ?? throw new CapacitorApiException((int)response.StatusCode, "The server's answer could not be read.");

                return new PassAnswerResult.Answered(answer);
            }
            case HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound:
                return new PassAnswerResult.Refused(await RefusalAsync(response, ct));
            default:
                throw await CapacitorApiRequests.FailureAsync(response);
        }
    }

    static async Task<PassRefusalDto> RefusalAsync(HttpResponseMessage response, CancellationToken ct) =>
        await ReadAsync(response, CapacitorJsonContext.Default.PassRefusalDto, ct) is { Reason.Length: > 0 } refusal
            ? refusal
            : new PassRefusalDto("refused", $"The server refused it (HTTP {(int)response.StatusCode}).");

    /// <summary>Null for a body that is empty or not the expected shape.</summary>
    static async Task<T?> ReadAsync<T>(HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct) {
        try {
            return await response.Content.ReadFromJsonAsync(type, ct);
        } catch (JsonException) {
            return default;
        }
    }
}
