using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Client;

/// One read of a session's work context, however the app reaches the server.
public interface IWorkContextSource {
    Task<WorkContextRead> ReadAsync(string sessionId, CancellationToken ct);
    /// The last read this viewer may still be shown, to fill a pane while it reads again.
    WorkContextRead? Cached(string sessionId);
    /// Forgets every read and client the current credential produced, so the next read goes out as whoever is signed in now.
    void InvalidateAuthentication();
}
