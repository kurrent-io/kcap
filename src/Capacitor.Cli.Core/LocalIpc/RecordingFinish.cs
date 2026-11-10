namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>How a daemon answered a hook's ask to finish a source it claims.</summary>
public enum RecordingFinish {
    /// <summary>It made the source's final drain.</summary>
    Finished,

    /// <summary>It is up and holds no recorder for the source now: one it has yet to pick up after a restart, or is
    /// letting go of.</summary>
    NotHeld,

    /// <summary>It took the connection and gave no answer in time, so it may still be finishing.</summary>
    Unanswered,

    /// <summary>Nothing listens on its socket, so nothing of it is running to finish the source.</summary>
    Unreachable,
}
