namespace Capacitor.Cli.Daemon.Services;

/// <summary>The body of <c>POST /link</c>: a dispatch landed as the user row <see cref="RowUuid"/> in
/// <see cref="SessionId"/>, which after a session change is not the session the dispatch was sent to.</summary>
internal sealed record SteeringLinkRequest(string? AgentId, string? DispatchId, string? SessionId, string? RowUuid);
