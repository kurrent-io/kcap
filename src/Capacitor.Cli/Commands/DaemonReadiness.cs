namespace Capacitor.Cli.Commands;

/// <summary>What a detached start learned about the daemon it spawned before it stopped waiting.</summary>
internal enum DaemonReadiness {
    /// <summary>The control socket answered with the spawned process's pid.</summary>
    Serving,

    /// <summary>The spawned process exited.</summary>
    Exited,

    /// <summary>The bound ran out with the process alive and its socket not answering: unknown, not failed.</summary>
    NotYetServing,
}
