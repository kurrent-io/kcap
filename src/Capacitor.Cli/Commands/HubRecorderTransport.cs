using Capacitor.Cli.Core;
using Capacitor.Cli.Recording;
using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.Cli.Commands;

/// <summary>
/// One source on one hub connection, as <c>kcap watch</c> runs it. Connecting retries for as long as the
/// server is away, because the watcher is cheap while idle and the session ends through its own hook.
/// </summary>
sealed class HubRecorderTransport : IRecorderTransport {
    readonly HubConnection  _hub;
    readonly string         _key;
    readonly Action         _beat;
    readonly Action<string> _log;
    readonly TimeProvider   _time;
    bool _startAttempted;

    public HubRecorderTransport(HubConnection hub, string key, Action beat, Action<string> log, TimeProvider time) {
        _hub  = hub;
        _key  = key;
        _beat = beat;
        _log  = log;
        _time = time;

        hub.On<string>("StopWatcher", RaiseStopRequestedAsync);
        hub.Reconnected += RaiseReconnectedAsync;
    }

    public event Func<string, string, Task>? StopRequested;

    public event Func<Task>? Reconnected;

    public bool IsConnected => _hub.State == HubConnectionState.Connected;

    /// <summary>A stop arrived before the first connect finished, so nothing was registered with the server.</summary>
    public bool StoppedBeforeConnect { get; private set; }

    public async Task<int> ConnectAsync(RecordingSource source, CancellationToken ct) {
        if (!_startAttempted) {
            _startAttempted = true;
            await StartWithRetryAsync(ct);

            if (ct.IsCancellationRequested) {
                StoppedBeforeConnect = true;
                ct.ThrowIfCancellationRequested();
            }
        }

        return await _hub.InvokeAsync<int>("WatcherConnect", source.SessionId, source.AgentId, ct);
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

    // This connection ends with its one source.
    public Task DetachAsync(RecordingSource source, CancellationToken ct) => Task.CompletedTask;

    async Task StartWithRetryAsync(CancellationToken ct) {
        var retryDelay = TimeSpan.FromSeconds(1);

        while (!ct.IsCancellationRequested) {
            // Every attempt: a server outage at startup outlasts the staleness threshold, and that is a
            // healthy reconnecting watcher, not a wedged one to reap and respawn.
            _beat();

            try {
                await _hub.StartAsync(ct);

                return;
            } catch (OperationCanceledException) {
                return;
            } catch (Exception ex) {
                _log($"SignalR connect failed, retrying in {retryDelay.TotalSeconds}s: {ex.Message}");

                if (!await DelayWithBeatsAsync(retryDelay, ct)) return;

                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }
    }

    // The backoff grows past the staleness threshold, so one plain delay would let the heartbeat go stale.
    async Task<bool> DelayWithBeatsAsync(TimeSpan total, CancellationToken ct) {
        foreach (var slice in TranscriptRecorder.HeartbeatSlices(total, TranscriptRecorder.HeartbeatSlice)) {
            _beat();

            try {
                await Task.Delay(slice, _time, ct);
            } catch (OperationCanceledException) {
                return false;
            }
        }

        return true;
    }

    async Task RaiseStopRequestedAsync(string reason) {
        foreach (var handler in StopRequested?.GetInvocationList() ?? []) await ((Func<string, string, Task>)handler)(_key, reason);
    }

    async Task RaiseReconnectedAsync(string? connectionId) {
        _log($"SignalR reconnected: {connectionId}");

        foreach (var handler in Reconnected?.GetInvocationList() ?? []) await ((Func<Task>)handler)();
    }
}
