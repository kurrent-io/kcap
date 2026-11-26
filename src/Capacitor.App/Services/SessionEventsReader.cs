namespace Capacitor.App.Services;

/// One page of a session's root stream: at most limit events, or the server's own page size when
/// null, starting from-many events past its oldest one. A tail asks for the stream's last tail events
/// instead, never before from; a server that does not know it answers as if only from were given, with
/// no first_from on the page.
public delegate Task<StreamEventsFetch> SessionEventsReader(string sessionId, long from, int? limit, int? tail, CancellationToken ct);
