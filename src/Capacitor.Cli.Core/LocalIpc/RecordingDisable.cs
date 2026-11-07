namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>What is known of a session's recording once a person has disabled it.</summary>
public enum RecordingDisable {
    /// <summary>It has stopped, and the server has answered everything sent before that.</summary>
    Stopped,

    /// <summary>The daemon asked holds none of it.</summary>
    NotHeld,

    /// <summary>It sends nothing more, but something it sent may still reach the server.</summary>
    SendInFlight,

    /// <summary>No daemon answered, which says nothing about whether one is still recording.</summary>
    Unanswered,
}
