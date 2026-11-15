using Capacitor.Cli.Core.Plans;

namespace Capacitor.Client;

/// One read of the plans a session works from, however the app reaches the server.
public interface IPlanSource {
    Task<SessionPlansRead> ReadAsync(string sessionId, CancellationToken ct);
    /// The last read this viewer may still be shown, to fill a pane while it reads again.
    SessionPlansRead? Cached(string sessionId);
    /// Forgets every read and client the current credential produced, so the next read goes out as whoever is signed in now.
    void InvalidateAuthentication();
}
