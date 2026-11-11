using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Tests.Helpers;

/// <summary>A daemon control socket whose hello offers the steering switch when it has an <c>answer</c>, and which
/// answers each SteeringSet with that text, one connection at a time. Records each SteeringSet payload it reads.</summary>
public sealed class FakeSteeringDaemon : IAsyncDisposable {
    readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    readonly CancellationTokenSource _cts = new();
    readonly List<string> _asked = [];
    readonly Task _serving;

    /// <param name="answer">The SteeringSetAck's text, sent as given; null for a daemon too old to offer the switch.</param>
    public FakeSteeringDaemon(string socketPath, string? answer) {
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        _listener.Listen(4);
        _serving = Task.Run(() => ServeAsync(answer));
    }

    /// <summary>A daemon that answers with <paramref name="ack"/>.</summary>
    public static FakeSteeringDaemon Answering(string socketPath, SteeringSetAckDto ack) =>
        new(socketPath, JsonSerializer.Serialize(ack, SteeringIpcJsonContext.Default.SteeringSetAckDto));

    public IReadOnlyList<string> Asked { get { lock (_asked) return [.. _asked]; } }

    async Task ServeAsync(string? answer) {
        var hello = JsonSerializer.Serialize(
            new HelloReplyDto(HelloProtocol.CurrentVersion, "test", "fake", answer is null ? [] : [SteeringIpc.SwitchCapability]),
            HelloIpcJsonContext.Default.HelloReplyDto);

        try {
            while (true) {
                using var conn = await _listener.AcceptAsync(_cts.Token);
                await using var stream = new NetworkStream(conn, ownsSocket: false);
                if (await FrameCodec.ReadAsync(stream, _cts.Token) is not { } frame) continue;

                if (frame.Type == FrameType.Hello) {
                    await FrameCodec.WriteAsync(stream, LocalFrame.HelloJson(FrameType.HelloReply, hello), _cts.Token);

                    continue;
                }

                lock (_asked) _asked.Add(frame.Text);
                await FrameCodec.WriteAsync(stream, LocalFrame.SteeringSetAck(answer!), _cts.Token);
            }
        } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException) {
            // Disposed: the listener is gone.
        }
    }

    public async ValueTask DisposeAsync() {
        await _cts.CancelAsync();
        _listener.Dispose();
        await _serving;
        _cts.Dispose();
    }
}
