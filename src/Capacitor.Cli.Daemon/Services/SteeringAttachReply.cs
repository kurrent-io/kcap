namespace Capacitor.Cli.Daemon.Services;

/// <summary>The answer to <c>POST /attach</c>: <c>{agentId, pollIntervalMs}</c>, or <c>{refused, retry}</c>. A refusal
/// with <see cref="Retry"/> false leaves the mod inert until its process or session changes.</summary>
internal sealed record SteeringAttachReply(string? AgentId = null, int? PollIntervalMs = null, string? Refused = null, bool? Retry = null) {
    public static SteeringAttachReply Attached(string agentId, TimeSpan pollInterval) => new(agentId, (int)pollInterval.TotalMilliseconds);

    public static SteeringAttachReply Refusal(string reason, bool retry) => new(Refused: reason, Retry: retry);
}
