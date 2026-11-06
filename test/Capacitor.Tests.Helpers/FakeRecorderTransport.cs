using Capacitor.Cli.Core;
using Capacitor.Cli.Recording;

namespace Capacitor.Tests.Helpers;

/// <summary>A transport a test drives by hand. Until <see cref="IsConnected"/> is set, every call fails the way a
/// hub connection that never started does.</summary>
public sealed class FakeRecorderTransport : IRecorderTransport {
    readonly Lock _gate = new();
    readonly List<TranscriptBatch> _batches = [];

    public bool IsConnected { get; set; }

    /// <summary>The line <see cref="ConnectAsync"/> answers with.</summary>
    public int ResumeFrom { get; set; }

    /// <summary>What <see cref="SendBatchAsync"/> answers for a batch; by default the line after its last.</summary>
    public Func<TranscriptBatch, int> Ack { get; set; } = batch => batch.LineNumbers is { Length: > 0 } sent ? sent[^1] + 1 : 0;

    public int DrainCompletes { get; private set; }

    public int Connects { get; private set; }

    public IReadOnlyList<(string SessionId, string Title)> SentTitles { get; private set; } = [];

    public IReadOnlyList<TranscriptBatch> Batches {
        get {
            lock (_gate) return [.._batches];
        }
    }

    public event Func<string, string, Task>? StopRequested;

    public event Func<Task>? Reconnected;

    public Task<int> ConnectAsync(RecordingSource source, CancellationToken ct) {
        Require();
        Connects++;

        return Task.FromResult(ResumeFrom);
    }

    public Task<int> SendBatchAsync(TranscriptBatch batch, CancellationToken ct) {
        Require();
        lock (_gate) _batches.Add(batch);

        return Task.FromResult(Ack(batch));
    }

    public Task SendTitleAsync(string sessionId, string title, CancellationToken ct) {
        Require();
        SentTitles = [..SentTitles, (sessionId, title)];

        return Task.CompletedTask;
    }

    public Task UpdateTitleAsync(
            string sessionId, string title, string? model, long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens, CancellationToken ct) {
        Require();
        SentTitles = [..SentTitles, (sessionId, title)];

        return Task.CompletedTask;
    }

    public Task DrainCompleteAsync(RecordingSource source, CancellationToken ct) {
        Require();
        DrainCompletes++;

        return Task.CompletedTask;
    }

    public Task RaiseStopRequestedAsync(string key, string reason) => StopRequested?.Invoke(key, reason) ?? Task.CompletedTask;

    public Task RaiseReconnectedAsync() => Reconnected?.Invoke() ?? Task.CompletedTask;

    void Require() {
        if (!IsConnected) throw new InvalidOperationException("The transport is not connected.");
    }
}
