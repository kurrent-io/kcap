namespace Capacitor.Cli.Core;

/// <summary>The tokens of <see cref="DaemonConnect.Capabilities"/>, as the server spells them.</summary>
public static class DaemonCapabilities {
    /// <summary>The daemon registers attached agents: terminal sessions it steers through a Claude Code mod, with the
    /// <c>mod</c> runtime transport and no launch.</summary>
    public const string Steering = "steering/1";

    /// <summary>The daemon takes <c>CompactAgent</c>: it compacts a hosted agent's context itself, refusing while a
    /// prompt is open and dropping a request while one is already queued or running.</summary>
    public const string Compaction = "compact/1";
}
