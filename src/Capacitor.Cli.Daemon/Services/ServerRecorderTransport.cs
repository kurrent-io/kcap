using Capacitor.Cli.Core;
using Capacitor.Cli.Recording;
using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Every source this daemon records, on one hub connection of their own: <c>WatcherConnect</c> takes a connection out of
/// the UI topics, and the daemon's main connection has to stay in its owner's. It lives from the first source to the
/// last, because with none the server keeps it in those topics for good, and while it lives it comes back by itself: a
/// failed transport through SignalR's own reconnect, and a clean close from the server, which SignalR takes as final,
/// through a start here.
/// </summary>
internal sealed class ServerRecorderTransport : IRecorderTransport, IAsyncDisposable {
    static readonly TimeSpan FirstRestartDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxRestartDelay = TimeSpan.FromSeconds(30);

    // SignalR's stop takes no working token and can hang on a wedged transport. Its teardown is under way by then, and its
    // close wakes the loop whenever it lands, so only the wait is given up: a source can't be left behind it.
    internal static readonly TimeSpan StopCap = TimeSpan.FromSeconds(5);

    // Short of a claim's grace: a host beats a source's claim between attempts, and not while one waits here.
    internal static readonly TimeSpan ConnectionWait = TimeSpan.FromSeconds(15);

    // A batch the server never answers would hold its source's loop for good. Going past this reads as a failed send, so
    // the recorder sends the lines again, and the server drops a late first copy: it takes nothing at or below its mark.
    // Short of a claim's grace: a source beats its claim between sends, and a send using all of this mustn't let it go stale.
    internal static readonly TimeSpan SendBudget = TimeSpan.FromSeconds(45);

    readonly HubConnection           _hub;
    readonly TimeProvider            _time;
    readonly Action<string>          _log;
    readonly CancellationTokenSource _closing = new();
    readonly Lock                    _gate    = new();

    // Each key this connection holds, as the server spells it, with the spelling its recorder knows it by.
    readonly Dictionary<string, string> _held = new(StringComparer.Ordinal);

    // Hub calls on one connection aren't ordered, so a key's detach has to answer before that key connects again, or
    // it can land behind the connect and take the new registration with it.
    readonly Dictionary<string, Task> _detaching = new(StringComparer.Ordinal);

    TaskCompletionSource _up      = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task?                _supervising;
    int                  _connecting;
    bool                 _restart;

    public ServerRecorderTransport(HubConnection hub, TimeProvider time, Action<string> log) {
        _hub  = hub;
        _time = time;
        _log  = log;

        DaemonHub.IgnoreUiBroadcasts(hub);
        hub.On<string, string>("StopSource", OnStopSource);
        // The server pushes this beside StopSource for an older watcher. It names no source, so it says nothing here.
        hub.On<string>("StopWatcher", static _ => Task.CompletedTask);

        hub.Reconnecting += OnReconnecting;
        hub.Reconnected  += OnReconnected;
        hub.Closed       += OnClosed;
    }

    /// <summary>Replaces the stop of the hub, for a test that hangs it.</summary>
    internal Func<Task>? StopHub { get; init; }

    public event Func<string, string, Task>? StopRequested;

    public event Func<Task>? Reconnected;

    public bool IsConnected => _hub.State == HubConnectionState.Connected;

    /// <summary>Replaces the connection, which keeps the identity it negotiated with for as long as it lives. A sign-out
    /// leaves nothing it can come back as, so nothing is sent until there is a sign-in again. Asks and returns: a stop can
    /// hang on a wedged transport.</summary>
    public void Restart() {
        lock (_gate) {
            _restart = true;
            _changed.TrySetResult();
        }
    }

    public async Task<int> ConnectAsync(RecordingSource source, CancellationToken ct) {
        var key = CanonicalKey(source);

        Task up;

        lock (_gate) {
            _connecting++;
            _supervising ??= Task.Run(SuperviseAsync, CancellationToken.None);
            _changed.TrySetResult();
            up = _up.Task;
        }

        try {
            await up.WaitAsync(ConnectionWait, _time, ct);

            Task? detaching;
            lock (_gate) _detaching.TryGetValue(key, out detaching);

            if (detaching is not null) await detaching.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            ct.ThrowIfCancellationRequested();

            lock (_gate) _held[key] = source.Key;

            // No token: given one, InvokeAsync completes the moment it fires, with the server's WatcherConnect still running.
            return await _hub.InvokeAsync<int>("WatcherConnect", source.SessionId, source.AgentId, CancellationToken.None);
        } finally {
            lock (_gate) {
                _connecting--;
                _changed.TrySetResult();
            }
        }
    }

