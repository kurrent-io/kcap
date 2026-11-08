namespace Capacitor.Cli.Daemon.Services;

/// <summary>The body of <c>POST /undelivered</c>. <see cref="Outcome"/> is <c>rejected</c>, when the mod knows the
/// prompt will not land, or <c>unconfirmed</c>, when it cannot tell; <see cref="Reason"/> is a short token, and
/// <c>stopped</c> for a session stopped at the terminal. A beat for the attachment only when <see cref="ProcessKey"/> is
/// its process's.</summary>
internal sealed record SteeringUndeliveredRequest(string? AgentId, string? DispatchId, string? Outcome, string? Reason, string? ProcessKey);
