using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Tests.Helpers;

/// <summary>Hands back a fresh, unauthenticated <see cref="HttpClient"/> per call — for driving an
/// API implementation (<c>FeedbackApi</c>, <c>SessionsApi</c>, ...) directly against a stub server,
/// bypassing the auth-discovery pipeline <c>AddCapacitorHttp</c> wires in production.</summary>
/// <param name="unauthenticated">Answers the anonymous and loopback lanes when given.</param>
/// <param name="background">Answers the background lane when given; the caller keeps it.</param>
public sealed class FixedCapacitorHttpClient(HttpMessageHandler? unauthenticated = null, HttpMessageHandler? background = null) : ICapacitorHttpClient {
    public Task<HttpClient> ForCommandAsync(CancellationToken ct = default) => Task.FromResult(new HttpClient());

    public Task<HttpClient> ForSessionAsync(CancellationToken ct = default) =>
        Task.FromResult(new HttpClient());

    public Task<AuthAttempt> ForHookAsync(CancellationToken ct = default) =>
        Task.FromResult(new AuthAttempt(new HttpClient(), AuthStatus.Ok, null, null));

    public Task<AuthAttempt> ForWaitAsync(CancellationToken ct = default) =>
        Task.FromResult(new AuthAttempt(new HttpClient(), AuthStatus.Ok, null, null));

    public Task<AuthAttempt> ForProtectedReadAsync(CancellationToken ct = default) =>
        Task.FromResult(new AuthAttempt(new HttpClient(), AuthStatus.Ok, null, null));

    public Task<HttpClient> ForBackgroundAsync(CancellationToken ct = default) =>
        Task.FromResult(background is null ? new HttpClient() : new HttpClient(background, disposeHandler: false));

    public Task<HttpClient> ForMemoryAsync(CancellationToken ct = default) =>
        Task.FromResult(new HttpClient());

    public HttpClient Anonymous() => Unauthenticated();

    public HttpClient Loopback() => Unauthenticated();

    HttpClient Unauthenticated() => unauthenticated is null ? new() : new(unauthenticated, disposeHandler: false);

    public HttpClient Bearer() => new();
}
