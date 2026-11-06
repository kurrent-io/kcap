namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>Restart frame modes a client sends only under a capability; "now", "when-idle" and "force" need none.</summary>
public static class RestartIpc {
    /// <summary>Restarts the way a daemon that sees its own update does: once every interactive agent is at a turn
    /// boundary, or when its turn wait runs out, resuming the agents.</summary>
    public const string TurnBoundaryMode = "turn-boundary";

    /// <summary>Advertised by a daemon that knows <see cref="TurnBoundaryMode"/>. One that does not reads an unknown
    /// mode as a bare restart, which does not wait for turns, so a client never sends the mode without it.</summary>
    public const string TurnBoundaryCapability = "restart/2";
}
