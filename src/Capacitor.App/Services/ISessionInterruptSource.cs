namespace Capacitor.App.Services;

/// A session's unresolved interrupts as of a read started by the call.
public interface ISessionInterruptSource {
    Task<SessionInterruptsFetch> ReadAsync(string sessionId, CancellationToken ct);

    /// Drops everything read so far, a read in flight included; called when the signed-in account changes.
    void Forget();
}
