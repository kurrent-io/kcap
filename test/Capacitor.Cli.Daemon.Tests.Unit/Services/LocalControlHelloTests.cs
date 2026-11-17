using System.Text.Json;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// End-to-end coverage of the Hello/HelloReply frame pair over a REAL Unix-domain socket —
/// the same <c>LocalControlServer.HandleConnectionAsync</c> routing switch a real
/// `kcap` client talks to.
/// </summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class LocalControlHelloTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    Task RunAsync(string daemonName, Func<OrchestratorFixture, CancellationToken, Task> body, bool recording = false) =>
        new OrchestratorFixture(Config, Home, daemonName, setConfigRoot: true, recorder: recording ? Recording : null).ServeAsync(body);

    FakeRecorderTransport Transport { get; } = new() { IsConnected = true };

    DaemonRecorder? Recorder { get; set; }

    DaemonRecorder Recording(DaemonConfig config) {
        config.RecordHostedClaude = true;
        config.ConfigRoot         = Config.Root;
        config.Profiles           = Resolutions.None(Config.Root);

        var probe = new RecordingProbe();
        probe.Report(true);

        return Recorder = new DaemonRecorder(
            config, new CapacitorServer(config.ServerUrl, Config.Root, config.Profiles), probe, () => Transport,
            (_, _, _, _) => new FakeRecorderHousekeeping(), TimeProvider.System, NullLogger<DaemonRecorder>.Instance);
    }

    static async Task<LocalFrame?> AskAsync(string sockPath, LocalFrame request, CancellationToken ct) {
        await using var s = await ControlSocket.ConnectAsync(sockPath, ct);
        await FrameCodec.WriteAsync(s, request, ct);

        return await FrameCodec.ReadAsync(s, ct);
    }

    static async Task<List<string>?> CapabilitiesAsync(string sockPath, CancellationToken ct) {
        var reply = await AskAsync(sockPath, new LocalFrame(FrameType.Hello), ct);

        return JsonSerializer.Deserialize(reply!.Text, HelloIpcJsonContext.Default.HelloReplyDto)!.Capabilities;
    }

    static readonly string AcceptsStarting =
        JsonSerializer.Serialize(new ClientHelloDto("kcap-app", "1.2.3", AcceptsStarting: true), HelloIpcJsonContext.Default.ClientHelloDto);

    static HelloReplyDto Reply(LocalFrame? frame) => JsonSerializer.Deserialize(frame!.Text, HelloIpcJsonContext.Default.HelloReplyDto)!;

    /// <summary>A daemon as it stands between binding its socket and finishing its vendor probes.</summary>
    sealed record Starting(OrchestratorFixture Fixture, LocalControlSocket Socket, StartingLocalControl Listener);

    Starting BindStarting(string daemonName, CancellationToken ct) {
        var fixture = new OrchestratorFixture(Config, Home, daemonName, setConfigRoot: true);
        fixture.Config.InstanceId = "instance-under-test";
        var socket   = new LocalControlSocket(fixture.Config);
        var listener = new StartingLocalControl(fixture.Config, NullLogger<StartingLocalControl>.Instance);
        listener.Start(socket.Bind(), ct);

        return new(fixture, socket, listener);
    }

    /// <summary>The socket is bound before the vendor probes, so a client that understands a starting daemon hears from
    /// it at once; it is told nothing it could gate a feature on.</summary>
    [Test]
    public async Task A_starting_daemon_answers_a_hello_that_accepts_it_at_once_and_advertises_nothing() {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var starting  = BindStarting("hello-starting", cts.Token);
        await using var fixture = starting.Fixture;
        using var socket  = starting.Socket;

        var reply = Reply(await AskAsync(socket.Path, LocalFrame.HelloJson(FrameType.Hello, AcceptsStarting), cts.Token));

        await Assert.That(reply.Starting == true).IsTrue();
        await Assert.That(reply.Capabilities).IsEmpty();
        await Assert.That(reply.DaemonName).IsEqualTo("hello-starting");
        await Assert.That(reply.InstanceId).IsEqualTo("instance-under-test");
    }

    /// <summary>A launch held through a long probe would still start after its client gave up, and the retry would start
    /// a second one, so a starting daemon refuses it for the client to send again.</summary>
    [Test]
    public async Task A_starting_daemon_refuses_a_launch_for_the_client_to_retry() {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var starting  = BindStarting("spawn-starting", cts.Token);
        await using var fixture = starting.Fixture;
        using var socket  = starting.Socket;

        var reply = await AskAsync(socket.Path, new LocalFrame(FrameType.Spawn), cts.Token);

        await Assert.That(reply!.Type).IsEqualTo(FrameType.Error);
        await Assert.That(reply.Text).IsEqualTo(StartingLocalControl.StartingRefusal);
    }

    /// <summary>Every client that reads capabilities sends a plain hello and gates on what comes back, so a starting
    /// daemon holds it and the ready daemon answers it: the client never sees a starting daemon's empty list as final.
    /// Once ready, a hello that accepts a starting daemon gets the ordinary reply.</summary>
    [Test]
    public async Task A_plain_hello_sent_while_starting_is_answered_by_the_ready_daemon() {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var starting  = BindStarting("hello-held", cts.Token);
        await using var fixture = starting.Fixture;

        var held = AskAsync(starting.Socket.Path, new LocalFrame(FrameType.Hello), cts.Token);
        await Task.Delay(200, cts.Token);
        await Assert.That(held.IsCompleted).IsFalse();

        await fixture.StartServerAsync(cts.Token, socket: starting.Socket, starting: starting.Listener);

        var answered = Reply(await held.WaitAsync(TimeSpan.FromSeconds(5), cts.Token));
        await Assert.That(answered.Starting).IsNull();
        await Assert.That(answered.Capabilities).IsEquivalentTo(LocalControlCapabilities.Current);

        var ready = Reply(await AskAsync(fixture.SockPath, LocalFrame.HelloJson(FrameType.Hello, AcceptsStarting), cts.Token));
        await Assert.That(ready.Starting).IsNull();
        await Assert.That(ready.Capabilities).IsEquivalentTo(LocalControlCapabilities.Current);
    }

    [Test]
    public async Task Hello_with_client_info_gets_a_reply_naming_version_name_and_capabilities() {
        await RunAsync("hello-a", async (h, ct) => {
            await using var s = await ControlSocket.ConnectAsync(h.SockPath, ct);
            var clientHello = JsonSerializer.Serialize(
                new ClientHelloDto("kcap-cli", "1.2.3"), HelloIpcJsonContext.Default.ClientHelloDto);
            await FrameCodec.WriteAsync(s, LocalFrame.HelloJson(FrameType.Hello, clientHello), ct);

            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp!.Type).IsEqualTo(FrameType.HelloReply);
            var dto = JsonSerializer.Deserialize(resp.Text, HelloIpcJsonContext.Default.HelloReplyDto);
            await Assert.That(dto!.ProtocolVersion).IsEqualTo(1);
            await Assert.That(dto.DaemonVersion).IsNotEmpty();
            await Assert.That(dto.DaemonName).IsEqualTo(h.Config.Name);
            await Assert.That(dto.Capabilities).IsEquivalentTo(LocalControlCapabilities.Current);
        });
    }

    /// <summary>A hook leaves a hosted session to a daemon that says this, so it can't be said behind a handler that
    /// isn't there.</summary>
    [Test]
    public async Task A_recording_daemon_says_so_and_answers_its_frames() {
        await RunAsync("hello-rec", async (h, ct) => {
            await Assert.That(await CapabilitiesAsync(h.SockPath, ct)).Contains(RecordingIpc.Capability);

            var json = JsonSerializer.Serialize(
                new RecordSourceDto("nobody", "session-1", null, "/t.jsonl", "claude", null, false), RecordingIpcJsonContext.Default.RecordSourceDto);
            var record = await AskAsync(h.SockPath, LocalFrame.RecordSource(json), ct);
            var finish = await AskAsync(h.SockPath, LocalFrame.FinishSource("session-1"), ct);

            await Assert.That((record!.Type, record.Text)).IsEqualTo((FrameType.RecordSourceAck, RecordingIpc.Refused));
            await Assert.That((finish!.Type, finish.Text)).IsEqualTo((FrameType.FinishSourceAck, RecordingIpc.NotHeld));
        }, recording: true);
    }

    /// <summary>A terminal hook sends its frame only to a daemon saying this, through the same routing switch, so the
    /// frame must reach its handler: one that didn't would drop the connection and every terminal session would read as
    /// unanswered.</summary>
    [Test]
    public async Task A_recording_daemon_says_it_takes_terminal_sessions_and_answers_their_frame() {
        await RunAsync("hello-term", async (h, ct) => {
            await Assert.That(await CapabilitiesAsync(h.SockPath, ct)).Contains(RecordingIpc.TerminalCapability);

            var json = JsonSerializer.Serialize(
                new RecordTerminalSourceDto("session-1", null, "/t.jsonl", "claude", null, false, "nobody", "https://elsewhere.example", "/c", "/c/w", "/h", 4242, "test:claude"),
                RecordingIpcJsonContext.Default.RecordTerminalSourceDto);
            var record = await AskAsync(h.SockPath, LocalFrame.RecordTerminalSource(json), ct);

            await Assert.That((record!.Type, record.Text)).IsEqualTo((FrameType.RecordSourceAck, RecordingIpc.Refused));
        }, recording: true);
    }

    /// <summary>Beside the routing switch or not at all: a capability with no handler would be a stop that never lands.</summary>
    [Test]
    public async Task Every_daemon_answers_a_disable() {
        await RunAsync("hello-dis", async (h, ct) => {
            await Assert.That(await CapabilitiesAsync(h.SockPath, ct)).Contains(RecordingIpc.DisableCapability);

            var disable = await AskAsync(h.SockPath, LocalFrame.DisableSession("session-1"), ct);
            var answer  = JsonSerializer.Deserialize(disable!.Text, RecordingIpcJsonContext.Default.DisableSessionAckDto);

            await Assert.That(disable.Type).IsEqualTo(FrameType.DisableSessionAck);
            await Assert.That(answer).IsEqualTo(new DisableSessionAckDto(RecordingIpc.NotHeld, false, h.Config.ServerUrl));
        });
    }

    /// <summary>The two halves of <c>kcap disable</c> joined at the wire: the call the command makes, over the real socket,
    /// to a daemon recording the session. The rest of the command is in an executable this project can't reference.</summary>
    [Test]
    public async Task A_disable_over_the_socket_stops_what_the_daemon_records() {
        const string session = "0123456789abcdef0123456789abcdef";

        await RunAsync("hello-join", async (h, ct) => {
            var transcript = Tmp.CreateFile("t.jsonl", """{"type":"user","message":{"content":"Summarise the README"}}""" + "\n");
            var source     = new RecordedSource("agent-1", session, null, transcript, "claude", null, false);
            await Recorder!.TryRecordAsync(source, ct);
            await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);

            var answer = await RecordingIpc.DisableAsync(h.Daemons.Store, "hello-join", session, ct);

            await Assert.That(answer).IsEqualTo(new RecordingDisableAnswer(RecordingDisable.Stopped, AgentLive: false, Server: h.Config.ServerUrl));
            await Assert.That(Recorder.ActiveKeys).IsEmpty();

            // The silence would refuse a final drain, so seeing no drain proves nothing. No attempt being refused does.
            await Assert.That(Transport.DrainCompletes).IsEqualTo(0);
            await Assert.That(Transport.Refused).IsEqualTo(0);
        }, recording: true);
    }

    [Test]
    public async Task A_daemon_with_recording_off_does_not_say_so() {
        await RunAsync("hello-off", async (h, ct) => {
            var capabilities = await CapabilitiesAsync(h.SockPath, ct);

            await Assert.That(capabilities).DoesNotContain(RecordingIpc.Capability);
            await Assert.That(capabilities).DoesNotContain(RecordingIpc.TerminalCapability);
        });
    }

    [Test]
    public async Task Hello_with_empty_payload_gets_an_identical_reply() {
        await RunAsync("hello-b", async (h, ct) => {
            await using var s = await ControlSocket.ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.Hello), ct);

            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp!.Type).IsEqualTo(FrameType.HelloReply);
            var dto = JsonSerializer.Deserialize(resp.Text, HelloIpcJsonContext.Default.HelloReplyDto);
            await Assert.That(dto!.ProtocolVersion).IsEqualTo(1);
            await Assert.That(dto.DaemonVersion).IsNotEmpty();
            await Assert.That(dto.DaemonName).IsEqualTo(h.Config.Name);
            await Assert.That(dto.Capabilities).IsEquivalentTo(LocalControlCapabilities.Current);
        });
    }

    [Test]
    public async Task Hello_with_malformed_json_payload_is_treated_as_empty_and_still_replies() {
        await RunAsync("hello-c", async (h, ct) => {
            await using var s = await ControlSocket.ConnectAsync(h.SockPath, ct);
            // Payload is diagnostics-only — malformed JSON must not drop the connection or
            // change the reply in any way.
            await FrameCodec.WriteAsync(s, LocalFrame.HelloJson(FrameType.Hello, "{not json"), ct);

            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp).IsNotNull();
            await Assert.That(resp!.Type).IsEqualTo(FrameType.HelloReply);
            var dto = JsonSerializer.Deserialize(resp.Text, HelloIpcJsonContext.Default.HelloReplyDto);
            await Assert.That(dto!.ProtocolVersion).IsEqualTo(1);
            await Assert.That(dto.DaemonVersion).IsNotEmpty();
            await Assert.That(dto.DaemonName).IsEqualTo(h.Config.Name);
            await Assert.That(dto.Capabilities).IsEquivalentTo(LocalControlCapabilities.Current);
        });
    }

    [Test]
    public async Task Hello_reply_carries_pid_and_instance_id() {
        await RunAsync("hello-id", async (h, ct) => {
            h.Config.InstanceId = "inst-test-1";
            await using var s = await ControlSocket.ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.Hello), ct);
            var frame = await FrameCodec.ReadAsync(s, ct);
            var dto = JsonSerializer.Deserialize(frame!.Text, HelloIpcJsonContext.Default.HelloReplyDto);

            await Assert.That(dto!.Pid).IsEqualTo(Environment.ProcessId);
            await Assert.That(dto.InstanceId).IsEqualTo("inst-test-1");
        });
    }

    [Test]
    public async Task List_still_returns_AgentList_alongside_the_new_Hello_route() {
        await RunAsync("hello-d", async (h, ct) => {
            await using var s = await ControlSocket.ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.List), ct);

            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp!.Type).IsEqualTo(FrameType.AgentList);
        });
    }

    [Test]
    public async Task SendText_frame_is_routed_and_answered_with_an_ack() {
        await RunAsync("hello-sendtext", async (h, ct) => {
            await using var s = await ControlSocket.ConnectAsync(h.SockPath, ct);
            var json = JsonSerializer.Serialize(new SendTextDto("nope", "hi"), InputIpcJsonContext.Default.SendTextDto);
            await FrameCodec.WriteAsync(s, LocalFrame.InputJson(FrameType.SendText, json), ct);

            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp!.Type).IsEqualTo(FrameType.SendTextAck);
            var ack = JsonSerializer.Deserialize(resp.Text, InputIpcJsonContext.Default.SendTextAckDto)!;
            await Assert.That(ack.Reason).IsEqualTo(SendTextReasons.NoSuchAgent);
        });
    }

    /// <summary>The client a hosted agent's MCP server runs, against the real routing switch: it sends the
    /// frame only after hello advertises it, and reads the agent's session from the reply.</summary>
    [Test]
    public async Task An_agent_session_query_is_advertised_routed_and_answered() {
        await RunAsync("hello-session", async (h, ct) => {
            AgentOrchestratorHarness.BindTranscript(
                AgentOrchestratorHarness.SeedAcpAgent(h.Orchestrator, "agent-7", new FakeAcpRuntime { AcpSessionId = "ses_01JC7QY3VZ8K" }));

            var bound   = await AgentSessionIpc.QueryAsync(h.Daemons.Store, h.Config.Name, "agent-7", TimeProvider.System, TimeSpan.FromSeconds(10), ct);
            var unknown = await AgentSessionIpc.QueryAsync(h.Daemons.Store, h.Config.Name, "nobody", TimeProvider.System, TimeSpan.FromSeconds(10), ct);

            await Assert.That(bound).IsEqualTo("ses_01JC7QY3VZ8K");
            await Assert.That(unknown).IsNull();
        });
    }

    [Test]
    public async Task Unrouted_frame_type_gets_an_error_reply_mentioning_hello() {
        await RunAsync("hello-e", async (h, ct) => {
            await using var s = await ControlSocket.ConnectAsync(h.SockPath, ct);
            // Detach is a valid, decodable FrameType that LocalControlServer's switch doesn't
            // route anywhere — it falls into the default arm, which is what this pins: the Error
            // reply for a decodable-but-unrouted frame. It is NOT the down-level discovery signal —
            // that is hello-then-EOF (a pre-hello daemon can't even decode byte 15), never an Error
            // frame.
            await FrameCodec.WriteAsync(s, LocalFrame.Detach(), ct);

            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp!.Type).IsEqualTo(FrameType.Error);
            await Assert.That(resp.Text).Contains("Hello");
        });
    }
}
