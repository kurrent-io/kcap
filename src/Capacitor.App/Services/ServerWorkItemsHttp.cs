using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Json;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// A project's open work items, for the rail's work item grouping.
public static class ServerWorkItemsHttp {
    /// Null when the read says nothing (signed out, offline, a failed status), so the rail keeps what it shows.
    /// A server or tenant without work items version 2 answers not found.
    public static Func<string, CancellationToken, Task<RailWorkItems?>> Reader(ICapacitorHttpClient? http, ProfileContext? profiles) => async (projectId, ct) => {
        var serverUrl = profiles?.Resolution.ServerUrl;
        if (http is null || profiles is null || string.IsNullOrEmpty(serverUrl)) return null;
        try {
            var (client, status, _, _) = await http.ForWaitAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return null;
                using var response = await client.GetAsync($"{serverUrl.TrimEnd('/')}/{ApiRoutes.ProjectWorkItems(projectId)}", ct).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return RailWorkItems.Unavailable;
                if (!response.IsSuccessStatusCode) return null;
                var body = await response.Content.ReadFromJsonAsync(RemoteModelsJsonContext.Default.ProjectWorkItemsResponseDto, ct).ConfigureAwait(false);
                return body is null ? null : new RailWorkItems(true, Map(body.WorkItems));
            }
        } catch (HttpRequestException) {
            return null;
        }
    };

    internal static IReadOnlyList<RailWorkItem> Map(IEnumerable<ProjectWorkItemDto> items) =>
        [.. items.Select(item => {
            var parts = item.Parts.ToDictionary(part => part.WorkItemId, part => part.Title, StringComparer.Ordinal);
            var bySession = item.Sessions.DistinctBy(session => session.SessionId).ToFrozenDictionary(
                session => session.SessionId,
                session => session.PartId is { } part ? parts.GetValueOrDefault(part, RailWorkItem.NoPart) : RailWorkItem.NoPart,
                StringComparer.Ordinal);
            var people = item.Sessions.Select(session => session.Owner).OfType<RepoSessionOwnerDto>().DistinctBy(owner => owner.UserId)
                .Select(owner => new WorkItemPerson(owner.UserId, owner.DisplayName ?? owner.Username, owner.AvatarUrl)).ToList();
            return new RailWorkItem(item.WorkItemId, item.Title, item.PartsDone, item.PartsTotal, bySession,
                Status: WorkStatusExtensions.ParseWorkStatus(item.Status), People: people);
        })];
}
