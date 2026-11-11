namespace Capacitor.Cli.Daemon.Services;

/// <summary>The body of <c>POST /attach</c>: the mod's session, its Claude process as <c>pid:start time</c>, and after a
/// session change the agent that process attached as before.</summary>
internal sealed record SteeringAttachRequest(string? SessionId, string? ProcessKey, string? ReplacesAgentId);
