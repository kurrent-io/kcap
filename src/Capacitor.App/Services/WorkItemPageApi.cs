using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Json;
using System.Reactive;
using System.Reactive.Subjects;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

public sealed class WorkItemPageApi(ICapacitorHttpClient http, ProfileContext profiles) : IWorkItemPageApi {
    static RemoteModelsJsonContext Json => RemoteModelsJsonContext.Default;
    readonly Subject<Unit> _changed = new();
    public IObservable<Unit> Changed => _changed;
    readonly BehaviorSubject<IReadOnlySet<string>> _suggested = new(FrozenSet<string>.Empty);
    readonly Lock _suggestedGate = new();
    public IObservable<IReadOnlySet<string>> SuggestedSessions => _suggested;

    public async Task<WorkItemPartsDto?> ReadAsync(string workItemId, CancellationToken ct) {
        WorkItemPartsDto? body = null;
        await SendAsync(ApiRoutes.WorkItem(workItemId, "parts"), (client, url) => client.GetAsync(url, ct),
            async response => body = await response.Content.ReadFromJsonAsync(Json.WorkItemPartsDto, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return body;
    }

    public Task<bool> SetStatusAsync(string workItemId, string? status, CancellationToken ct) =>
        PostAsync(ApiRoutes.WorkItem(workItemId, "status"), new WorkItemStatusRequestDto(status), Json.WorkItemStatusRequestDto, ct);

    public Task<bool> RenameAsync(string workItemId, string title, CancellationToken ct) =>
        PostAsync(ApiRoutes.WorkItem(workItemId, "title"), new WorkItemTitleRequestDto(title), Json.WorkItemTitleRequestDto, ct);

    public Task<bool> SetGoalAsync(string workItemId, string? goal, CancellationToken ct) =>
        PostAsync(ApiRoutes.WorkItem(workItemId, "goal"), new WorkItemGoalRequestDto(goal), Json.WorkItemGoalRequestDto, ct);

    public Task<bool> OrderPartsAsync(string workItemId, IReadOnlyList<string> partIds, CancellationToken ct) =>
        PostAsync(ApiRoutes.WorkItem(workItemId, "parts/order"), new WorkItemPartOrderRequestDto(partIds), Json.WorkItemPartOrderRequestDto, ct);

    public Task<bool> MakePartAsync(string workItemId, string looseEndId, CancellationToken ct) =>
        ChangeAsync(ApiRoutes.LooseEndPart(workItemId, looseEndId), (client, url) => client.PostAsync(url, null, ct), null, ct);

    public async Task<string?> AddPartAsync(string workItemId, string title, CancellationToken ct) {
        string? id = null;
        await ChangeAsync(ApiRoutes.WorkItem(workItemId, "parts"),
            (client, url) => client.PostAsJsonAsync(url, new WorkItemPartRequestDto(title), Json.WorkItemPartRequestDto, ct),
            async response => id = (await response.Content.ReadFromJsonAsync(Json.WorkItemPartCreatedDto, ct).ConfigureAwait(false))?.WorkItemId,
            ct).ConfigureAwait(false);
        return id;
    }

    public Task<bool> RemovePartAsync(string workItemId, string partId, CancellationToken ct) =>
        ChangeAsync(ApiRoutes.WorkItem(workItemId, $"parts/{Uri.EscapeDataString(partId)}/remove"), (client, url) => client.PostAsync(url, null, ct), null, ct);

    public Task<bool> SetWaitAsync(string partId, string waitsOnId, bool waits, CancellationToken ct) =>
        PostAsync(ApiRoutes.WorkItem(partId, waits ? "waits" : "waits/remove"), new WorkItemWaitRequestDto(waitsOnId), Json.WorkItemWaitRequestDto, ct);

    public Task<bool> AddLinkAsync(string workItemId, string reference, CancellationToken ct) =>
        PostAsync(ApiRoutes.WorkItem(workItemId, "links"), new WorkItemLinkRequestDto(reference), Json.WorkItemLinkRequestDto, ct);

    public async Task<string?> CreateAsync(string projectId, string title, CancellationToken ct) {
        string? id = null;
        await ChangeAsync(ApiRoutes.WorkItems,
            (client, url) => client.PostAsJsonAsync(url, new WorkItemCreateRequestDto(projectId, title), Json.WorkItemCreateRequestDto, ct),
            async response => id = (await response.Content.ReadFromJsonAsync(Json.WorkItemDeclaredDto, ct).ConfigureAwait(false))?.WorkItemId,
            ct).ConfigureAwait(false);
        return id;
    }

    public async Task<string?> CreateForSessionAsync(string sessionId, string title, CancellationToken ct) {
        string? id = null;
        await ChangeAsync(ApiRoutes.WorkItemDeclare,
            (client, url) => client.PostAsJsonAsync(url, new WorkItemDeclareRequestDto(title, sessionId), Json.WorkItemDeclareRequestDto, ct),
            async response => id = (await response.Content.ReadFromJsonAsync(Json.WorkItemDeclaredDto, ct).ConfigureAwait(false))?.WorkItemId,
            ct).ConfigureAwait(false);
        return id;
    }

    public Task<bool> MoveSessionAsync(string workItemId, string sessionId, CancellationToken ct) =>
        PostAsync(ApiRoutes.WorkItem(workItemId, "sessions"), new WorkItemSessionRequestDto(sessionId), Json.WorkItemSessionRequestDto, ct);

    public Task<bool> RemoveSessionAsync(string workItemId, string sessionId, CancellationToken ct) =>
        PostAsync(ApiRoutes.WorkItem(workItemId, "sessions/remove"), new WorkItemSessionRequestDto(sessionId), Json.WorkItemSessionRequestDto, ct);

    public async Task<WorkItemSuggestionDto?> ReadSuggestionAsync(string sessionId, CancellationToken ct) {
        WorkItemSuggestionDto? body = null;
        var answered = await SendAsync(ApiRoutes.SessionSuggestion(sessionId), (client, url) => client.GetAsync(url, ct), async response => {
            if (response.StatusCode != HttpStatusCode.NoContent)
                body = await response.Content.ReadFromJsonAsync(Json.WorkItemSuggestionDto, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        if (answered) MarkSuggested(sessionId, body is not null);
        return body;
    }

    public Task<bool> AcceptSuggestionAsync(string workItemId, string sessionId, CancellationToken ct) =>
        AnswerSuggestionAsync(ApiRoutes.WorkItem(workItemId, "suggestion/accept"), sessionId, ct);

    public Task<bool> DismissSuggestionAsync(string workItemId, string sessionId, CancellationToken ct) =>
        AnswerSuggestionAsync(ApiRoutes.WorkItem(workItemId, "suggestion/dismiss"), sessionId, ct);

    async Task<bool> AnswerSuggestionAsync(string route, string sessionId, CancellationToken ct) {
        var recorded = await SendAsync(route,
            (client, url) => client.PostAsJsonAsync(url, new WorkItemSessionRequestDto(sessionId), Json.WorkItemSessionRequestDto, ct), null, ct).ConfigureAwait(false);
        if (!recorded) return false;
        MarkSuggested(sessionId, false);
        _changed.OnNext(Unit.Default);
        return true;
    }

    void MarkSuggested(string sessionId, bool suggested) {
        if (SessionIds.Canonical(sessionId) is not { } id) return;
        lock (_suggestedGate) {
            var current = _suggested.Value;
            if (current.Contains(id) == suggested) return;
            _suggested.OnNext(suggested ? current.Append(id).ToFrozenSet(StringComparer.Ordinal)
                : current.Where(s => s != id).ToFrozenSet(StringComparer.Ordinal));
        }
    }

    Task<bool> PostAsync<T>(string route, T body, JsonTypeInfo<T> type, CancellationToken ct) =>
        ChangeAsync(route, (client, url) => client.PostAsJsonAsync(url, body, type, ct), null, ct);

    async Task<bool> ChangeAsync(string route, Func<HttpClient, string, Task<HttpResponseMessage>> send,
            Func<HttpResponseMessage, Task>? read, CancellationToken ct) {
        var recorded = await SendAsync(route, send, read, ct).ConfigureAwait(false);
        if (recorded) _changed.OnNext(Unit.Default);
        return recorded;
    }

    async Task<bool> SendAsync(string route, Func<HttpClient, string, Task<HttpResponseMessage>> send,
            Func<HttpResponseMessage, Task>? read, CancellationToken ct) {
        if (profiles.Resolution.ServerUrl is not { Length: > 0 } server) return false;
        try {
            var (client, status, _, _) = await http.ForWaitAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return false;
                using var response = await send(client, $"{server.TrimEnd('/')}/{route}").ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return false;
                if (read is not null) await read(response).ConfigureAwait(false);
                return true;
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException) {
            return false;
        }
    }
}
