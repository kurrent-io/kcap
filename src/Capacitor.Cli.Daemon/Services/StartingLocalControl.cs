using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Answers the local control socket while the daemon is still probing its vendors. A hello that accepts a starting
/// daemon is answered at once, marked starting and advertising nothing; every other frame, a plain hello included,
/// waits on its connection and is handed to <see cref="LocalControlServer"/> once it is live. So each client gets the
/// answer a ready daemon gives, only later, and no capability is advertised before its handler can run. A launch is
/// the exception: it is refused, for the client to retry.
/// </summary>
internal sealed partial class StartingLocalControl(DaemonConfig config, ILogger<StartingLocalControl> logger) : IDisposable {
    public delegate Task Dispatch(LocalFrame first, Stream stream, CancellationToken ct);

    public const string StartingRefusal = "the daemon is starting; try again in a moment";

    readonly TaskCompletionSource<Dispatch> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly CancellationTokenSource        _accepting = new();
    Task                                    _loop = Task.CompletedTask;

    /// <summary>Starts accepting on <paramref name="listener"/>. <paramref name="ct"/> is the daemon's lifetime: a connection
    /// still waiting when it ends is closed.</summary>
    public void Start(Socket listener, CancellationToken ct) {
        var accepting = CancellationTokenSource.CreateLinkedTokenSource(ct, _accepting.Token);
        _loop = Task.Run(() => AcceptAsync(listener, accepting, ct), CancellationToken.None);
    }

    /// <summary>Stops accepting and hands every waiting connection, and any accepted from here on, to <paramref name="dispatch"/>.</summary>
    public async Task HandOverAsync(Dispatch dispatch) {
        _ready.TrySetResult(dispatch);
        await _accepting.CancelAsync();
        try { await _loop; } catch { /* the loop logs its own faults */ }
    }

    public void Dispose() => _accepting.Dispose();

    async Task AcceptAsync(Socket listener, CancellationTokenSource accepting, CancellationToken lifetime) {
        using var linked = accepting;
        try {
            while (!accepting.IsCancellationRequested) {
                var conn = await listener.AcceptAsync(accepting.Token);
                _ = HandleAsync(conn, lifetime);
            }
        } catch (OperationCanceledException) {
        } catch (ObjectDisposedException) {
        } catch (SocketException ex) {
            LogAcceptFailed(ex);
        }
    }

    async Task HandleAsync(Socket conn, CancellationToken ct) {
        using var _ = conn;
        await using var stream = new NetworkStream(conn, ownsSocket: false);
        try {
            var first = await FrameCodec.ReadAsync(stream, ct);
            if (first is null) return;

            if (!_ready.Task.IsCompleted && first.Type == FrameType.Hello && AcceptsStarting(first.Text)) {
                await FrameCodec.WriteAsync(stream, LocalFrame.HelloJson(FrameType.HelloReply, StartingReply()), ct);

                return;
            }

            // A launch is refused rather than held: one held through a long probe would still start after the client
            // had given up on it, and its retry would start a second.
            if (!_ready.Task.IsCompleted && first.Type is FrameType.Spawn or FrameType.AgentResume) {
                await FrameCodec.WriteAsync(stream, LocalFrame.Error(StartingRefusal), ct);

                return;
            }

            var dispatch = await _ready.Task.WaitAsync(ct);
            await dispatch(first, stream, ct);
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            LogConnectionError(ex);
        }
    }

    string StartingReply() => JsonSerializer.Serialize(
        new HelloReplyDto(HelloProtocol.CurrentVersion, DaemonRunner.ResolveDaemonVersion(), config.Name, [], Environment.ProcessId, config.InstanceId, Starting: true),
        HelloIpcJsonContext.Default.HelloReplyDto);

    internal static bool AcceptsStarting(string payload) {
        try {
            return payload.Length > 0 && JsonSerializer.Deserialize(payload, HelloIpcJsonContext.Default.ClientHelloDto)?.AcceptsStarting == true;
        } catch (JsonException) {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Local control socket stopped accepting while the daemon was starting")]
    partial void LogAcceptFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Local control connection faulted while the daemon was starting")]
    partial void LogConnectionError(Exception ex);
}
