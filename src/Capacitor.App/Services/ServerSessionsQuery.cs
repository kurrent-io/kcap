namespace Capacitor.App.Services;

/// One page of the server's session listing: State is "active", "ended" or "all", Owner is "me", a user
/// id, or null for everyone visible, and ProjectId narrows it to one project's sessions.
public sealed record ServerSessionsQuery(string State, string? Owner, int Limit, int Offset, string? ProjectId = null);
