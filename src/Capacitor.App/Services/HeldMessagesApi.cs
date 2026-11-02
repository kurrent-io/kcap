using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// Each call leases its client, so a token the hub refreshed is what HTTP sends too.
public sealed class HeldMessagesApi(ICapacitorHttpClient? http, ProfileContext? profiles) : IHeldMessagesApi {
    public async Task<HeldMessagesRead> ListAsync(string agentId, CancellationToken ct) {
        var serverUrl = profiles?.Resolution.ServerUrl;
        if (http is null || string.IsNullOrEmpty(serverUrl)) return HeldMessagesRead.Failed;
        try {
            var (client, status, _, _) = await http.ForWaitAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return HeldMessagesRead.Failed;
                using var response = await client.GetAsync($"{serverUrl.TrimEnd('/')}/{ApiRoutes.HeldMessages(agentId)}", ct).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden) return HeldMessagesRead.None;
                if (!response.IsSuccessStatusCode) return HeldMessagesRead.Failed;
                var body = await response.Content.ReadFromJsonAsync(RemoteModelsJsonContext.Default.ListHeldMessagesResponseDto, ct).ConfigureAwait(false);
                return new(body?.Messages ?? []);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: held messages read failed: {ex.Message}");
            return HeldMessagesRead.Failed;
        }
    }

    public async Task<HeldMessageDecisionOutcome> DecideAsync(string agentId, Guid dispatchId, HeldMessageDecision decision, CancellationToken ct) {
        var serverUrl = profiles?.Resolution.ServerUrl;
        if (http is null || string.IsNullOrEmpty(serverUrl)) return new(HeldMessageDecisionKind.Unauthorized);
        try {
            var (client, status, _, _) = await http.ForWaitAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return new(HeldMessageDecisionKind.Unauthorized);
                var url = $"{serverUrl.TrimEnd('/')}/{ApiRoutes.HeldMessageDecision(agentId, dispatchId, decision.RouteSegment())}";
                using var response = await client.PostAsync(url, null, ct).ConfigureAwait(false);
                switch (response.StatusCode) {
                    case HttpStatusCode.Conflict:
                        return await ErrorAsync(response, ct).ConfigureAwait(false) is { Error: WireTokens.HeldQuestionPending } pending
                            ? new(HeldMessageDecisionKind.QuestionPending, Message: pending.Message)
                            : new(HeldMessageDecisionKind.AlreadyDecided);
                    case HttpStatusCode.NotFound: return new(HeldMessageDecisionKind.NotFound);
                    case HttpStatusCode.Forbidden: return new(HeldMessageDecisionKind.NotOwner);
                    case HttpStatusCode.Unauthorized: return new(HeldMessageDecisionKind.Unauthorized);
                }
                if (!response.IsSuccessStatusCode) return new(HeldMessageDecisionKind.Unreachable);
                var body = await response.Content.ReadFromJsonAsync(RemoteModelsJsonContext.Default.HeldMessageDecisionResponseDto, ct).ConfigureAwait(false);
                return new(HeldMessageDecisionKind.Decided, body?.State);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: held message {decision.RouteSegment()} failed: {ex.Message}");
            return new(HeldMessageDecisionKind.Unreachable);
        }
    }

    static async Task<AgentPrivilegeErrorDto?> ErrorAsync(HttpResponseMessage response, CancellationToken ct) {
        try {
            return await response.Content.ReadFromJsonAsync(RemoteModelsJsonContext.Default.AgentPrivilegeErrorDto, ct).ConfigureAwait(false);
        } catch (JsonException) {
            return null;
        }
    }
}
