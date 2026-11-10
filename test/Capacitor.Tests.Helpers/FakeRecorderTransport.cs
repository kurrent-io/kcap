using Capacitor.Cli.Core;
using Capacitor.Cli.Recording;

namespace Capacitor.Tests.Helpers;

/// <summary>A transport a test drives by hand. Until <see cref="IsConnected"/> is set, every call fails the way a
/// hub connection that never started does.</summary>
public sealed class FakeRecorderTransport : ISilenceableRecorderTransport {
    readonly Lock _gate = new();
    readonly List<TranscriptBatch> _batches = [];
    readonly List<string> _detached = [];

    public bool IsConnected { get; set; }

    /// <summary>The line <see cref="ConnectAsync"/> answers with.</summary>
    public int ResumeFrom { get; set; }

    /// <summary>What <see cref="SendBatchAsync"/> answers for a batch; by default the line after its last.</summary>
    public Func<TranscriptBatch, int> Ack { get; set; } = batch => batch.LineNumbers is { Length: > 0 } sent ? sent[^1] + 1 : 0;

    readonly HashSet<string> _silenced = new(StringComparer.Ordinal);

    int _refused;
    int _drainCompletes;
    int _connects;
    int _connectAttempts;
    int _connectFailures;
    int _connectsHeld;

    public int DrainCompletes => Volatile.Read(ref _drainCompletes);

    /// <summary>What <see cref="SilenceAsync"/> answers with; by default the server has answered everything.</summary>
    public Task<bool> SilenceAnswer { get; set; } = Task.FromResult(true);

    /// <summary>The calls refused for a silenced session. Any of these is something that would have gone out.</summary>
    public int Refused => Volatile.Read(ref _refused);

    public int Connects => Volatile.Read(ref _connects);

    public IReadOnlyList<(string SessionId, string Title)> SentTitles { get; private set; } = [];

    public IReadOnlyList<TranscriptBatch> Batches {
        get {
            lock (_gate) return [.._batches];
        }
    }

    /// <summary>The key of each <see cref="DetachAsync"/> call, in order.</summary>
    public IReadOnlyList<string> Detached {
        get {
            lock (_gate) return [.._detached];
        }
    }

    /// <summary>Awaited inside <see cref="DetachAsync"/> with its token, after the call is recorded.</summary>
    public Func<CancellationToken, Task>? OnDetach { get; set; }

    public event Func<string, string, Task>? StopRequested;

    public event Func<Task>? Reconnected;

    /// <summary>How many of the next <see cref="ConnectAsync"/> calls fail, as a hub that is down does.</summary>
    public int ConnectFailures {
        get => Volatile.Read(ref _connectFailures);
        set => Volatile.Write(ref _connectFailures, value);
    }

    /// <summary>Every <see cref="ConnectAsync"/> call, the failed ones included.</summary>
    public int ConnectAttempts => Volatile.Read(ref _connectAttempts);

    /// <summary>Awaited inside <see cref="DrainCompleteAsync"/> with its token, to hold a drain open.</summary>
    public Func<CancellationToken, Task>? OnDrainComplete { get; set; }

    /// <summary>Whether <see cref="ConnectAsync"/> never answers and ignores its token, as a hub retrying on a token
    /// that is never cancelled does while its server is down.</summary>
    public bool ConnectHangs { get; set; }

    /// <summary>Awaited inside <see cref="ConnectAsync"/>, ignoring its token as the transport's contract says, so a
    /// connect can land after whoever made it gave up.</summary>
    public Task? ConnectHold { get; set; }

    /// <summary>How many connects have reached <see cref="ConnectHold"/>.</summary>
    public int ConnectsHeld => Volatile.Read(ref _connectsHeld);

    public async Task<int> ConnectAsync(RecordingSource source, CancellationToken ct) {
        Interlocked.Increment(ref _connectAttempts);
        ct.ThrowIfCancellationRequested();
        RefuseSilenced(source.SessionId);
        if (ConnectHangs) await new TaskCompletionSource().Task;

        if (ConnectHold is { } hold) {
            Interlocked.Increment(ref _connectsHeld);
            await hold;
        }

        if (Interlocked.Decrement(ref _connectFailures) >= 0) throw new InvalidOperationException("The hub is down.");
        Interlocked.Exchange(ref _connectFailures, 0);
        Require();
        Interlocked.Increment(ref _connects);

        return ResumeFrom;
    }

    /// <summary>Awaited inside <see cref="SendBatchAsync"/> with its token, before the batch counts as sent.</summary>
    public Func<CancellationToken, Task>? OnSend { get; set; }

    public async Task<int> SendBatchAsync(TranscriptBatch batch, CancellationToken ct) {
        Require();
        RefuseSilenced(batch.SessionId);
        if (OnSend is { } send) await send(ct);
        RefuseSilenced(batch.SessionId);
        lock (_gate) _batches.Add(batch);

        return Ack(batch);
    }

    public Task SendTitleAsync(string sessionId, string title, CancellationToken ct) {
        Require();
        RefuseSilenced(sessionId);
        SentTitles = [..SentTitles, (sessionId, title)];

        return Task.CompletedTask;
    }

    public Task UpdateTitleAsync(
            string sessionId, string title, string? model, long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens, CancellationToken ct) {
        Require();
        RefuseSilenced(sessionId);
        SentTitles = [..SentTitles, (sessionId, title)];

        return Task.CompletedTask;
    }

    public Task DrainCompleteAsync(RecordingSource source, CancellationToken ct) {
        Require();
        RefuseSilenced(source.SessionId);
        Interlocked.Increment(ref _drainCompletes);

        return OnDrainComplete?.Invoke(ct) ?? Task.CompletedTask;
    }

    public Task DetachAsync(RecordingSource source, CancellationToken ct) {
        lock (_gate) _detached.Add(source.Key);

        return OnDetach?.Invoke(ct) ?? Task.CompletedTask;
    }

    public Task<bool> SilenceAsync(string sessionId) {
        lock (_gate) _silenced.Add(sessionId);

        return SilenceAnswer;
    }

    /// <summary>Awaits each handler in turn, as the hub transport does, so one that throws starves the rest.</summary>
    public async Task RaiseStopRequestedAsync(string key, string reason) {
        foreach (var handler in StopRequested?.GetInvocationList() ?? []) await ((Func<string, string, Task>)handler)(key, reason);
    }

    public async Task RaiseReconnectedAsync() {
        foreach (var handler in Reconnected?.GetInvocationList() ?? []) await ((Func<Task>)handler)();
    }

    void RefuseSilenced(string sessionId) {
        lock (_gate) {
            if (!_silenced.Contains(sessionId)) return;
        }

        Interlocked.Increment(ref _refused);

        throw new InvalidOperationException("The session is silenced.");
    }

    void Require() {
        if (!IsConnected) throw new InvalidOperationException("The transport is not connected.");
    }
}
