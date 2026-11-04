using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// What a queued daemon restart waits for, read from the daemon's restart-pending marker.
public enum DaemonRestartWait {
    /// No restart is queued.
    None,

    /// The interactive agents' turns, up to the daemon's turn wait.
    Turns,

    /// The daemon to have no agents at all. Idle agents hold it too, for as long as they stay.
    Agents,

    /// The marker does not say: a bare restart waiting to be retried, a foreground daemon's, or a when-idle request
    /// recorded by a daemon that predates the distinction.
    Unstated,
}

public static class DaemonRestartWaitExtensions {
    public static DaemonRestartWait ToRestartWait(this DaemonRestartMarker? marker) =>
        marker is null ? DaemonRestartWait.None
        : marker.WaitsForTurns ? DaemonRestartWait.Turns
        : marker.WaitsForNoAgents ? DaemonRestartWait.Agents
        : DaemonRestartWait.Unstated;
}
