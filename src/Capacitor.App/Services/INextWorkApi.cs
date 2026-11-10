using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// <summary>The next-work surface the web Home band reads and writes, for the app profile's own viewer. Mutations
/// answer whether the server recorded the change.</summary>
public interface INextWorkApi {
    Task<NextWorkRead<NextWorkSnapshotDto>>        SnapshotAsync(CancellationToken ct);
    Task<bool>                                     DismissAsync(string targetKey, string? repoHash, CancellationToken ct);
    Task<bool>                                     CloseLooseEndAsync(string looseEndId, CancellationToken ct);
    Task<bool>                                     DeferLooseEndAsync(string looseEndId, DateTimeOffset until, CancellationToken ct);
    Task<NextWorkRead<NextWorkLaunchPreparationDto>> PrepareLaunchAsync(string targetKey, CancellationToken ct);
    Task<NextWorkRead<NextWorkLaunchResultDto>>    LaunchAsync(NextWorkLaunchRequestDto request, CancellationToken ct);

    /// <summary>An absolute address for a path the server returned, or null when no server is configured.</summary>
    string? Resolve(string pathOrUrl);
}
