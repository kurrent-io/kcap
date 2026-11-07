using System.Net.Sockets;
using System.Text.Json;

namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>What a hook, or <c>kcap disable</c>, asks of the daemon recording a session. Every answer is the daemon's own:
/// a daemon that can't be reached or lets <c>ct</c> run out has given none.</summary>
public static class RecordingIpc {
    /// <summary>Advertised by a daemon that records hosted sessions now, so a hook can leave them to it.</summary>
    public const string Capability = "recording/1";

    /// <summary>Advertised by a daemon that records terminal sessions now, so a hook outside any hosted agent can hand
    /// one over.</summary>
    public const string TerminalCapability = "recording-terminal/1";

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

    /// <summary>How long a terminal hook waits for the hello. A daemon that is up answers in well under a millisecond,
    /// and one stopped, starved or swapped out would otherwise spend the hook's whole hand-off on it.</summary>
    public static readonly TimeSpan TerminalHelloWait = TimeSpan.FromMilliseconds(250);

    /// <summary>How the daemon took <paramref name="source"/>.</summary>
    public static async Task<RecordingHandOver> RecordAsync(DaemonStore store, string daemonName, RecordSourceDto source, CancellationToken ct) {
        try {
            if (await CapabilitiesAsync(store, daemonName, ct) is not { } capabilities) return RecordingHandOver.Unanswered;
            if (!capabilities.Contains(Capability)) return RecordingHandOver.Declined;

            var json = JsonSerializer.Serialize(source, RecordingIpcJsonContext.Default.RecordSourceDto);

            return HandOverOf(await ExchangeAsync(store, daemonName, LocalFrame.RecordSource(json), ct));
        } catch (Exception ex) when (IsNoAnswer(ex)) {
            return RecordingHandOver.Unanswered;
        }
    }

    /// <summary>How the daemon took a terminal session's source. <paramref name="describe"/> runs only once the daemon has
    /// said it takes terminal sessions, so one that doesn't costs the hook its hello and nothing more; null from it hands
    /// nothing over.</summary>
    public static async Task<RecordingHandOver> RecordTerminalAsync(
            DaemonStore store, string daemonName, Func<RecordTerminalSourceDto?> describe, TimeProvider time, CancellationToken ct) {
        try {
            IReadOnlyList<string>? capabilities;

            using (var wait = new CancellationTokenSource(TerminalHelloWait, time))
            using (var hello = CancellationTokenSource.CreateLinkedTokenSource(ct, wait.Token)) {
                capabilities = await CapabilitiesAsync(store, daemonName, hello.Token);
            }

            if (capabilities is null) return RecordingHandOver.Unanswered;
            if (!capabilities.Contains(TerminalCapability) || describe() is not { } source) return RecordingHandOver.Declined;

            var json = JsonSerializer.Serialize(source, RecordingIpcJsonContext.Default.RecordTerminalSourceDto);

            return HandOverOf(await ExchangeAsync(store, daemonName, LocalFrame.RecordTerminalSource(json), ct));
        } catch (Exception ex) when (IsNoAnswer(ex)) {
            return RecordingHandOver.Unanswered;
        }
    }

    static RecordingHandOver HandOverOf(LocalFrame? answer) => answer switch {
        { Type: FrameType.RecordSourceAck, Text: Recording }    => RecordingHandOver.Taken,
        { Type: FrameType.RecordSourceAck, Text: Refused }      => RecordingHandOver.Declined,
        { Type: FrameType.RecordSourceAck, Text: NotTheAgents } => RecordingHandOver.Disowned,
        _                                                       => RecordingHandOver.Unanswered,
    };

    /// <summary>How the daemon answered an ask for the final drain of the source <paramref name="key"/> names. A caller
    /// that stops waiting has still asked, and the daemon still finishes. No capability is asked for: a hook sends this
    /// only to the daemon with a claim, and one that has stopped taking sources still holds that one.</summary>
    /// <param name="from">The coding-agent process the asking hook runs under, when it knows it.</param>
    public static async Task<RecordingFinish> FinishAsync(DaemonStore store, string daemonName, string key, ProcessIncarnation? from, CancellationToken ct) {
        try {
            return await ExchangeAsync(store, daemonName, LocalFrame.FinishSource(FinishText(key, from)), ct) switch {
                { Type: FrameType.FinishSourceAck, Text: Finished } => RecordingFinish.Finished,
                { Type: FrameType.FinishSourceAck, Text: NotHeld }  => RecordingFinish.NotHeld,
                _                                                   => RecordingFinish.Unanswered,
            };
        } catch (SocketException) {
            // Only the connect throws this: nothing listens on the socket.
            return RecordingFinish.Unreachable;
        } catch (ArgumentException) {
            // A path too long to open a socket on, which nothing can be listening on either.
            return RecordingFinish.Unreachable;
        } catch (Exception ex) when (IsNoAnswer(ex)) {
            return RecordingFinish.Unanswered;
        }
    }

    /// <summary>A <see cref="FrameType.FinishSource"/> payload: the key, then the asking process's pid and start token.</summary>
    public static string FinishText(string key, ProcessIncarnation? from) =>
        from is null ? key : $"{key}\n{from.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n{from.StartToken}";

    /// <summary>What <see cref="FinishText"/> wrote. A process that doesn't read back is no process.</summary>
    public static (string Key, ProcessIncarnation? From) ReadFinish(string text) =>
        text.Split('\n') is [var key, var pid, var token]
     && int.TryParse(pid, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 1 && token.Length > 0
            ? (key, new ProcessIncarnation(id, token))
            : (text.Split('\n')[0], null);

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

            return ack.Status switch {
                Stopped       => new(RecordingDisable.Stopped, ack.AgentLive, ack.Server),
                SendInFlight  => new(RecordingDisable.SendInFlight, ack.AgentLive, ack.Server),
                StillStopping => new(RecordingDisable.StillStopping, ack.AgentLive, ack.Server),
                NotHeld       => new(RecordingDisable.NotHeld, ack.AgentLive, ack.Server),
                _             => new(RecordingDisable.Silent),
            };
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
