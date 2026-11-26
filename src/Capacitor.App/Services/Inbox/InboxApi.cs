using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services.Inbox;

/// Each call leases its client, so a token the hub refreshed is what HTTP sends too.
public sealed class InboxApi(ICapacitorHttpClient? http, ProfileContext? profiles) : IInboxApi {
    static RemoteModelsJsonContext Json => RemoteModelsJsonContext.Default;

    public Task<InboxRead<UserSurfacesResponseDto>> SurfacesAsync(CancellationToken ct) =>
        SendAsync(ApiRoutes.UserSurfaces, c => c.GetAsync(Url(ApiRoutes.UserSurfaces), ct), Json.UserSurfacesResponseDto, ct);

    public Task<InboxRead<NotificationUnreadCountDto>> UnreadCountAsync(CancellationToken ct) =>
        SendAsync(ApiRoutes.NotificationsUnreadCount, c => c.GetAsync(Url(ApiRoutes.NotificationsUnreadCount), ct),
            Json.NotificationUnreadCountDto, ct);

    public Task<InboxRead<NotificationPageDto>> InboxAsync(int limit, CancellationToken ct) {
        var route = ApiRoutes.Notifications(limit);
        return SendAsync(route, c => c.GetAsync(Url(route), ct), Json.NotificationPageDto, ct);
    }

    public Task<bool> MarkReadAsync(string logicalId, long version, CancellationToken ct) =>
        PostAsync(ApiRoutes.Notification(logicalId, "read"), new NotificationVersionRequestDto(version), Json.NotificationVersionRequestDto, ct);

    public Task<bool> DismissAsync(string logicalId, long version, CancellationToken ct) =>
        PostAsync(ApiRoutes.Notification(logicalId, "dismiss"), new NotificationVersionRequestDto(version), Json.NotificationVersionRequestDto, ct);

    public Task<bool> MarkAllReadAsync(CancellationToken ct) => StatusAsync(ApiRoutes.NotificationsReadAll, c => c.PostAsync(Url(ApiRoutes.NotificationsReadAll), null, ct), ct);

    public Task<bool> DismissAllAsync(CancellationToken ct) => StatusAsync(ApiRoutes.NotificationsDismissAll, c => c.PostAsync(Url(ApiRoutes.NotificationsDismissAll), null, ct), ct);

    public Task<InboxRead<NotificationAnswerResponseDto>> AnswerAsync(string logicalId, string answer, string? text, CancellationToken ct) {
        var route = ApiRoutes.Notification(logicalId, "answer");
        return SendAsync(route, c => c.PostAsJsonAsync(Url(route), new NotificationAnswerRequestDto(answer, text), Json.NotificationAnswerRequestDto, ct),
            Json.NotificationAnswerResponseDto, ct);
    }

    public Task<InboxRead<NotificationPrefsDto>> PrefsAsync(CancellationToken ct) =>
        SendAsync(ApiRoutes.NotificationPrefs, c => c.GetAsync(Url(ApiRoutes.NotificationPrefs), ct), Json.NotificationPrefsDto, ct);

    public Task<bool> PutPrefAsync(string type, bool enabled, bool slackEnabled, CancellationToken ct) {
        var route = ApiRoutes.NotificationPref(type);
        return StatusAsync(route, c => c.PutAsJsonAsync(Url(route), new NotificationPrefUpdateRequestDto(enabled, slackEnabled),
            Json.NotificationPrefUpdateRequestDto, ct), ct);
    }

    public Task<InboxRead<PassControlsDto>> PassControlsAsync(CancellationToken ct) =>
        SendAsync(ApiRoutes.PassControls, c => c.GetAsync(Url(ApiRoutes.PassControls), ct), Json.PassControlsDto, ct);

    public Task<InboxRead<PassControlsDto>> SetAgentOptOutAsync(bool optedOut, CancellationToken ct) =>
        SendAsync(ApiRoutes.PassAgentOptOut, c => c.PutAsJsonAsync(Url(ApiRoutes.PassAgentOptOut), new PassAgentOptOutRequestDto(optedOut),
            Json.PassAgentOptOutRequestDto, ct), Json.PassControlsDto, ct);

    public Task<InboxRead<PassControlsDto>> UnmuteAsync(string senderId, CancellationToken ct) {
        var route = ApiRoutes.PassMute(senderId);
        return SendAsync(route, c => c.DeleteAsync(Url(route), ct), Json.PassControlsDto, ct);
    }

    public string? Resolve(string pathOrUrl) {
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") return absolute.ToString();
        if (!pathOrUrl.StartsWith('/') || pathOrUrl.StartsWith("//", StringComparison.Ordinal)) return null;
        return profiles?.Resolution.ServerUrl is { Length: > 0 } server ? server.TrimEnd('/') + pathOrUrl : null;
    }

    Task<bool> PostAsync<TRequest>(string route, TRequest body, JsonTypeInfo<TRequest> request, CancellationToken ct) =>
        StatusAsync(route, c => c.PostAsJsonAsync(Url(route), body, request, ct), ct);

    async Task<bool> StatusAsync(string route, Func<HttpClient, Task<HttpResponseMessage>> send, CancellationToken ct) =>
        (await SendAsync<object>(route, send, null, ct).ConfigureAwait(false)).Kind == InboxReadKind.Ready;

    string Url(string route) => $"{profiles!.Resolution.ServerUrl!.TrimEnd('/')}/{route}";

    /// A null <paramref name="type"/> reads only the status.
    async Task<InboxRead<T>> SendAsync<T>(string route, Func<HttpClient, Task<HttpResponseMessage>> send, JsonTypeInfo<T>? type,
            CancellationToken ct) where T : class {
        if (http is null || string.IsNullOrEmpty(profiles?.Resolution.ServerUrl)) return InboxRead<T>.SignedOut;
        try {
            var (client, status, _, _) = await http.ForProtectedReadAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return InboxRead<T>.SignedOut;
                using var response = await send(client).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized) return InboxRead<T>.SignedOut;
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) return InboxRead<T>.Unavailable;
                if (!response.IsSuccessStatusCode) return InboxRead<T>.Failed;
                if (type is null) return new(InboxReadKind.Ready);
                var body = await response.Content.ReadFromJsonAsync(type, ct).ConfigureAwait(false);
                return body is null ? InboxRead<T>.Failed : new(InboxReadKind.Ready, body);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: inbox {route} failed: {ex.Message}");
            return InboxRead<T>.Failed;
        }
    }
}
