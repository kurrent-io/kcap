using System.Diagnostics;
using System.Runtime.CompilerServices;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The lifecycle of a daemon-hosted child started with redirected stdio: a stderr drain, bounded
/// waits that return silently on timeout, a tree kill through <see cref="ProcessTree.Kill(Process)"/>,
/// and an idempotent dispose that kills. Terminate is safe after dispose.
/// </summary>
internal sealed class ManagedChildProcess : IAsyncDisposable {
    static readonly TimeSpan DefaultTerminateWait = TimeSpan.FromSeconds(5);
    static readonly TimeSpan StderrDrainStopWait  = TimeSpan.FromSeconds(2);

    readonly Process                 _process;
    readonly TimeProvider            _time;
    readonly Action<string>          _onStderrLine;
    readonly CancellationTokenSource _stderrDrainCts = new();
    readonly Task                    _stderrDrainTask;

    /// <summary>Never disposed: disposing a <see cref="SemaphoreSlim"/> does not release queued async
    /// waiters, so a writer queued at dispose would hang. The disposal checks in
    /// <see cref="WriteLineAsync"/> turn those writers away instead.</summary>
    readonly SemaphoreSlim _stdinGate = new(1, 1);

    int _disposed;

    /// <summary>Starts draining stderr at once. An undrained stderr pipe fills at ~64KB and blocks the
    /// child's next write. <paramref name="onStderrLine"/> gets each non-empty line and must not throw.</summary>
    public ManagedChildProcess(Process process, TimeProvider time, Action<string> onStderrLine) {
        _process      = process;
        _time         = time;
        _onStderrLine = onStderrLine;
        Pid           = SafePid(process);

        _stderrDrainTask = DrainStderrAsync(_stderrDrainCts.Token);
    }

    /// <summary>Captured at construction: <see cref="Process.Id"/> throws once the process is disposed.</summary>
    public int Pid { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public bool HasExited {
        get {
            try {
                return _process.HasExited;
            } catch {
                return true;
            }
        }
    }

    public int? ExitCode {
        get {
            try {
                return _process.HasExited ? _process.ExitCode : null;
            } catch {
                return null;
            }
        }
    }

    async Task DrainStderrAsync(CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested) {
                var line = await _process.StandardError.ReadLineAsync(ct).ConfigureAwait(false);

                if (line is null) break;
                if (line.Length == 0) continue;

                _onStderrLine(line);
            }
        } catch (OperationCanceledException) {
        } catch (IOException) {
        } catch (ObjectDisposedException) {
        }
    }

    /// <summary>Stdout, line by line, ending at EOF; a pipe torn down by a kill or a racing dispose is EOF too.</summary>
    public async IAsyncEnumerable<string> ReadStdoutLinesAsync([EnumeratorCancellation] CancellationToken ct) {
        while (true) {
            string? line;

            try {
                line = await _process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            } catch (IOException) {
                break;
            } catch (ObjectDisposedException) {
                break;
            }

            if (line is null) break;

            yield return line;
        }
    }

    /// <summary>Writes <paramref name="line"/> and a <c>\n</c> to stdin and flushes, serialized against
    /// other writers so lines never interleave. Disposal is checked before and after taking the gate: a
    /// writer queued when <see cref="DisposeAsync"/> ran must not write into the process it just killed.</summary>
    public async Task WriteLineAsync(string line, CancellationToken ct) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        await _stdinGate.WaitAsync(ct).ConfigureAwait(false);

        try {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            await _process.StandardInput.WriteAsync((line + "\n").AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        } finally {
            _stdinGate.Release();
        }
    }

    /// <summary>Closes stdin, waiting at most <paramref name="timeout"/> for a write in progress. A
    /// no-op once disposed or closed.</summary>
    public async Task CloseInputAsync(TimeSpan timeout) {
        if (IsDisposed) return;

        bool acquired;

        try {
            acquired = await _stdinGate.WaitAsync(timeout).ConfigureAwait(false);
        } catch {
            return;
        }

        if (!acquired) return;

        try {
            if (!IsDisposed) _process.StandardInput.Close();
        } catch {
            // The child already exited, or its stdin is already gone.
        } finally {
            _stdinGate.Release();
        }
    }

    public async Task WaitForExitAsync(TimeSpan? timeout = null) {
        try {
            if (timeout is { } t) {
                using var cts = new CancellationTokenSource(t, _time);

                try {
                    await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                }
            } else {
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        } catch {
            // Already exited or disposed.
        }
    }

    public async Task TerminateAsync(TimeSpan? timeout = null) {
        try {
            if (_process.HasExited) return;

            ProcessTree.Kill(_process);
        } catch {
            return;   // already exited or disposed, or the kill raced the exit
        }

        await WaitForExitAsync(timeout ?? DefaultTerminateWait).ConfigureAwait(false);
    }

    /// <summary>Kills a running child without waiting for it to exit, so disposal is not exit
    /// evidence: a caller that needs a confirmed exit terminates and reads <see cref="HasExited"/>
    /// first. The kill comes first so a writer inside the stdin gate fails on the broken pipe rather
    /// than hanging.</summary>
    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try {
            if (!_process.HasExited) ProcessTree.Kill(_process);
        } catch {
            // Already exited or inaccessible.
        }

        try {
            await _stderrDrainCts.CancelAsync().ConfigureAwait(false);
        } catch {
            // A registration callback threw; dispose must not.
        }

        try {
            await _stderrDrainTask.WaitAsync(StderrDrainStopWait, _time).ConfigureAwait(false);
        } catch {
            // A drain still blocked after the bound is abandoned rather than hanging dispose.
        }

        _stderrDrainCts.Dispose();

        try {
            _process.Dispose();
        } catch {
            // Handle release only; the child is already killed.
        }
    }

    static int SafePid(Process process) {
        try {
            return process.Id;
        } catch {
            return 0;
        }
    }
}
