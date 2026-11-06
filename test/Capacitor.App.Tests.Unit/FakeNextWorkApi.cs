using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

/// Serves a fixed snapshot and records every change asked of it.
sealed class FakeNextWorkApi : INextWorkApi {
    public NextWorkRead<NextWorkSnapshotDto> Snapshot { get; set; } = NextWorkRead<NextWorkSnapshotDto>.Unavailable;
    public NextWorkRead<NextWorkLaunchPreparationDto> Preparation { get; set; } = NextWorkRead<NextWorkLaunchPreparationDto>.Failed;
    public NextWorkRead<NextWorkLaunchResultDto> Launch { get; set; } = NextWorkRead<NextWorkLaunchResultDto>.Failed;
    public bool Accepts { get; set; } = true;
    public string ServerUrl { get; set; } = "https://kcap.test";

    public List<string> Calls { get; } = [];
    public List<NextWorkLaunchRequestDto> Launches { get; } = [];
    public int SnapshotReads { get; private set; }

    public Task<NextWorkRead<NextWorkSnapshotDto>> SnapshotAsync(CancellationToken ct) {
        SnapshotReads++;
        return Task.FromResult(Snapshot);
    }

    public Task<bool> DismissAsync(string targetKey, string? repoHash, CancellationToken ct) => Record($"dismiss {targetKey} {repoHash}");
    public Task<bool> CloseLooseEndAsync(string looseEndId, CancellationToken ct) => Record($"close {looseEndId}");
    public Task<bool> DeferLooseEndAsync(string looseEndId, DateTimeOffset until, CancellationToken ct) => Record($"defer {looseEndId} {until:O}");

    public Task<NextWorkRead<NextWorkLaunchPreparationDto>> PrepareLaunchAsync(string targetKey, CancellationToken ct) {
        Calls.Add($"prepare {targetKey}");
        return Task.FromResult(Preparation);
    }

    public Task<NextWorkRead<NextWorkLaunchResultDto>> LaunchAsync(NextWorkLaunchRequestDto request, CancellationToken ct) {
        Launches.Add(request);
        return Task.FromResult(Launch);
    }

    public string? Resolve(string pathOrUrl) =>
        Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? pathOrUrl
        : pathOrUrl.StartsWith('/') ? ServerUrl + pathOrUrl : null;

    Task<bool> Record(string call) {
        Calls.Add(call);
        return Task.FromResult(Accepts);
    }
}
