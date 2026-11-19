using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit.Services;

/// Serves each read as a whole-session snapshot, so a reconciler test states what the stream holds now.
sealed class SnapshotInterruptSource(Func<string, Task<SessionDetailFetch>> read) : ISessionInterruptSource {
    public int Forgets;

    public async Task<SessionInterruptsFetch> ReadAsync(string sessionId, CancellationToken ct) {
        var fetch = await read(sessionId).ConfigureAwait(false);
        return fetch.Detail is { } detail ? new(InterruptReconciliation.FromDetail(detail)) : new(null, fetch.NotFound);
    }

    public void Forget() => Forgets++;

    public void Dispose() { }
}
