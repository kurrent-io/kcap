namespace Capacitor.App.Services;

/// A session's unresolved interrupts as of a read started by the call.
/// Owned by the reconciler it is given to, which disposes it.
public interface ISessionInterruptSource : IDisposable {
    Task<SessionInterruptsFetch> ReadAsync(string sessionId, CancellationToken ct);

    /// Drops everything read so far, a read in flight included; called when the signed-in account changes.
    void Forget();
}
