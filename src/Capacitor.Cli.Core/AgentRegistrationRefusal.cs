namespace Capacitor.Cli.Core;

/// <summary>The values of <see cref="AgentRegisteredResponse.RefusedReason"/> this daemon acts on, as the server
/// spells them. The server's set is open, and it has more.</summary>
public static class AgentRegistrationRefusal {
    /// <summary>The reporting connection was evicted while the registration ran.</summary>
    public const string ConnectionVanished = "connection_vanished";

    /// <summary>The caller is not a registered daemon connection.</summary>
    public const string NotADaemon = "not_a_daemon";

    /// <summary>An attached agent registered by a daemon that did not advertise <see cref="DaemonCapabilities.Steering"/>.</summary>
    public const string SteeringUnsupported = "steering_unsupported";

    /// <summary>An attached agent registered without a process key or a usable session id.</summary>
    public const string InvalidAttachment = "invalid_attachment";

    /// <summary>Another Claude process holds the session or the agent id, or this process is attached under another
    /// agent it does not name as replaced.</summary>
    public const string SessionAttached = "session_attached";

    /// <summary>The agent named as replaced is attached under another owner or another process.</summary>
    public const string ReplacementRefused = "replacement_refused";

    /// <summary>The session is not the daemon owner's, or who owns it could not be established.</summary>
    public const string SessionNotOwned = "session_not_owned";
}
