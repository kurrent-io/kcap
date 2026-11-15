namespace Capacitor.App.Services;

/// <summary>
/// A background loop that runs at most once at a time, linked to the app's shutdown token. It shares
/// its owner's lock, so the owner can stop it and clear what the loop fed in one critical section, and
/// a loop it stopped can tell, under that lock, that it is no longer the current one.
/// </summary>
internal sealed class CancellableLoop(
        Lock gate, Func<bool> closed, Func<CancellationToken, Task> body, CancellationToken shutdownToken) {
    CancellationTokenSource? _cts;
    CancellationToken _token; // read without touching _cts, which the loop disposes once it unwinds

    /// <summary>Whether a loop is running. Caller holds the gate.</summary>
    public bool IsRunning => _cts is not null;

    /// <summary>Whether <paramref name="ct"/> belongs to the running loop. Caller holds the gate.</summary>
    public bool IsCurrent(CancellationToken ct) => _cts is not null && _token == ct;

    /// <summary>Starts the loop unless one is running or the owner is closed.</summary>
    public void Start() {
        CancellationTokenSource cts;
        lock (gate) {
            if (closed() || _cts is not null) return;
            cts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            _cts = cts;
            _token = cts.Token;
        }
        _ = Task.Run(() => RunAsync(cts));
    }

    /// <summary>Stops the running loop, running <paramref name="whileLocked"/> in the same critical
    /// section that detaches it.</summary>
    public void Stop(Action? whileLocked = null) {
        CancellationTokenSource? cts;
        lock (gate) {
            cts = _cts;
            _cts = null;
            whileLocked?.Invoke();
        }
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { } // already unwound and self-disposed
    }

    async Task RunAsync(CancellationTokenSource cts) {
        try {
            await body(cts.Token).ConfigureAwait(false);
        } finally {
            cts.Dispose();
        }
    }
}
