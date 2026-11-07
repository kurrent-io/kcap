using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// End-to-end coverage of the Hello/HelloReply frame pair over a REAL Unix-domain socket —
/// the same <c>LocalControlServer.HandleConnectionAsync</c> routing switch a real
/// `kcap` client talks to. The harness mirrors <c>LaunchConsentIpcTests</c> (temp
/// per-test daemons directory, socket-file poll, Windows guard) but builds its own minimal
/// AgentOrchestrator, since none of these tests exercise Spawn/Attach/Stop — the
/// orchestrator (and the consent plumbing) only need to exist to satisfy
/// LocalControlServer's constructor.
/// </summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class LocalControlHelloTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    sealed class NoopHostLifetime : IHostApplicationLifetime {
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public void StopApplication() { }
    }

    sealed class NoopPtyProcessFactory : IPtyProcessFactory {
        public IPtyProcess Spawn(
                string command, string[] args, string cwd,
                Dictionary<string, string>? extraEnv = null, ushort cols = 120, ushort rows = 40,
                IReadOnlyCollection<string>? removeEnv = null
            ) => throw new NotSupportedException("LocalControlHelloTests never spawns a PTY");
    }

    sealed class NoopHttpClientFactory : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new();
    }

    sealed class NoopRestartStrategy : IRestartStrategy {
        public RestartOutcome Restart() => RestartOutcome.NoOp;
    }

    sealed record Harness(TempDaemonStore Daemons, LocalControlServer Server, AgentOrchestrator Orchestrator, ServerConnection Connection, DaemonConfig Config, string SockPath);

    async Task<Harness> StartAsync(string daemonName, CancellationToken ct, bool recording = false) {
        var daemons     = new TempDaemonStore();
        var stateRoot   = daemons.Store.StateDirectory(daemonName);
        var store       = new LaunchConsentStore(stateRoot, NullLogger.Instance, TimeProvider.System);
        var broker      = new LaunchConsentBroker();
        var decisionLog = new LaunchConsentDecisionLog(stateRoot, NullLogger.Instance);
        var gate        = new LaunchConsentGate(store, decisionLog, broker, TimeProvider.System, NullLogger<LaunchConsentGate>.Instance);

        var config = new DaemonConfig {
            Name         = daemonName,
            ServerUrl    = "http://127.0.0.1:1",
            Store        = daemons.Store,
            WorktreeRoot = daemons.PathTo("worktrees"),
            ConfigRoot   = Config.Root,
        };
        var consentIpc  = new LaunchConsentIpc(broker, store, config, NullLogger<LaunchConsentIpc>.Instance);

        var tokens           = AuthFixtures.NewTokenStore(Config.Root);
        var connection       = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance, TimeProvider.System);
        var worktreeManager  = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var repoMatcher      = new RepoMatcher(config, NullLogger<RepoMatcher>.Instance, TimeProvider.System);
        var permissionBridge = new LocalPermissionBridge(connection, NullLogger<LocalPermissionBridge>.Instance, EphemeralLoopbackPortSource.Instance, TimeProvider.System);

        var orchestrator = new AgentOrchestrator(
            config, Config.Root, TestHarnesses.Under(Home), connection, worktreeManager, repoMatcher,
            new NoopPtyProcessFactory(), new NoopHttpClientFactory(), new FixedCapacitorHttpClient(),
            tokens,
            permissionBridge, new Dictionary<string, IHostedAgentLauncher>(),
            new Dictionary<string, IHostedAgentRuntimeFactory>(), new NoopHostLifetime(),
            NullLogger<AgentOrchestrator>.Instance, gate, TimeProvider.System,
            recorder: recording ? Recording(config) : null);

        var permissionIpc = new PermissionIpc(new PermissionPromptBroker(), NullLogger<PermissionIpc>.Instance);
        var notifier = new DaemonStatusNotifier();
        var statusIpc = new DaemonStatusIpc(config, orchestrator, connection, notifier, TimeProvider.System);
        var settingsIpc = new DaemonSettingsIpc(config, orchestrator, notifier, NullLogger<DaemonSettingsIpc>.Instance);
        var restart = RestartCoordinator.ForTest(daemons.Store, daemonName, daemonName, new NoopRestartStrategy(), TimeProvider.System);
        var server = new LocalControlServer(config, orchestrator, restart, consentIpc, permissionIpc, statusIpc, settingsIpc, TestFences.Ipc(config, orchestrator), TestShutdown.Ipc(orchestrator, restart), TestAccountsIpc.Inert(), NullLogger<LocalControlServer>.Instance);
        await server.StartAsync(ct);

        var sockPath = daemons.Store.SocketPath(daemonName);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(sockPath) && DateTime.UtcNow < deadline) await Task.Delay(20, ct);

        return new Harness(daemons, server, orchestrator, connection, config, sockPath);
    }

    static async Task StopAsync(Harness h) {
        await h.Orchestrator.DisposeAsync();
        await h.Server.StopAsync(CancellationToken.None);
        h.Server.Dispose();
        await h.Connection.DisposeAsync();
        h.Daemons.Dispose();
    }

    /// Wraps a test body with the harness lifecycle, mirroring LaunchConsentIpcTests's RunAsync. The harness owns
    /// its own daemons directory, so nothing here is shared between tests; each [Test] still
    /// carries its own Windows guard, which must be visible on the test method itself.
    async Task RunAsync(string daemonName, Func<Harness, CancellationToken, Task> body, bool recording = false) {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        Harness? h = null;
        try {
            h = await StartAsync(daemonName, cts.Token, recording);
            await Assert.That(File.Exists(h.SockPath)).IsTrue();
            await body(h, cts.Token);
        } finally {
            if (h is not null) await StopAsync(h);
        }
    }

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
        await using var s = await ConnectAsync(sockPath, ct);
        await FrameCodec.WriteAsync(s, request, ct);

        return await FrameCodec.ReadAsync(s, ct);
    }

    static async Task<List<string>?> CapabilitiesAsync(string sockPath, CancellationToken ct) {
        var reply = await AskAsync(sockPath, new LocalFrame(FrameType.Hello), ct);

        return JsonSerializer.Deserialize(reply!.Text, HelloIpcJsonContext.Default.HelloReplyDto)!.Capabilities;
    }

    static async Task<NetworkStream> ConnectAsync(string sockPath, CancellationToken ct) {
        // Bind publishes the socket file before Listen. A connect in that gap is refused.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (true) {
            var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try {
                await sock.ConnectAsync(new UnixDomainSocketEndPoint(sockPath), ct);
                return new NetworkStream(sock, ownsSocket: true);
            } catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) {
                sock.Dispose();
                if (DateTime.UtcNow >= deadline) throw;
                await Task.Delay(20, ct);
            }
        }
    }

    [Test]
    public async Task Hello_with_client_info_gets_a_reply_naming_version_name_and_capabilities() {
        await RunAsync("hello-a", async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
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
            await Assert.That(await CapabilitiesAsync(h.SockPath, ct)).DoesNotContain(RecordingIpc.Capability);
        });
    }

    [Test]
    public async Task Hello_with_empty_payload_gets_an_identical_reply() {
        await RunAsync("hello-b", async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
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
            await using var s = await ConnectAsync(h.SockPath, ct);
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
            await using var s = await ConnectAsync(h.SockPath, ct);
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
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.List), ct);

            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp!.Type).IsEqualTo(FrameType.AgentList);
        });
    }

    [Test]
    public async Task SendText_frame_is_routed_and_answered_with_an_ack() {
        await RunAsync("hello-sendtext", async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
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
            await using var s = await ConnectAsync(h.SockPath, ct);
            // Detach is a valid, decodable FrameType that LocalControlServer's switch doesn't
            // route anywhere — it falls into the default arm, which is what this pins: the Error
            // reply for a decodable-but-unrouted frame. It is NOT the down-level discovery signal —
            // that is hello-then-EOF (a pre-hello daemon can't even decode byte 15), never an Error
            // frame (§3.1 of the design doc).
            await FrameCodec.WriteAsync(s, LocalFrame.Detach(), ct);

            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp!.Type).IsEqualTo(FrameType.Error);
            await Assert.That(resp.Text).Contains("Hello");
        });
    }
}
