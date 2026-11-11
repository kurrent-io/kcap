namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>One Claude process attached to one session, as the server registered it. <paramref name="SessionId"/> is
/// as the mod gave it; <paramref name="ReplacesAgentId"/> is sent on every announcement, as the server asks.</summary>
internal sealed record ModAttachmentRecord(
        string         AgentId,
        string         SessionId,
        string         ProcessKey,
        string?        ReplacesAgentId,
        DateTimeOffset AttachedAt
    );
