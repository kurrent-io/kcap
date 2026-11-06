using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// Each call leases its client, so a token the hub refreshed is what HTTP sends too.
public sealed class NextWorkApi(ICapacitorHttpClient? http, ProfileContext? profiles) : INextWorkApi {
    static RemoteModelsJsonContext Json => RemoteModelsJsonContext.Default;

    public Task<NextWorkRead<NextWorkSnapshotDto>> SnapshotAsync(CancellationToken ct) =>
        GetAsync(ApiRoutes.NextWorkSnapshot(null), Json.NextWorkSnapshotDto, ct);

    public Task<bool> DismissAsync(string targetKey, string? repoHash, CancellationToken ct) =>
        PostAsync(ApiRoutes.NextWorkDismissals, new NextWorkTargetRequestDto(targetKey, repoHash), Json.NextWorkTargetRequestDto, ct);

    public Task<bool> CloseLooseEndAsync(string looseEndId, CancellationToken ct) =>
        PostAsync(ApiRoutes.LooseEndClose, new LooseEndCloseRequestDto(looseEndId), Json.LooseEndCloseRequestDto, ct);

    public Task<bool> DeferLooseEndAsync(string looseEndId, DateTimeOffset until, CancellationToken ct) =>
        PostAsync(ApiRoutes.LooseEndDefer, new LooseEndDeferRequestDto(looseEndId, until), Json.LooseEndDeferRequestDto, ct);

    public Task<NextWorkRead<NextWorkLaunchPreparationDto>> PrepareLaunchAsync(string targetKey, CancellationToken ct) =>
        ExchangeAsync(ApiRoutes.NextWorkLaunchPrepare, new NextWorkLaunchPrepareRequestDto(targetKey),
            Json.NextWorkLaunchPrepareRequestDto, Json.NextWorkLaunchPreparationDto, ct);

    public Task<NextWorkRead<NextWorkLaunchResultDto>> LaunchAsync(NextWorkLaunchRequestDto request, CancellationToken ct) =>
        ExchangeAsync(ApiRoutes.NextWorkLaunch, request, Json.NextWorkLaunchRequestDto, Json.NextWorkLaunchResultDto, ct);

    public string? Resolve(string pathOrUrl) {
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") return absolute.ToString();
        if (!pathOrUrl.StartsWith('/') || pathOrUrl.StartsWith("//", StringComparison.Ordinal)) return null;
        return profiles?.Resolution.ServerUrl is { Length: > 0 } server ? server.TrimEnd('/') + pathOrUrl : null;
    }

    Task<NextWorkRead<T>> GetAsync<T>(string route, JsonTypeInfo<T> type, CancellationToken ct) where T : class =>
        SendAsync(route, client => client.GetAsync(Url(route), ct), type, ct);

    Task<NextWorkRead<TResponse>> ExchangeAsync<TRequest, TResponse>(string route, TRequest body, JsonTypeInfo<TRequest> request,
            JsonTypeInfo<TResponse> response, CancellationToken ct) where TResponse : class =>
        SendAsync(route, client => client.PostAsJsonAsync(Url(route), body, request, ct), response, ct);

    async Task<bool> PostAsync<TRequest>(string route, TRequest body, JsonTypeInfo<TRequest> request, CancellationToken ct) =>
        (await SendAsync<object>(route, client => client.PostAsJsonAsync(Url(route), body, request, ct), null, ct).ConfigureAwait(false))
            .Kind == NextWorkReadKind.Ready;

    string Url(string route) => $"{profiles!.Resolution.ServerUrl!.TrimEnd('/')}/{route}";

    /// A null <paramref name="type"/> reads only the status.
    async Task<NextWorkRead<T>> SendAsync<T>(string route, Func<HttpClient, Task<HttpResponseMessage>> send, JsonTypeInfo<T>? type,
            CancellationToken ct) where T : class {
        if (http is null || string.IsNullOrEmpty(profiles?.Resolution.ServerUrl)) return NextWorkRead<T>.SignedOut;
        try {
            var (client, status, _, _) = await http.ForWaitAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return NextWorkRead<T>.SignedOut;
                using var response = await send(client).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized) return NextWorkRead<T>.SignedOut;
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed)
                    return NextWorkRead<T>.Unavailable;
                if (!response.IsSuccessStatusCode) return NextWorkRead<T>.Failed;
                if (type is null) return new(NextWorkReadKind.Ready);
                var body = await response.Content.ReadFromJsonAsync(type, ct).ConfigureAwait(false);
                return body is null ? NextWorkRead<T>.Failed : new(NextWorkReadKind.Ready, body);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: next-work {route} failed: {ex.Message}");
            return NextWorkRead<T>.Failed;
        }
    }
}