    public async Task<int> SendBatchAsync(TranscriptBatch batch, CancellationToken ct) {
        using var budget = new CancellationTokenSource(SendBudget, _time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);

        try {
            if (batch.Vendor == "cursor") return (await _hub.InvokeAsync<TranscriptBatchAck>("SendTranscriptBatchAcked", batch, either.Token)).NextLineNumber;

            await _hub.InvokeAsync("SendTranscriptBatch2", batch, either.Token);
        } catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) {
            // Neither is this source's own stop, which is what a recorder takes a cancellation for.
            if (budget.IsCancellationRequested) throw new TimeoutException($"The server did not answer a transcript batch within {SendBudget.TotalSeconds}s.", ex);

            throw new IOException($"The connection closed under a transcript batch: {ex.Message}", ex);
        }

        return batch.LineNumbers is { Length: > 0 } sent ? sent[^1] + 1 : 0;
    }

    public Task SendTitleAsync(string sessionId, string title, CancellationToken ct) =>
        _hub.InvokeAsync("SendTitle", sessionId, title, null, 0L, 0L, 0L, 0L, ct);

    public Task UpdateTitleAsync(
            string sessionId, string title, string? model, long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens, CancellationToken ct) =>
        _hub.InvokeAsync("UpdateTitle", sessionId, title, model, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, ct);

    public Task DrainCompleteAsync(RecordingSource source, CancellationToken ct) =>
        _hub.InvokeAsync("WatcherDrainComplete", source.SessionId, source.AgentId, cancellationToken: ct);

    /// <summary>A stop still on its way for this key finds nothing held and is dropped. <paramref name="ct"/> ends the
    /// caller's wait; the key's next connect still waits for the server's answer.</summary>
    public Task DetachAsync(RecordingSource source, CancellationToken ct) {
        var key = CanonicalKey(source);

        lock (_gate) {
            _held.Remove(key);

            var detach = SendDetachAsync(source, _detaching.GetValueOrDefault(key));
            _detaching[key] = detach;
            _ = detach.ContinueWith(done => Forget(key, done), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            return detach.WaitAsync(ct);
        }
    }

    async Task SendDetachAsync(RecordingSource source, Task? previous) {
        await Task.Yield();

        if (previous is not null) await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // A connection that is down has lost its registrations with it. No token, as for the connect.
        if (IsConnected) await _hub.InvokeAsync("WatcherDetach", source.SessionId, source.AgentId, CancellationToken.None);
    }

    void Forget(string key, Task detach) {
        lock (_gate) {
            if (_detaching.TryGetValue(key, out var latest) && latest == detach) _detaching.Remove(key);

            _changed.TrySetResult();
        }
    }

    // The one place the connection starts and stops, so a stop and the start behind it can't overlap. SignalR raises a
    // close in its own time, which can be behind that start, so what happens next is read from the connection's state.
    async Task SuperviseAsync() {
        var   delay      = FirstRestartDelay;
        var   registered = false;
        long? upSince    = null;
        Task? stopping   = null;

        while (!_closing.IsCancellationRequested) {
            Task               changed;
            bool               wanted, stop;
            HubConnectionState state;

            lock (_gate) {
                if (_changed.Task.IsCompleted) _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

                changed = _changed.Task;
                wanted  = _connecting > 0 || _held.Count > 0 || _detaching.Count > 0;
                state   = _hub.State;
                stop    = stopping is not { IsCompleted: false } && (state != HubConnectionState.Disconnected && !wanted || state == HubConnectionState.Connected && _restart);

                _restart = false;

                if (stop) MarkDown();
                else if (state == HubConnectionState.Connected) _up.TrySetResult();
            }

            if (stop) {
                registered &= wanted;
                upSince    =  null;
                delay      =  FirstRestartDelay;
                stopping   =  StopHub?.Invoke() ?? _hub.StopAsync(CancellationToken.None);

                await WaitOutStopAsync(stopping);
            } else if (wanted && state == HubConnectionState.Disconnected) {
                // Only staying up earns a quick return. A start doesn't: a server closing every new connection would get a
                // tight loop, and every recorder re-registering each time round it.
                var wasUp    = upSince is not null;
                var stayedUp = upSince is { } since && _time.GetElapsedTime(since) >= MaxRestartDelay;

                upSince = null;

                if (stayedUp) delay = FirstRestartDelay;

                if ((!wasUp || stayedUp) && await TryStartAsync(registered)) {
                    upSince    = _time.GetTimestamp();
                    registered = true;

                    continue;
                }

                if (!await WaitAsync(Task.Delay(delay, _time, _closing.Token))) return;

                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxRestartDelay.TotalSeconds));
            } else if (!await WaitAsync(changed.WaitAsync(_closing.Token))) {
                return;
            }
        }
    }

    async Task<bool> TryStartAsync(bool raiseReconnected) {
        try {
            await _hub.StartAsync(_closing.Token);
        } catch (Exception ex) {
            if (!_closing.IsCancellationRequested) Log($"Recording connection failed: {ex.Message}");

            return false;
        }

        MarkUp();
        if (raiseReconnected) RaiseReconnected();

        return true;
    }

    async Task WaitOutStopAsync(Task stopping) {
        _ = stopping.ContinueWith(_ => Wake(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try {
            await stopping.WaitAsync(StopCap, _time, _closing.Token);
        } catch (TimeoutException) {
            Log($"Stopping the recording connection took over {StopCap.TotalSeconds}s; leaving it to finish");
        } catch (OperationCanceledException) {
            // Being disposed, which ends the connection anyway.
        } catch (Exception ex) {
            Log($"Stopping the recording connection failed: {ex.Message}");
        }
    }

    static async Task<bool> WaitAsync(Task wait) {
        try {
            await wait;

            return true;
        } catch (OperationCanceledException) {
            return false;
        }
    }

    void Wake() {
        lock (_gate) _changed.TrySetResult();
    }

    Task OnReconnecting(Exception? ex) {
        lock (_gate) MarkDown();
        Log($"Recording connection lost, reconnecting: {ex?.Message}");

        return Task.CompletedTask;
    }

    Task OnReconnected(string? connectionId) {
        Log($"Recording connection back: {connectionId}");
        MarkUp();
        RaiseReconnected();

        return Task.CompletedTask;
    }

    Task OnClosed(Exception? ex) {
        lock (_gate) {
            // Connected by now means this close was an earlier connection's.
            if (_hub.State != HubConnectionState.Connected) MarkDown();

            _changed.TrySetResult();
        }

        if (ex is not null) Log($"Recording connection closed: {ex.Message}");

        return Task.CompletedTask;
    }

    // Every source's stop goes through the connection's one sequential dispatch, so nothing of a handler's runs on it.
    Task OnStopSource(string key, string reason) {
        string? held;

        lock (_gate) {
            if (!_held.TryGetValue(key, out held)) return Task.CompletedTask;
        }

        foreach (var handler in StopRequested?.GetInvocationList() ?? []) Isolate(() => ((Func<string, string, Task>)handler)(held, reason));

        return Task.CompletedTask;
    }

    // Each on its own, because a recorder re-registering waits on its WatcherConnect, and one that hangs must not hold
    // up the rest.
    void RaiseReconnected() {
        foreach (var handler in Reconnected?.GetInvocationList() ?? []) Isolate((Func<Task>)handler);
    }

    void Isolate(Func<Task> handler) =>
        _ = Task.Run(async () => {
            try {
                await handler();
            } catch (Exception ex) {
                Log($"A recording handler failed: {ex.Message}");
            }
        });

    void MarkUp() {
        lock (_gate) _up.TrySetResult();
    }

    // Under the gate.
    void MarkDown() {
        if (_up.Task.IsCompleted) _up = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    void Log(string message) {
        try {
            _log(message);
        } catch {
            // Nowhere left to report it.
        }
    }

    static string CanonicalKey(RecordingSource source) {
        var session = SessionIds.Canonical(source.SessionId);

        return source.AgentId is null ? session! : $"{session}-{SessionIds.Canonical(source.AgentId)}";
    }

    public async ValueTask DisposeAsync() {
        await _closing.CancelAsync();
        await _hub.DisposeAsync();
    }
}
