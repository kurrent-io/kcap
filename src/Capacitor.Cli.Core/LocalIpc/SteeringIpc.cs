using System.Net.Sockets;
using System.Text.Json;

namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>The steering switch, asked of the daemon that owns it, and the steering socket that daemon opens while the
/// switch is on.</summary>
public static class SteeringIpc {
    /// <summary>Advertised by a daemon that answers <see cref="FrameType.SteeringSet"/>.</summary>
    public const string SwitchCapability = "steering-switch/1";

    public const string On  = "on";
    public const string Off = "off";

    /// <summary>Steering isn't offered on Windows until the mod's socket transport has been checked there.</summary>
    public static bool Offered => !OperatingSystem.IsWindows();

    // A daemon's reasons for refusing a SteeringSet.
    public const string Malformed  = "malformed";
    public const string NotOffered = "not_offered";
    public const string Unwritable = "unwritable";
    public const string Unbindable = "unbindable";

    // The client's own: no socket to connect to, a daemon too old for the frame, or no answer it could read.
    public const string Unreachable = "unreachable";
    public const string Unsupported = "unsupported";
    public const string Unanswered  = "unanswered";

    /// <summary>Turns the switch of the daemon named <paramref name="daemonName"/> on or off, through that daemon.</summary>
    public static async Task<SteeringSetAckDto> SetAsync(DaemonStore store, string daemonName, bool on, CancellationToken ct) {
        try {
            var hello = await ExchangeAsync(store, daemonName, new LocalFrame(FrameType.Hello), ct);
            if (hello?.Type != FrameType.HelloReply) return Failed(Unanswered);

            var capabilities = JsonSerializer.Deserialize(hello.Text, HelloIpcJsonContext.Default.HelloReplyDto)?.Capabilities ?? [];
            if (!capabilities.Contains(SwitchCapability)) return Failed(Unsupported);

            return await ExchangeAsync(store, daemonName, LocalFrame.SteeringSet(on), ct) is { Type: FrameType.SteeringSetAck, Text: var json }
                && JsonSerializer.Deserialize(json, SteeringIpcJsonContext.Default.SteeringSetAckDto) is { } ack
                    ? ack
                    : Failed(Unanswered);
        } catch (Exception ex) when (ex is SocketException or ArgumentException) {
            // Only the connect throws these: nothing listens there, or the path is too long for anything to.
            return Failed(Unreachable);
        } catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or OperationCanceledException) {
            return Failed(Unanswered);
        }
    }

    /// <summary>The steering socket of the daemon named <paramref name="daemonName"/> while something listens on it, or
    /// null: the switch is off, the daemon isn't running, or it died bound.</summary>
    public static async Task<string?> ListeningSocketAsync(DaemonStore store, string daemonName, CancellationToken ct) {
        var path = store.SteeringSocketPath(daemonName);
        if (!File.Exists(path)) return null;

        try {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);

            return path;
        } catch (Exception ex) when (ex is SocketException or ArgumentException or IOException or OperationCanceledException) {
            return null;
        }
    }

    static SteeringSetAckDto Failed(string reason) => new(false, reason, null);

    static async Task<LocalFrame?> ExchangeAsync(DaemonStore store, string daemonName, LocalFrame request, CancellationToken ct) {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(store.SocketPath(daemonName)), ct);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await FrameCodec.WriteAsync(stream, request, ct);

        return await FrameCodec.ReadAsync(stream, ct);
    }
}
