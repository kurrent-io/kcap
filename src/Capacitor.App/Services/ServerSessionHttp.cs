using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// The app's session-scoped HTTP calls, each on a client leased per call so a token the
/// hub refreshed is what HTTP sends too. Every failure is a value; nothing here throws past a
/// cancellation.
public static class ServerSessionHttp {
    public static PermissionResponder Responder(ICapacitorHttpClient? http, ProfileContext? profiles) => async (sessionId, requestId, payload, ct) => {
        var serverUrl = profiles?.Resolution.ServerUrl;
        if (http is null || profiles is null || string.IsNullOrEmpty(serverUrl)) return new(ServerRespondKind.Unreachable, "not_signed_in");
        try {
            var (client, status, _, _) = await http.ForWaitAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return new(ServerRespondKind.Unauthorized, "not_signed_in");
                var url = $"{serverUrl.TrimEnd('/')}/{ApiRoutes.PermissionResponse(sessionId, requestId)}";
                using var content = JsonContent.Create(payload, RemoteModelsJsonContext.Default.PermissionResponsePayload);
                using var response = await client.PostAsync(url, content, ct).ConfigureAwait(false);
                return response.StatusCode switch {
                    HttpStatusCode.OK or HttpStatusCode.NoContent => new(ServerRespondKind.Applied),
                    HttpStatusCode.NotFound => new(ServerRespondKind.NotPending),
                    HttpStatusCode.Unauthorized => new(ServerRespondKind.Unauthorized, "not_signed_in"),
                    HttpStatusCode.BadRequest => new(ServerRespondKind.Rejected, await ReasonAsync(response, ct).ConfigureAwait(false)),
                    _ => new(ServerRespondKind.Unreachable, $"server_status_{(int)response.StatusCode}"),
                };
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            return new(ServerRespondKind.Unreachable, ex.Message);
        }
    };

    public static SubagentEventsReader SubagentEventsReader(ICapacitorHttpClient? http, ProfileContext? profiles) => (sessionId, agentId, from, ct) =>
        ReadPageAsync(http, profiles, ApiRoutes.SubagentEvents(sessionId, agentId, from), ct);

    public static SessionEventsReader SessionEventsReader(ICapacitorHttpClient? http, ProfileContext? profiles) => (sessionId, from, limit, ct) =>
        ReadPageAsync(http, profiles, limit is { } max ? ApiRoutes.SessionEvents(sessionId, from, max) : ApiRoutes.SessionEvents(sessionId, from), ct);

    static async Task<StreamEventsFetch> ReadPageAsync(ICapacitorHttpClient? http, ProfileContext? profiles, string route, CancellationToken ct) {
        var serverUrl = profiles?.Resolution.ServerUrl;
        if (http is null || profiles is null || string.IsNullOrEmpty(serverUrl)) return new(null);
        try {
            var (client, status, _, _) = await http.ForWaitAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return new(null, Unauthorized: true);
                using var response = await client.GetAsync($"{serverUrl.TrimEnd('/')}/{route}", ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound) return new(null, NotFound: true);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new(null, Unauthorized: true);
                if (!response.IsSuccessStatusCode) return new(null, Failure: $"HTTP {(int)response.StatusCode}");
                return new(await response.Content.ReadFromJsonAsync(RemoteModelsJsonContext.Default.StreamEventsPage, ct).ConfigureAwait(false));
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            return new(null, Failure: ex.Message);
        }
    }

    static async Task<string> ReasonAsync(HttpResponseMessage response, CancellationToken ct) {
        try {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Prop("error")?.GetString() ?? text;
        } catch (Exception) when (!ct.IsCancellationRequested) {
            return "rejected";
        }
    }
}
