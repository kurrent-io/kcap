namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>What one daemon says of a session a person has disabled.</summary>
public enum RecordingDisable {
    /// <summary>It has stopped, and the server has answered everything sent before that.</summary>
    Stopped,

    /// <summary>This daemon holds none of it, and nothing it sent is still on its way.</summary>
    NotHeld,

    /// <summary>It sends nothing more, but something it sent has no answer yet and may still reach the server.</summary>
    SendInFlight,

    /// <summary>A source was told to stop and hadn't ended when the daemon stopped waiting.</summary>
    StillStopping,

    /// <summary>Nothing answers on its socket and it is down, or it is too old to know the frame. Neither can be recording
    /// for this command to stop, so this alone says nothing is wrong. One up with nothing on its socket is
    /// <see cref="Silent"/>.</summary>
    NoSocket,

    /// <summary>It took the connection and gave no answer, so it may still be running what it was asked about.</summary>
    Silent,
}
