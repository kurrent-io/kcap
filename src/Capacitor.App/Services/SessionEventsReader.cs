namespace Capacitor.App.Services;

/// One page of a session's own events from a count of events already received.
public delegate Task<SubagentEventsFetch> SessionEventsReader(string sessionId, long from, CancellationToken ct);
