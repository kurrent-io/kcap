namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>What became of a dispatch the mod was handed, kept until the server takes it. <paramref name="AgentId"/> is
/// the agent the dispatch was sent to.</summary>
internal sealed record ModInputReport(
        string  Kind,
        Guid    DispatchId,
        string  AgentId,
        string? SessionId = null,
        Guid?   RowUuid   = null,
        string? Reason    = null
    ) {
    public const string Link        = "link";
    public const string Rejected    = "rejected";
    public const string Unconfirmed = "unconfirmed";
}
