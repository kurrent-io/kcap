using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Tests.Helpers;

/// <summary>A daemon control socket that answers hello with the capabilities it is given and each recording frame
/// with <see cref="Answer"/>, one connection at a time. With no answer it holds the frame unanswered until disposed.
/// Records every frame it reads.</summary>
public sealed class FakeRecordingDaemon : IAsyncDisposable {
    readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    readonly CancellationTokenSource _cts = new();
    readonly List<LocalFrame> _received = [];
    readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Task _accept;

    public FakeRecordingDaemon(string socketPath, params string[] capabilities) {
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        _listener.Listen(8);
        _accept = Task.Run(() => ServeAsync(capabilities));
    }

    public string? Answer { get; set; }

    /// <summary>Completes on the first frame past hello.</summary>
    public Task Asked => _asked.Task;

    public IReadOnlyList<LocalFrame> Received {
        get {
            lock (_received) return [.. _received];
        }
    }

    public IReadOnlyList<FrameType> ReceivedTypes => [.. Received.Select(frame => frame.Type)];

    async Task ServeAsync(IReadOnlyList<string> capabilities) {
        var hello = JsonSerializer.Serialize(
            new HelloReplyDto(HelloProtocol.CurrentVersion, "test", "fake", [.. capabilities]), HelloIpcJsonContext.Default.HelloReplyDto);

        try {
            while (true) {
                using var conn = await _listener.AcceptAsync(_cts.Token);
                await using var stream = new NetworkStream(conn, ownsSocket: false);
                if (await FrameCodec.ReadAsync(stream, _cts.Token) is not { } frame) continue;

                lock (_received) _received.Add(frame);
                if (frame.Type != FrameType.Hello) _asked.TrySetResult();
                if (frame.Type != FrameType.Hello && Answer is null) await Task.Delay(Timeout.Infinite, _cts.Token);

                await FrameCodec.WriteAsync(stream, frame.Type switch {
                    FrameType.Hello          => LocalFrame.HelloJson(FrameType.HelloReply, hello),
                    FrameType.RecordSource or FrameType.RecordTerminalSource => LocalFrame.RecordSourceAck(Answer!),
                    FrameType.DisableSession => LocalFrame.DisableSessionAck(Answer!),
                    _                        => LocalFrame.FinishSourceAck(Answer!),
                }, _cts.Token);
            }
        } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException) {
            // Disposed: the listener is gone.
        }
    }

    public async ValueTask DisposeAsync() {
        await _cts.CancelAsync();
        _listener.Dispose();
        await _accept;
        _cts.Dispose();
    }
}
