namespace Capacitor.Cli.Daemon.Services;

/// <summary>How a hosted agent's launch prompt ended up.</summary>
internal enum LaunchPromptOutcome {
    /// <summary>Pasted once startup output had settled. Its arrival could not be checked: the agent's
    /// hooks do not report to this daemon.</summary>
    Delivered,

    /// <summary>Pasted blind after the startup wait expired, into a TUI that never went quiet. Its
    /// arrival could not be checked.</summary>
    DeliveredAfterMaxWait,

    /// <summary>Withdrawn: the agent exited before its prompt could be pasted or seen to arrive.</summary>
    AgentExited,

    /// <summary>Withdrawn: a graceful stop was requested first.</summary>
    StopRequested,

    /// <summary>Withdrawn: the runtime was disposed first.</summary>
    Cancelled,

    /// <summary>The paste or its submit could not be written: the PTY had closed.</summary>
    WriteFailed,

    /// <summary>The agent's hooks reported a turn after the paste.</summary>
    Confirmed,

    /// <summary>The agent's hooks reported a turn only after the submit was sent again.</summary>
    ConfirmedAfterResubmit,

    /// <summary>No turn was reported, even after the submit was sent again: the prompt may be sitting
    /// unsent in the composer.</summary>
    NotConfirmed,
}
