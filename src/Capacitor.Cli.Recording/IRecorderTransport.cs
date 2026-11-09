using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording;

/// <summary>How a recorder reaches the server. Several recorders can share one transport, so each filters
/// <see cref="StopRequested"/> by key and each handles <see cref="Reconnected"/> itself.</summary>
public interface IRecorderTransport {
    bool IsConnected { get; }

    /// <summary>Registers the source and returns the line the server wants next.</summary>
    Task<int> ConnectAsync(RecordingSource source, CancellationToken ct);

    /// <summary>Sends one batch. A Cursor batch returns the server's acknowledged next line, which can be short of
    /// what was sent. No other vendor's batch is acknowledged by line, so those callers ignore the result.</summary>
    Task<int> SendBatchAsync(TranscriptBatch batch, CancellationToken ct);

    Task SendTitleAsync(string sessionId, string title, CancellationToken ct);

    Task UpdateTitleAsync(
        string sessionId, string title, string? model, long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens, CancellationToken ct);

    Task DrainCompleteAsync(RecordingSource source, CancellationToken ct);

    /// <summary>The server asked the source with this key to stop, for this reason.</summary>
    event Func<string, string, Task> StopRequested;

    event Func<Task> Reconnected;
}
