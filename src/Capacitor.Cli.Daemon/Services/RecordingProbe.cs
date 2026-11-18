namespace Capacitor.Cli.Daemon.Services;

/// <summary>Whether the server on the daemon's main connection can stop a source. A version can't say: beta, dev and
/// self-hosted servers send none. Calling <c>WatcherDetach</c> for a key nobody holds does nothing on a server that has
/// it and fails as an unknown method on one that doesn't, and only that failure is a no. Anything else says nothing, so
/// the asking goes on until the server answers: with the connection up, no reconnect would ask again. The answer goes
/// with the connection, and no answer reads as no, so nothing has to wait for one.</summary>
internal sealed class RecordingProbe {
    internal const string Method = "WatcherDetach";

    static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5);
    static readonly TimeSpan AskWait    = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(3);

    readonly Lock _gate = new();

    TaskCompletionSource<bool> _answer = NewAnswer();
    CancellationTokenSource?   _asking;

    /// <summary>A server that had said yes, or hadn't said, has said no. What is already recording has no stop path.</summary>
    public event Action? SaidNo;

    /// <summary>What the server answered on this connection, or null until it has.</summary>
    public bool? Answer {
        get {
            lock (_gate) return _answer.Task.IsCompleted ? _answer.Task.Result : null;
        }
    }

    /// <summary>Completes when the server has answered.</summary>
    public Task<bool> Answered {
        get {
            lock (_gate) return _answer.Task;
        }
    }

    /// <summary>Drops the answer of a connection that went away, and stops asking it. The next connection asks for itself,
    /// and its answer settles whoever waits.</summary>
    public void Forget() {
        CancellationTokenSource? asking;

        lock (_gate) {
            if (_answer.Task.IsCompleted) _answer = NewAnswer();

            asking  = _asking;
            _asking = null;
        }

        Cancel(asking);
    }

    public void Report(bool records) => Settle(records, from: null);

    // With from, only while that asking is still the current one: a call already on the wire can answer after its
    // connection went away, for a server the next connection may not reach.
    bool Settle(bool records, CancellationTokenSource? from) {
        bool settled;

        lock (_gate) {
            if (from is not null && _asking != from) return false;

            settled = _answer.TrySetResult(records);
        }

        if (settled && !records) SaidNo?.Invoke();

        return true;
    }

    /// <summary>Asks until the server answers, this asking gives way to the next, or <paramref name="ct"/> ends it.</summary>
    /// <param name="detach">Calls <c>WatcherDetach</c> for a key nobody holds.</param>
    /// <returns>The server's answer, or null if it gave none while this asking was the current one.</returns>
    public async Task<bool?> AskUntilAnsweredAsync(Func<CancellationToken, Task> detach, TimeProvider time, CancellationToken ct) {
        using var asking = CancellationTokenSource.CreateLinkedTokenSource(ct);

        CancellationTokenSource? before;

        lock (_gate) {
            before  = _asking;
            _asking = asking;
        }

        Cancel(before);

        try {
            for (var delay = FirstRetry;; delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetry.Ticks))) {
                if (await AskOnceAsync(detach, time, asking.Token) is { } answer) return Settle(answer, from: asking) ? answer : null;

                await Task.Delay(delay, time, asking.Token);
            }
        } catch (OperationCanceledException) {
            return null;
        } finally {
            lock (_gate) {
                if (_asking == asking) _asking = null;
            }
        }
    }

    static async Task<bool?> AskOnceAsync(Func<CancellationToken, Task> detach, TimeProvider time, CancellationToken ct) {
        using var bound  = new CancellationTokenSource(AskWait, time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(ct, bound.Token);

        try {
            await detach(either.Token);

            return true;
        } catch (Exception ex) when (ServerConnection.IsUnknownHubMethod(ex, Method)) {
            return false;
        } catch (Exception) {
            return null;
        }
    }

    static void Cancel(CancellationTokenSource? asking) {
        try {
            asking?.Cancel();
        } catch (ObjectDisposedException) {
            // It finished by itself.
        }
    }

    static TaskCompletionSource<bool> NewAnswer() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
