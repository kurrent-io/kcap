using Capacitor.Cli.Core;
using Capacitor.Cli.Recording;
using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Every source this daemon records, on one hub connection of their own: <c>WatcherConnect</c> takes a connection out of
/// the UI topics, and the daemon's main connection has to stay in its owner's. It starts with the first source and
/// outlives each of them, so it comes back by itself: a failed transport through SignalR's own reconnect, and a clean
/// close from the server, which SignalR takes as final, through a restart here.
/// </summary>
internal sealed class ServerRecorderTransport : IRecorderTransport, IAsyncDisposable {
    static readonly TimeSpan FirstRestartDelay = TimeSpan.FromSeconds(1);
    static readonly TimeSpan MaxRestartDelay   = TimeSpan.FromSeconds(30);

    // Short of a claim's grace: a host beats a source's claim between attempts, and not while one waits here.
    internal static readonly TimeSpan ConnectionWait = TimeSpan.FromSeconds(15);

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

    TaskCompletionSource _up = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool                 _started;

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

    public event Func<string, string, Task>? StopRequested;

    public event Func<Task>? Reconnected;

    public bool IsConnected => _hub.State == HubConnectionState.Connected;

    public async Task<int> ConnectAsync(RecordingSource source, CancellationToken ct) {
        var key = CanonicalKey(source);

        await UntilUpAsync(ct);

        Task? detaching;
        lock (_gate) _detaching.TryGetValue(key, out detaching);

        if (detaching is not null) await detaching.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        ct.ThrowIfCancellationRequested();

        lock (_gate) _held[key] = source.Key;

        // No token: given one, InvokeAsync completes the moment it fires, with the server's WatcherConnect still running.
        return await _hub.InvokeAsync<int>("WatcherConnect", source.SessionId, source.AgentId, CancellationToken.None);
    }

    public async Task<int> SendBatchAsync(TranscriptBatch batch, CancellationToken ct) {
        if (batch.Vendor == "cursor") return (await _hub.InvokeAsync<TranscriptBatchAck>("SendTranscriptBatchAcked", batch, ct)).NextLineNumber;

        await _hub.InvokeAsync("SendTranscriptBatch2", batch, ct);

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
        }
    }

    async Task UntilUpAsync(CancellationToken ct) {
        Task up;

        lock (_gate) {
            if (!_started) {
                _started = true;
                _        = StartAsync(raiseReconnected: false);
            }

            up = _up.Task;
        }

        await up.WaitAsync(ConnectionWait, _time, ct);
    }

    async Task StartAsync(bool raiseReconnected) {
        var delay = FirstRestartDelay;

        while (!_closing.IsCancellationRequested) {
            try {
                await _hub.StartAsync(_closing.Token);
                MarkUp();
                if (raiseReconnected) RaiseReconnected();

                return;
            } catch (OperationCanceledException) when (_closing.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                Log($"Recording connection failed, retrying in {delay.TotalSeconds}s: {ex.Message}");
            }

            try {
                await Task.Delay(delay, _time, _closing.Token);
            } catch (OperationCanceledException) {
                return;
            }

            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxRestartDelay.TotalSeconds));
        }
    }

    Task OnReconnecting(Exception? ex) {
        MarkDown();
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
        MarkDown();

        if (!_closing.IsCancellationRequested) {
            Log($"Recording connection closed, restarting it: {ex?.Message}");
            _ = StartAsync(raiseReconnected: true);
        }

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

    void MarkDown() {
        lock (_gate) {
            if (_up.Task.IsCompleted) _up = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
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
