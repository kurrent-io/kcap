using System.Diagnostics;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Commands;

/// <summary>Waits for a detached daemon to serve its control socket or exit, whichever comes first.
/// The bound only ends the wait; reaching it proves nothing either way.</summary>
static class DetachedDaemonReadiness {
    /// <summary>Generous: the listener binds before the server connect, but the lock acquisition ahead
    /// of it can itself wait seconds for a predecessor.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <param name="probe">Dials the daemon's control socket, bounded by the timeout it is handed.</param>
    /// <remarks>Only a reply naming the spawned pid counts: a daemon already holding the name answers on
    /// the same socket while the one just spawned is about to exit on the lock.</remarks>
    public static async Task<DaemonReadiness> AwaitAsync(
            Process daemon, Func<TimeSpan, Task<HelloProbeResult>> probe, TimeProvider time, TimeSpan bound) {
        var deadline = time.GetUtcNow() + bound;
        var exited   = daemon.WaitForExitAsync();

        while (true) {
            if (exited.IsCompleted) return DaemonReadiness.Exited;

            var remaining = deadline - time.GetUtcNow();
            if (remaining <= TimeSpan.Zero) return DaemonReadiness.NotYetServing;

            var hello = await probe(remaining < ProbeTimeout ? remaining : ProbeTimeout);
            if (exited.IsCompleted) return DaemonReadiness.Exited;
            if (hello.Reachable && hello.Pid == daemon.Id) return DaemonReadiness.Serving;

            remaining = deadline - time.GetUtcNow();
            if (remaining <= TimeSpan.Zero) return DaemonReadiness.NotYetServing;

            await Task.WhenAny(exited, Task.Delay(remaining < PollInterval ? remaining : PollInterval, time));
        }
    }
}
