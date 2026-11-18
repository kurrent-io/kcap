namespace Capacitor.App.Services;

/// One page of a session's root stream: at most limit events, or the server's own page size when
/// null, starting from-many events past its oldest one.
public delegate Task<StreamEventsFetch> SessionEventsReader(string sessionId, long from, int? limit, CancellationToken ct);
