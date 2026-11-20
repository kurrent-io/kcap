namespace Capacitor.App.Services;

public delegate Task<StreamEventsFetch> SubagentEventsReader(string sessionId, string agentId, long from, CancellationToken ct);
