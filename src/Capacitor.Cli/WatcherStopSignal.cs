namespace Capacitor.Cli;

/// <summary>
/// Windows' stand-in for the SIGTERM a watcher gets on Unix: a named event per watcher pid that asks it
/// to run its shutdown path (final drain, tail spool) before the stopper's hard kill.
/// The name is session-local, so a stopper in another logon session finds nothing and kills hard.
/// </summary>
static class WatcherStopSignal {
    internal static string Name(int pid) => $@"Local\kcap-watcher-stop-{pid}";

    /// <summary>Calls <paramref name="onStop"/> once a stopper signals this pid; null off Windows or when
    /// the event cannot be created.</summary>
    internal static IDisposable? Listen(int pid, Action onStop) {
        if (!OperatingSystem.IsWindows()) return null;

        try {
            var signal       = new EventWaitHandle(false, EventResetMode.ManualReset, Name(pid));
            // The callback can race the listener's disposal; an exception escaping a pool thread ends the process.
            var registration = ThreadPool.RegisterWaitForSingleObject(
                signal, (_, timedOut) => {
                    if (timedOut) return;
                    try { onStop(); } catch { /* the watcher is already shutting down */ }
                }, null, Timeout.Infinite, executeOnlyOnce: true);

            return new Listener(signal, registration);
        } catch {
            return null;
        }
    }

    /// <summary>True when a watcher is listening on <paramref name="pid"/> and was asked to stop.</summary>
    internal static bool TryRequest(int pid) {
        if (!OperatingSystem.IsWindows()) return false;

        try {
            if (!EventWaitHandle.TryOpenExisting(Name(pid), out var signal)) return false;

            using (signal) return signal.Set();
        } catch {
            return false;
        }
    }

    sealed class Listener(EventWaitHandle signal, RegisteredWaitHandle registration) : IDisposable {
        // The pool's wait thread holds its own handle to the event until the unregistration completes, and can for a
        // moment after, so the name stays openable briefly past Dispose. Bounded: Dispose may be called from inside
        // the stop callback itself.
        static readonly TimeSpan UnregisterWait = TimeSpan.FromSeconds(2);

        public void Dispose() {
            var unregistered = new ManualResetEvent(false);
            // Left undisposed when the wait runs out: the pool still signals it once the unregistration completes.
            if (!registration.Unregister(unregistered) || unregistered.WaitOne(UnregisterWait)) unregistered.Dispose();
            signal.Dispose();
        }
    }
}
