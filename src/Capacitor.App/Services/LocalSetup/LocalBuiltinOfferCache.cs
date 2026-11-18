using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Services.LocalSetup;

/// One lookup per version, shared by every caller. A lookup that failed or answered none is asked again next time, so a
/// lapse in the network does not hide the offer for the app's lifetime.
public sealed class LocalBuiltinOfferCache(Func<string, Task<LocalEmbeddingModelManifest?>> lookup) {
    readonly Lock _gate = new();
    readonly Dictionary<string, Task<LocalEmbeddingModelManifest?>> _lookups = [];

    /// A caller's cancellation abandons its wait, never the shared lookup another caller may be waiting on.
    public Task<LocalEmbeddingModelManifest?> LookupAsync(string version, CancellationToken ct) {
        Task<LocalEmbeddingModelManifest?> task;

        lock (_gate) {
            if (!_lookups.TryGetValue(version, out var kept) || kept is { IsCompleted: true } and not { IsCompletedSuccessfully: true, Result: not null })
                _lookups[version] = kept = Task.Run(() => lookup(version), CancellationToken.None);
            task = kept;
        }

        return task.WaitAsync(ct);
    }
}
