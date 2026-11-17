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

    public async Task<WorkItemPartsDto?> ReadAsync(string workItemId, CancellationToken ct) {
        WorkItemPartsDto? body = null;
        await SendAsync(ApiRoutes.WorkItem(workItemId, "parts"), (client, url) => client.GetAsync(url, ct),
            async response => body = await response.Content.ReadFromJsonAsync(Json.WorkItemPartsDto, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return body;
    }

    public Task<bool> SetStatusAsync(string workItemId, string? status, CancellationToken ct) =>
        PostAsync(ApiRoutes.WorkItem(workItemId, "status"), new WorkItemStatusRequestDto(status), Json.WorkItemStatusRequestDto, ct);

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

    public Task<bool> AddLinkAsync(string workItemId, string reference, CancellationToken ct) =>
        PostAsync(ApiRoutes.WorkItem(workItemId, "links"), new WorkItemLinkRequestDto(reference), Json.WorkItemLinkRequestDto, ct);

    public Task<bool> MoveSessionAsync(string workItemId, string sessionId, CancellationToken ct) =>
        PostAsync(ApiRoutes.WorkItem(workItemId, "sessions"), new WorkItemSessionRequestDto(sessionId), Json.WorkItemSessionRequestDto, ct);

    public Task<bool> RemoveSessionAsync(string workItemId, string sessionId, CancellationToken ct) =>
        PostAsync(ApiRoutes.WorkItem(workItemId, "sessions/remove"), new WorkItemSessionRequestDto(sessionId), Json.WorkItemSessionRequestDto, ct);

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
