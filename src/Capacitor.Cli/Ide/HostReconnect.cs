using System.Reactive.Linq;
using Capacitor.Client;

namespace Capacitor.Cli.Ide;

/// <summary>
/// A client's retry restarts an unreachable daemon attach, and a server connection that is dormant, signed out, waiting out
/// its backoff or inside SignalR's own reconnect. A connected or connecting one is left alone: either restart drops it.
/// </summary>
internal sealed class HostReconnect(
        IObservable<AttachStatus> daemonStatus, Func<Task> restartDaemon,
        IObservable<ServerLaneStatus> laneStatus, Func<Task> restartLane, TextWriter errors) {
    /// <summary>Starts the restarts without waiting for them: a server restart waits for the old connection to close, which
    /// can take as long as a credential refresh nothing can cancel.</summary>
    public async Task ReconnectAsync() {
        if ((await daemonStatus.FirstAsync()).State == AttachState.Unreachable) _ = RunAsync(restartDaemon, "the daemon attach");
        if ((await laneStatus.FirstAsync()).State is not (ServerLaneState.Connected or ServerLaneState.Connecting))
            _ = RunAsync(restartLane, "the server connection");
    }

    /// <summary>Never faults, so nothing is left unobserved.</summary>
    async Task RunAsync(Func<Task> restart, string what) {
        try {
            await restart();
        } catch (Exception ex) {
            try {
                await errors.WriteLineAsync($"kcap ide: restarting {what} failed: {ex}");
            } catch {
                // A broken stderr is not a protocol fault.
            }
        }
    }
}
