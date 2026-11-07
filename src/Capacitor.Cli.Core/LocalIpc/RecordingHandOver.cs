namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>How a hook's hand-over to the daemon hosting its agent went. A stale hosted claim turns on this: only a daemon
/// that answers can say no catch-up is coming.</summary>
public enum RecordingHandOver {
    /// <summary>The daemon records the source.</summary>
    Taken,

    /// <summary>The daemon is up and won't record it, so nothing of it is coming back for a claim on that source.</summary>
    Declined,

    /// <summary>The daemon is up and says the source isn't the asking agent's, so a claim on its key may be another agent's.</summary>
    Disowned,

    /// <summary>No daemon answered in time. Down is how a daemon restarts, so its catch-up may still be coming.</summary>
    Unanswered,
}
