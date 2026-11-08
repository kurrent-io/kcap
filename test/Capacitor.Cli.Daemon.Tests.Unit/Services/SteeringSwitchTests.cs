using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The steering switch: through a real control socket, across daemon restarts over one state directory, where
/// each incarnation reads the consent document afresh and starts its switch as the host does; and on its own, for
/// what it answers when it can't move.</summary>
public class SteeringSwitchTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    sealed class NoopRestartStrategy : IRestartStrategy {
        public RestartOutcome Restart() => RestartOutcome.NoOp;
    }

    sealed record Incarnation(LocalControlServer Server, SteeringSwitch Steering);

    CaptureServerConnection Connection { get; } = new();

    async Task<Incarnation> StartAsync(AgentOrchestrator orchestrator, DaemonConfig config, CancellationToken ct) {
        var stateRoot = config.Store.StateDirectory(config.Name);
        var consent   = new LaunchConsentStore(stateRoot, NullLogger.Instance, TimeProvider.System);
        var steering  = new SteeringSwitch(consent, TestSteering.Endpoint(config));
        await steering.StartAsync(ct);

        var notifier = new DaemonStatusNotifier();
        var restart  = RestartCoordinator.ForTest(config.Store, config.Name, "test", new NoopRestartStrategy(), TimeProvider.System);
        var control  = new LocalControlServer(
            config, orchestrator, restart,
            new LaunchConsentIpc(new LaunchConsentBroker(), consent, config, NullLogger<LaunchConsentIpc>.Instance),
            new PermissionIpc(new PermissionPromptBroker(), NullLogger<PermissionIpc>.Instance),
            new DaemonStatusIpc(config, orchestrator, Connection, notifier, TimeProvider.System),
            new DaemonSettingsIpc(config, orchestrator, notifier, NullLogger<DaemonSettingsIpc>.Instance),
            TestFences.Ipc(config, orchestrator), TestShutdown.Ipc(orchestrator, restart), TestAccountsIpc.Inert(), steering,
            NullLogger<LocalControlServer>.Instance);
        await control.StartAsync(ct);
        await WaitUntilListeningAsync(config.Store.SocketPath(config.Name), ct);

        return new(control, steering);
    }

    static async Task StopAsync(Incarnation daemon) {
        await daemon.Server.StopAsync(CancellationToken.None);
        daemon.Server.Dispose();
        await daemon.Steering.StopAsync(CancellationToken.None);
    }

    /// <summary>The control server publishes its socket file a moment before it listens.</summary>
    static async Task WaitUntilListeningAsync(string path, CancellationToken ct) {
        while (true) {
            try {
                using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await probe.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);

                return;
            } catch (SocketException) {
                await Task.Delay(20, ct);
            }
        }
    }

    AgentOrchestrator Orchestrator(out DaemonConfig config) {
        DaemonConfig? captured = null;
        var orchestrator = AgentOrchestratorHarness.BuildOrchestrator(
            Connection, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(), configure: c => captured = c);
        config = captured!;

        return orchestrator;
    }

    [Test]
    [ExcludeOn(OS.Windows)] // steering isn't offered on Windows
    public async Task Steering_on_and_off_round_trips_through_the_daemon() {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var orchestrator = Orchestrator(out var config);
        var socket       = config.Store.SteeringSocketPath(config.Name);
        Incarnation? daemon = null;

        try {
            daemon = await StartAsync(orchestrator, config, cts.Token);
            await Assert.That(await SteeringIpc.ListeningSocketAsync(config.Store, config.Name, cts.Token)).IsNull();

            await Assert.That(await SteeringIpc.SetAsync(config.Store, config.Name, on: true, cts.Token))
                .IsEqualTo(new SteeringSetAckDto(true, null, SteeringIpc.On));
            await Assert.That(await SteeringIpc.ListeningSocketAsync(config.Store, config.Name, cts.Token)).IsEqualTo(socket);

            await StopAsync(daemon);
            daemon = null;
            await Assert.That(File.Exists(socket)).IsFalse();

            daemon = await StartAsync(orchestrator, config, cts.Token);
            await Assert.That(await SteeringIpc.ListeningSocketAsync(config.Store, config.Name, cts.Token)).IsEqualTo(socket);

            await Assert.That(await SteeringIpc.SetAsync(config.Store, config.Name, on: false, cts.Token))
                .IsEqualTo(new SteeringSetAckDto(true, null, SteeringIpc.Off));
            await Assert.That(File.Exists(socket)).IsFalse();

            await StopAsync(daemon);
            daemon = await StartAsync(orchestrator, config, cts.Token);
            await Assert.That(await SteeringIpc.ListeningSocketAsync(config.Store, config.Name, cts.Token)).IsNull();
        } finally {
            if (daemon is not null) await StopAsync(daemon);
            await orchestrator.DisposeAsync();
        }
    }

    [Test]
    [ExcludeOn(OS.Windows)] // steering isn't offered on Windows
    public async Task A_switch_request_that_is_neither_on_nor_off_changes_nothing() {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var orchestrator = Orchestrator(out var config);
        Incarnation? daemon = null;

        try {
            daemon = await StartAsync(orchestrator, config, cts.Token);

            using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await sock.ConnectAsync(new UnixDomainSocketEndPoint(config.Store.SocketPath(config.Name)), cts.Token);
            await using var stream = new NetworkStream(sock, ownsSocket: false);
            await FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.SteeringSet) { Text = "yes" }, cts.Token);
            var reply = await FrameCodec.ReadAsync(stream, cts.Token);

            await Assert.That(reply!.Type).IsEqualTo(FrameType.SteeringSetAck);
            await Assert.That(JsonSerializer.Deserialize(reply.Text, SteeringIpcJsonContext.Default.SteeringSetAckDto))
                .IsEqualTo(new SteeringSetAckDto(false, SteeringIpc.Malformed, SteeringIpc.Off));
            await Assert.That(File.Exists(config.Store.SteeringSocketPath(config.Name))).IsFalse();
        } finally {
            if (daemon is not null) await StopAsync(daemon);
            await orchestrator.DisposeAsync();
        }
    }

    LaunchConsentStore Consent() =>
        new(Daemons.Store.StateDirectory(Name), NullLogger.Instance, TimeProvider.System);

    SteeringEndpoint Endpoint() =>
        new(Daemons.Store.SteeringSocketPath(Name), TestSteering.NoRoutes, TimeProvider.System, NullLogger.Instance);

    const string Name = "t";

    /// <summary>The switch's own answer to a frame, without a control server in front of it.</summary>
    static async Task<SteeringSetAckDto> AskAsync(SteeringSwitch steering, string payload) {
        using var stream = new MemoryStream();
        await steering.HandleSetAsync(payload, stream, CancellationToken.None);
        stream.Position = 0;
        var reply = await FrameCodec.ReadAsync(stream, CancellationToken.None);

        return JsonSerializer.Deserialize(reply!.Text, SteeringIpcJsonContext.Default.SteeringSetAckDto)!;
    }

    /// <summary>Where steering isn't offered, a switch left on is not acted on at start, and a request to move it is
    /// refused.</summary>
    [Test]
    public async Task The_daemon_refuses_steering_where_it_is_not_offered() {
        var consent = Consent();
        consent.TrySetSteering(true, out _);
        await using var endpoint = Endpoint();
        using var steering = new SteeringSwitch(consent, endpoint, offered: false);

        await steering.StartAsync(CancellationToken.None);
        await Assert.That(endpoint.IsBound).IsFalse();

        await Assert.That(await AskAsync(steering, SteeringIpc.On))
            .IsEqualTo(new SteeringSetAckDto(false, SteeringIpc.NotOffered, SteeringIpc.On));
        await Assert.That(endpoint.IsBound).IsFalse();
    }

    [Test]
    [ExcludeOn(OS.Windows)] // steering isn't offered on Windows
    public async Task A_socket_that_will_not_bind_is_refused_with_why() {
        Directory.CreateDirectory(Daemons.Store.SteeringSocketPath(Name));
        var consent = Consent();
        await using var endpoint = Endpoint();
        using var steering = new SteeringSwitch(consent, endpoint);

        var ack = await AskAsync(steering, SteeringIpc.On);

        await Assert.That(ack.Ok).IsFalse();
        await Assert.That(ack.Reason).IsEqualTo(SteeringIpc.Unbindable);
        await Assert.That(ack.Steering).IsEqualTo(SteeringIpc.Off);
        await Assert.That(ack.Detail).IsNotNull().And.IsNotEmpty();
        await Assert.That(consent.SteeringOn).IsFalse();
    }

    /// <summary>The switch can't be written while the policy beside it can't be read; the socket bound for it goes
    /// again, so the two still agree.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // the read is failed through the file's mode
    [UnsupportedOSPlatform("windows")]
    public async Task A_switch_that_cannot_be_saved_is_refused_with_why() {
        var policy = Daemons.CreateFile(Path.Combine(Name, "consent.json"), """{"default":"deny"}""");
        File.SetUnixFileMode(policy, UnixFileMode.None);
        var consent = Consent();
        await using var endpoint = Endpoint();
        using var steering = new SteeringSwitch(consent, endpoint);

        var ack = await AskAsync(steering, SteeringIpc.On);

        await Assert.That(ack.Ok).IsFalse();
        await Assert.That(ack.Reason).IsEqualTo(SteeringIpc.Unwritable);
        await Assert.That(ack.Detail).Contains("could not read");
        await Assert.That(endpoint.IsBound).IsFalse();
        await Assert.That(File.Exists(Daemons.Store.SteeringSocketPath(Name))).IsFalse();
        File.SetUnixFileMode(policy, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
