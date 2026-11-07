using System.Net.Sockets;
using System.Text.Json;

namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>What a hook asks of the daemon recording its session. Every answer is the daemon's own: a daemon that
/// can't be reached, or doesn't advertise <see cref="Capability"/>, answers false here, and <c>ct</c> running out reads
/// the same, because a hook has a recorder to arrange either way.</summary>
public static class RecordingIpc {
    /// <summary>Advertised by a daemon that records hosted sessions now, so a hook can leave them to it.</summary>
    public const string Capability = "recording/1";

    /// <summary>The daemon's switch, read from its own environment. Off unless set.</summary>
    public const string EnabledVar = "KCAP_DAEMON_RECORDING";

    public const string Recording = "recording";
    public const string Refused   = "refused";
    public const string Finished  = "finished";
    public const string NotHeld   = "not_held";

    /// <summary>Whether the daemon records <paramref name="source"/> now.</summary>
    public static async Task<bool> RecordAsync(DaemonStore store, string daemonName, RecordSourceDto source, CancellationToken ct) {
        var json  = JsonSerializer.Serialize(source, RecordingIpcJsonContext.Default.RecordSourceDto);
        var reply = await AskAsync(store, daemonName, LocalFrame.RecordSource(json), ct);

        return reply is { Type: FrameType.RecordSourceAck, Text: Recording };
    }

    /// <summary>Whether the daemon has made the final drain of the source <paramref name="key"/> names. A caller that
    /// stops waiting has still asked, and the daemon still finishes.</summary>
    public static async Task<bool> FinishAsync(DaemonStore store, string daemonName, string key, CancellationToken ct) {
        var reply = await AskAsync(store, daemonName, LocalFrame.FinishSource(key), ct);

        return reply is { Type: FrameType.FinishSourceAck, Text: Finished };
    }

    static async Task<LocalFrame?> AskAsync(DaemonStore store, string daemonName, LocalFrame request, CancellationToken ct) {
        try {
            var hello = await ExchangeAsync(store, daemonName, new LocalFrame(FrameType.Hello), ct);
            if (hello?.Type != FrameType.HelloReply) return null;

            var caps = JsonSerializer.Deserialize(hello.Text, HelloIpcJsonContext.Default.HelloReplyDto)?.Capabilities;

            return caps?.Contains(Capability) == true ? await ExchangeAsync(store, daemonName, request, ct) : null;
        } catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or JsonException or OperationCanceledException) {
            return null;
        }
    }

    static async Task<LocalFrame?> ExchangeAsync(DaemonStore store, string daemonName, LocalFrame request, CancellationToken ct) {
        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await sock.ConnectAsync(new UnixDomainSocketEndPoint(store.SocketPath(daemonName)), ct);
        await using var stream = new NetworkStream(sock, ownsSocket: false);
        await FrameCodec.WriteAsync(stream, request, ct);

        return await FrameCodec.ReadAsync(stream, ct);
    }
}
