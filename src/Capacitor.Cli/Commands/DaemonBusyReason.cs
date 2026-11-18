namespace Capacitor.Cli.Commands;

/// <summary>Why a daemon holds up an uninstall.</summary>
enum DaemonBusyReason {
    /// <summary>It is running or starting agents.</summary>
    Running,

    /// <summary>It answered without saying whether agents run, as a daemon too old to report it does.</summary>
    Unknown,

    /// <summary>It did not answer within the wait; a daemon still starting answers only once it has started.</summary>
    NoAnswer,
}
