using System.Net;
using System.Net.Http.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;

namespace Capacitor.App.Services.Driver;

/// <summary>Leases its client per call, like <see cref="NextWorkApi"/>. A 404 means the server has no work items version 2.</summary>
public sealed class DriverApi(ICapacitorHttpClient? http, ProfileContext? profiles) : IDriverApi {
    const string Route = "api/driver/work-items";

    public async Task<NextWorkRead<DriverWorkItemsDto>> WorkItemsAsync(CancellationToken ct) {
        if (http is null || profiles?.Resolution.ServerUrl is not { Length: > 0 } server) return NextWorkRead<DriverWorkItemsDto>.SignedOut;
        try {
            var (client, status, _, _) = await http.ForProtectedReadAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return NextWorkRead<DriverWorkItemsDto>.SignedOut;
                using var response = await client.GetAsync($"{server.TrimEnd('/')}/{Route}", ct).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized) return NextWorkRead<DriverWorkItemsDto>.SignedOut;
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden) return NextWorkRead<DriverWorkItemsDto>.Unavailable;
                if (!response.IsSuccessStatusCode) return NextWorkRead<DriverWorkItemsDto>.Failed;
                var body = await response.Content.ReadFromJsonAsync(DriverJsonContext.Default.DriverWorkItemsDto, ct).ConfigureAwait(false);
                return body is null ? NextWorkRead<DriverWorkItemsDto>.Failed : new(NextWorkReadKind.Ready, body);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: driver read failed: {ex.Message}");
            return NextWorkRead<DriverWorkItemsDto>.Failed;
        }
    }
}
