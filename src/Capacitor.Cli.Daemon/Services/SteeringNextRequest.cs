namespace Capacitor.Cli.Daemon.Services;

/// <summary>The body of <c>POST /next</c>, sent on every poll. <see cref="Outstanding"/> is the dispatch the mod holds,
/// if any. <see cref="ProcessKey"/> is required: two processes can share a session, and so its agent id, and only the
/// attached one is answered.</summary>
internal sealed record SteeringNextRequest(string? AgentId, bool Idle, string? Outstanding, string? ProcessKey);
