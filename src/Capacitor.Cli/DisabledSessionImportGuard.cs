using Capacitor.Cli.Core;

namespace Capacitor.Cli;

/// <summary>
/// Ends a detached import of one session once the person disables that session. The import runs for minutes after the
/// session ended and checks no disable of its own, so without this a <c>kcap disable</c> would not stop the upload.
/// </summary>
public static class DisabledSessionImportGuard {
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <param name="stop">Called once, from a timer thread, when the disable is seen; it ends the process.</param>
    public static ITimer Start(string sessionId, ConfigRoot config, TimeProvider time, Action stop) {
        var fired = 0;
        ITimer? timer = null;

        timer = time.CreateTimer(_ => {
            if (!DisabledSessions.IsDisabledByPerson(sessionId, config) || Interlocked.Exchange(ref fired, 1) == 1) return;

            timer?.Dispose();
            stop();
        }, null, TimeSpan.Zero, PollInterval);

        return timer;
    }
}
