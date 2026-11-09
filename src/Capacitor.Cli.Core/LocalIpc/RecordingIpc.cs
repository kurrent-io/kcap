using System.Net.Sockets;
using System.Text.Json;

namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>What a hook, or <c>kcap disable</c>, asks of the daemon recording a session. Every answer is the daemon's own:
/// a daemon that can't be reached or lets <c>ct</c> run out has given none.</summary>
public static class RecordingIpc {
    /// <summary>Advertised by a daemon that records hosted sessions now, so a hook can leave them to it.</summary>
    public const string Capability = "recording/1";

    /// <summary>Advertised by a daemon that answers <see cref="FrameType.DisableSession"/>.</summary>
    public const string DisableCapability = "recording-disable/1";

    /// <summary>The daemon's switch, read from its own environment. Off unless set.</summary>
    public const string EnabledVar = "KCAP_DAEMON_RECORDING";

    public const string Recording = "recording";
    public const string Refused   = "refused";
    public const string Finished  = "finished";
    public const string NotHeld   = "not_held";
    public const string Stopped   = "stopped";

    /// <summary>The daemon sends nothing more of the session, and something it sent before that has no answer yet.</summary>
    public const string SendInFlight = "send_in_flight";

    /// <summary>A source of the session was told to stop and hadn't ended when the daemon stopped waiting.</summary>
    public const string StillStopping = "still_stopping";

    /// <summary>The daemon's server hasn't said whether it can stop a source, so nothing is decided about this one.</summary>
    public const string NotYet = "not_yet";

    /// <summary>The refusal of a source that isn't the asking agent's.</summary>
    public const string NotTheAgents = "not_the_agents";

    /// <summary>How the daemon took <paramref name="source"/>.</summary>
    public static async Task<RecordingHandOver> RecordAsync(DaemonStore store, string daemonName, RecordSourceDto source, CancellationToken ct) {
        try {
            if (await CapabilitiesAsync(store, daemonName, ct) is not { } capabilities) return RecordingHandOver.Unanswered;
            if (!capabilities.Contains(Capability)) return RecordingHandOver.Declined;

            var json = JsonSerializer.Serialize(source, RecordingIpcJsonContext.Default.RecordSourceDto);

            return await ExchangeAsync(store, daemonName, LocalFrame.RecordSource(json), ct) switch {
                { Type: FrameType.RecordSourceAck, Text: Recording }    => RecordingHandOver.Taken,
                { Type: FrameType.RecordSourceAck, Text: Refused }      => RecordingHandOver.Declined,
                { Type: FrameType.RecordSourceAck, Text: NotTheAgents } => RecordingHandOver.Disowned,
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

    /// <summary>Stops every source of a session a person disabled, with no final drain, on the daemon holding them. One that
    /// takes the connection and says nothing it can read is silent. A daemon too old for the frame records nothing this
    /// command could stop, so it answers as one with no socket.</summary>
    public static async Task<RecordingDisableAnswer> DisableAsync(DaemonStore store, string daemonName, string sessionId, CancellationToken ct) {
        try {
            if (await CapabilitiesAsync(store, daemonName, ct) is not { } capabilities) return new(RecordingDisable.Silent);
            if (!capabilities.Contains(DisableCapability)) return new(RecordingDisable.NoSocket);

            if (await ExchangeAsync(store, daemonName, LocalFrame.DisableSession(sessionId), ct) is not { Type: FrameType.DisableSessionAck, Text: var json }
             || JsonSerializer.Deserialize(json, RecordingIpcJsonContext.Default.DisableSessionAckDto) is not { } ack)
                return new(RecordingDisable.Silent);

            RecordingDisable? outcome = ack.Status switch {
                Stopped       => RecordingDisable.Stopped,
                SendInFlight  => RecordingDisable.SendInFlight,
                StillStopping => RecordingDisable.StillStopping,
                NotHeld       => RecordingDisable.NotHeld,
                _             => null,
            };

            return outcome is { } known ? new(known, ack.AgentLive, ack.Server, ack.MarkFailed) : new(RecordingDisable.Silent);
        } catch (SocketException) {
            // Only the connect throws this: nothing listens on the socket.
            return new(RecordingDisable.NoSocket);
        } catch (ArgumentException) {
            // A path too long to open a socket on, which nothing can be listening on either.
            return new(RecordingDisable.NoSocket);
        } catch (Exception ex) when (IsNoAnswer(ex)) {
            return new(RecordingDisable.Silent);
        }
    }

    // Null from a daemon that didn't say hello.
    static async Task<IReadOnlyList<string>?> CapabilitiesAsync(DaemonStore store, string daemonName, CancellationToken ct) {
        var hello = await ExchangeAsync(store, daemonName, new LocalFrame(FrameType.Hello), ct);

        return hello?.Type == FrameType.HelloReply ? JsonSerializer.Deserialize(hello.Text, HelloIpcJsonContext.Default.HelloReplyDto)?.Capabilities ?? [] : null;
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
