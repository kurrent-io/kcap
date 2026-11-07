using System.Net.Sockets;
using System.Text.Json;

namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>What a hook asks of the daemon recording its session. Every answer is the daemon's own: a daemon that can't
/// be reached or lets <c>ct</c> run out has given none, and a hook has a recorder to arrange either way.</summary>
public static class RecordingIpc {
    /// <summary>Advertised by a daemon that records hosted sessions now, so a hook can leave them to it.</summary>
    public const string Capability = "recording/1";

    /// <summary>The daemon's switch, read from its own environment. Off unless set.</summary>
    public const string EnabledVar = "KCAP_DAEMON_RECORDING";

    public const string Recording = "recording";
    public const string Refused   = "refused";
    public const string Finished  = "finished";
    public const string NotHeld   = "not_held";

    /// <summary>The daemon's server hasn't said whether it can stop a source, so nothing is decided about this one.</summary>
    public const string NotYet = "not_yet";

    /// <summary>The refusal of a source that isn't the asking agent's.</summary>
    public const string NotTheAgents = "not_the_agents";

    /// <summary>How the daemon took <paramref name="source"/>.</summary>
    public static async Task<RecordingHandOver> RecordAsync(DaemonStore store, string daemonName, RecordSourceDto source, CancellationToken ct) {
        try {
            var hello = await ExchangeAsync(store, daemonName, new LocalFrame(FrameType.Hello), ct);
            if (hello?.Type != FrameType.HelloReply) return RecordingHandOver.Unanswered;

            var caps = JsonSerializer.Deserialize(hello.Text, HelloIpcJsonContext.Default.HelloReplyDto)?.Capabilities;
            if (caps?.Contains(Capability) != true) return RecordingHandOver.Declined;

            var json = JsonSerializer.Serialize(source, RecordingIpcJsonContext.Default.RecordSourceDto);

            return await ExchangeAsync(store, daemonName, LocalFrame.RecordSource(json), ct) switch {
                { Type: FrameType.RecordSourceAck, Text: Recording }    => RecordingHandOver.Taken,
                { Type: FrameType.RecordSourceAck, Text: NotTheAgents } => RecordingHandOver.Disowned,
                { Type: FrameType.RecordSourceAck, Text: NotYet }       => RecordingHandOver.Unanswered,
                { Type: FrameType.RecordSourceAck }                     => RecordingHandOver.Declined,
                _                                                       => RecordingHandOver.Unanswered,
            };
        } catch (Exception ex) when (IsNoAnswer(ex)) {
            return RecordingHandOver.Unanswered;
        }
    }

    /// <summary>Whether the daemon has made the final drain of the source <paramref name="key"/> names. A caller that
    /// stops waiting has still asked, and the daemon still finishes. No capability is asked for: a hook sends this only
    /// to the daemon with a claim, and one that has stopped taking sources still holds that one.</summary>
    public static async Task<bool> FinishAsync(DaemonStore store, string daemonName, string key, CancellationToken ct) {
        try {
            return await ExchangeAsync(store, daemonName, LocalFrame.FinishSource(key), ct) is { Type: FrameType.FinishSourceAck, Text: Finished };
        } catch (Exception ex) when (IsNoAnswer(ex)) {
            return false;
        }
    }

    static bool IsNoAnswer(Exception ex) => ex is SocketException or IOException or InvalidDataException or JsonException or OperationCanceledException;

    static async Task<LocalFrame?> ExchangeAsync(DaemonStore store, string daemonName, LocalFrame request, CancellationToken ct) {
        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await sock.ConnectAsync(new UnixDomainSocketEndPoint(store.SocketPath(daemonName)), ct);
        await using var stream = new NetworkStream(sock, ownsSocket: false);
        await FrameCodec.WriteAsync(stream, request, ct);

        return await FrameCodec.ReadAsync(stream, ct);
    }
}
