namespace Capacitor.Cli;

/// <summary>What the daemons on this machine leave <c>kcap disable</c> able to say, worst last.</summary>
public enum RecordingStopOutcome {
    /// <summary>Every daemon that could be recording answered, and nothing they sent is still on its way.</summary>
    Stopped,

    /// <summary>A daemon sends nothing more, but something it sent has no answer yet.</summary>
    SendInFlight,

    /// <summary>A daemon was told to stop a source that hadn't ended when it stopped waiting.</summary>
    StillStopping,

    /// <summary>A daemon nothing ties to the session took the connection and gave no answer.</summary>
    DaemonSilent,

    /// <summary>A daemon tied to the session, by a claim or an earlier run, has not said it stopped.</summary>
    HolderUnconfirmed,
}
