namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>What became of a dispatch the mod was handed, kept until the server takes it. <paramref name="AgentId"/> is
/// the agent the dispatch was sent to; <paramref name="Refusals"/> how many times the server answered it with an
/// error.</summary>
internal sealed record ModInputReport(
        string  Kind,
        Guid    DispatchId,
        string  AgentId,
        string? SessionId = null,
        Guid?   RowUuid   = null,
        string? Reason    = null,
        int     Refusals  = 0
    ) {
    public const string Link        = "link";
    public const string Rejected    = "rejected";
    public const string Unconfirmed = "unconfirmed";

    /// <summary>Whether the two say the same thing, however often either was refused.</summary>
    public bool SaysTheSameAs(ModInputReport other) => this with { Refusals = 0 } == other with { Refusals = 0 };
}
