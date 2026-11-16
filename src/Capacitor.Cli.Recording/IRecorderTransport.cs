using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Transcripts;

namespace Capacitor.Cli.Recording;

/// <summary>How a recorder reaches the server. Several recorders can share one transport, so each filters
/// <see cref="StopRequested"/> by key and each handles <see cref="Reconnected"/> itself.</summary>
public interface IRecorderTransport {
    bool IsConnected { get; }

    /// <summary>Registers the source and returns the line the server wants next. The task completes once the server
    /// has answered or the connection has gone, whatever <paramref name="ct"/> does: a call that has left this client
    /// can still register, and a caller that gave up detaches only when this task says nothing is left to land.
    /// <paramref name="ct"/> stops only what comes before the call leaves the client.</summary>
    Task<int> ConnectAsync(RecordingSource source, CancellationToken ct);

    /// <summary>Sends one batch. A Cursor batch returns the server's acknowledged next line, which can be short of
    /// what was sent. No other vendor's batch is acknowledged by line, so those callers ignore the result.</summary>
    Task<int> SendBatchAsync(TranscriptBatch batch, CancellationToken ct);

    /// <summary>Whether the server on this connection takes canonical batches. A server without the call, or one whose
    /// answer fails, takes none: the result is false, never an exception, unless <paramref name="ct"/> stops it.</summary>
    Task<bool> SupportsCanonicalAsync(CancellationToken ct);

    /// <summary>Sends one canonical batch. A batch the server cannot take comes back unsupported with nothing stored, and
    /// a send that fails says nothing of what the server stored.</summary>
    Task<CanonicalSendResult> SendCanonicalBatchAsync(CanonicalTranscriptBatch batch, CancellationToken ct);

    Task SendTitleAsync(string sessionId, string title, CancellationToken ct);

    Task UpdateTitleAsync(
        string sessionId, string title, string? model, long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens, CancellationToken ct);

    Task DrainCompleteAsync(RecordingSource source, CancellationToken ct);

    /// <summary>Tells the server this connection no longer records the source, for a connection that outlives its
    /// sources. A host calls this once a source has ended, on any path, before its key can be added again, so a
    /// transport must not let a later <see cref="ConnectAsync"/> overtake it: on a shared connection a detach landing
    /// behind that connect would remove the new recorder's registration.</summary>
    Task DetachAsync(RecordingSource source, CancellationToken ct);

    /// <summary>The server asked the source with this key to stop, for this reason.</summary>
    event Func<string, string, Task> StopRequested;

    event Func<Task> Reconnected;
}
