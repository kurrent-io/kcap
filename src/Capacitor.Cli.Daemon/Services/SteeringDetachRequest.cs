namespace Capacitor.Cli.Daemon.Services;

/// <summary>The body of <c>POST /detach</c>, sent when the session ends with its process. Not after <c>/clear</c> or a
/// resume, where the next attach names this agent as the one it replaces.</summary>
internal sealed record SteeringDetachRequest(string? AgentId, string? ProcessKey);
