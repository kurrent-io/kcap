using System.Net;
using System.Net.Http.Json;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// The projects the signed-in person owns or belongs to, for the rail's switcher.
public static class ServerProjectsHttp {
    /// Null when the read says nothing (signed out, offline, a failed status), so the rail keeps what it shows.
    /// A tenant whose plan has no projects, or a server with none, answers an empty list.
    public static Func<CancellationToken, Task<IReadOnlyList<RailProject>?>> Reader(ICapacitorHttpClient? http, ProfileContext? profiles) => async ct => {
        var serverUrl = profiles?.Resolution.ServerUrl;
        if (http is null || profiles is null || string.IsNullOrEmpty(serverUrl)) return null;
        try {
            var (client, status, _, _) = await http.ForWaitAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return null;
                using var response = await client.GetAsync($"{serverUrl.TrimEnd('/')}/{ApiRoutes.Projects}", ct).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return [];
                if (!response.IsSuccessStatusCode) return null;
                var rows = await response.Content.ReadFromJsonAsync(RemoteModelsJsonContext.Default.ListProjectSummaryDto, ct).ConfigureAwait(false);
                return rows is null ? null : Map(rows);
            }
        } catch (HttpRequestException) {
            return null;
        }
    };

    internal static IReadOnlyList<RailProject> Map(IEnumerable<ProjectSummaryDto> rows) =>
        [.. rows.Where(row => row.ViewerMembership is "owner" or "member").Select(row => new RailProject(row.ProjectId, row.Name, row.RepoHashes))];
}
