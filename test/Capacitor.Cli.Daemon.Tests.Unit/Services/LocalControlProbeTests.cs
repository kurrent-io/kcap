using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// End-to-end coverage of <see cref="LocalControlProbe.ProbeAsync"/> over a REAL Unix-domain
/// socket.
/// </summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class LocalControlProbeTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    Task RunAsync(string daemonName, Func<OrchestratorFixture, CancellationToken, Task> body) =>
        new OrchestratorFixture(Config, Home, daemonName).ServeAsync(body);

    [Test]
    public async Task Probe_returns_hello_and_first_snapshot_with_consistent_identity() {
        await RunAsync("probe-a", async (h, ct) => {
            h.Config.InstanceId = "inst-p1";
            var r = await LocalControlProbe.ProbeAsync(h.Daemons.Store, "probe-a", TimeProvider.System, TimeSpan.FromSeconds(5), ct);

            await Assert.That(r.Reachable).IsTrue();
            await Assert.That(r.Hello!.DaemonName).IsEqualTo("probe-a");
            await Assert.That(r.Snapshot!.Daemon.InstanceId).IsEqualTo("inst-p1");
            await Assert.That(r.IdentityConsistent).IsTrue();
        });
    }

    [Test]
    public async Task Probe_on_missing_socket_reports_unreachable_without_throwing() {
        using var daemons = new TempDaemonStore();

        var r = await LocalControlProbe.ProbeAsync(daemons.Store, "no-such-daemon-xyz", TimeProvider.System, TimeSpan.FromMilliseconds(500));
        await Assert.That(r.Reachable).IsFalse();
        await Assert.That(r.Hello).IsNull();
    }

    /// <summary>A same-named daemon can replace the recorded one between reading its PID file and probing the socket;
    /// its starting reply must not be pinned on the recorded process.</summary>
    [Test]
    public async Task Starting_probe_counts_only_a_reply_from_the_expected_process() {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var fixture = new OrchestratorFixture(Config, Home, "starting-pid");
        using var socket   = new LocalControlSocket(fixture.Config);
        using var listener = new StartingLocalControl(fixture.Config, NullLogger<StartingLocalControl>.Instance);
        listener.Start(socket.Bind(), cts.Token);

        var store = fixture.Daemons.Store;
        var self  = Environment.ProcessId;

        await Assert.That(await LocalControlProbe.StartingAsync(store, "starting-pid", self, TimeProvider.System, TimeSpan.FromSeconds(5), cts.Token)).IsTrue();
        await Assert.That(await LocalControlProbe.StartingAsync(store, "starting-pid", self + 1, TimeProvider.System, TimeSpan.FromSeconds(5), cts.Token)).IsNull();
    }

    // ---- a reachable peer answering well-formed-but-structurally-degenerate JSON ----

    delegate Task ConnScript(NetworkStream s, CancellationToken ct);

    /// Minimal scripted-connection UDS listener — a trimmed copy of
    /// <c>LocalControlClientTests.ScriptedServer</c> (that one is a private nested type there,
    /// so it can't be referenced directly), sized for exactly what this file needs: one script
    /// per accepted connection, in order.
    sealed class ScriptedServer : IAsyncDisposable {
        readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        readonly CancellationTokenSource _cts = new();
        readonly ConnScript[] _scripts;
        int _served;
        readonly Task _accept;

        public ScriptedServer(string sockPath, params ConnScript[] scripts) {
            _scripts = scripts;
            _listener.Bind(new UnixDomainSocketEndPoint(sockPath));
            _listener.Listen(8);
            _accept = Task.Run(async () => {
                try {
                    while (!_cts.IsCancellationRequested) {
                        var conn = await _listener.AcceptAsync(_cts.Token);
                        var idx = Interlocked.Increment(ref _served) - 1;
                        if (idx >= _scripts.Length) { conn.Dispose(); continue; }
                        var script = _scripts[idx];
                        _ = Task.Run(async () => {
                            using var c = conn;
                            await using var s = new NetworkStream(c, ownsSocket: false);
                            try { await script(s, _cts.Token); } catch { /* scripted teardown */ }
                        }, _cts.Token);
                    }
                } catch { /* shutdown */ }
            });
        }

        public async ValueTask DisposeAsync() {
            _cts.Cancel();
            _listener.Dispose();
            try { await _accept; } catch { }
        }
    }

    [Test]
    public async Task Probe_treats_a_structurally_degenerate_snapshot_as_a_snapshot_failure_not_a_throw() {
        using var daemons = new TempDaemonStore();
        const string name = "probe-degenerate";
        var helloJson = JsonSerializer.Serialize(
            new HelloReplyDto(1, "1.0", name, ["status/1"], 111, "inst-x"),
            HelloIpcJsonContext.Default.HelloReplyDto);

        ConnScript helloThen = async (s, ct) => {
            var f = await FrameCodec.ReadAsync(s, ct);
            if (f?.Type == FrameType.Hello)
                await FrameCodec.WriteAsync(s, LocalFrame.HelloJson(FrameType.HelloReply, helloJson), ct);
        };
        // Well-formed JSON, but structurally degenerate: daemon/agents both null. STJ source-gen
        // leaves declared-non-nullable reference members at their default on null/absent JSON
        // rather than throwing, so this deserializes to a NON-null DaemonStatusDto with a null
        // Daemon — exactly the shape that must go through DaemonStatusValidator.IsValid instead
        // of a bare null-check, or ProbeAsync either NREs on snapshot.Daemon.Pid or silently
        // returns a null-riddled Snapshot.
        ConnScript subscribeDegenerate = async (s, ct) => {
            var f = await FrameCodec.ReadAsync(s, ct);
            if (f?.Type == FrameType.StatusSubscribe)
                await FrameCodec.WriteAsync(s, LocalFrame.StatusJson(FrameType.DaemonStatus, """{"daemon":null,"agents":null}"""), ct);
        };

        await using var server = new ScriptedServer(daemons.Store.SocketPath(name), helloThen, subscribeDegenerate);

        var r = await LocalControlProbe.ProbeAsync(daemons.Store, name, TimeProvider.System, TimeSpan.FromSeconds(5));

        await Assert.That(r.Reachable).IsTrue();
        await Assert.That(r.Hello).IsNotNull();
        await Assert.That(r.Snapshot).IsNull();
        await Assert.That(r.IdentityConsistent).IsFalse();
    }

    /// <summary>Fail-closed: a hello carrying no pid/instance_id (an older daemon that
    /// predates those fields, per <see cref="HelloReplyDto"/>'s own doc comment) must never default
    /// to "consistent" just because one side has nothing to disagree with — both sides must
    /// carry BOTH ids and agree, or the two dials cannot be proven to have landed on the same
    /// process.</summary>
    [Test]
    public async Task Hello_without_ids_is_never_consistent_even_with_a_valid_snapshot() {
        using var daemons = new TempDaemonStore();
        const string name = "probe-noids";
        var helloJson = JsonSerializer.Serialize(
            new HelloReplyDto(1, "0.1.0", name, ["status/1"], Pid: null, InstanceId: null),
            HelloIpcJsonContext.Default.HelloReplyDto);
        var snapshotJson = JsonSerializer.Serialize(
            new DaemonStatusDto(
                new DaemonInfoDto("probe-noids", "1.0.0", "https://s", "connected", 5, 0, Pid: 999, InstanceId: "inst-real"),
                []),
            StatusIpcJsonContext.Default.DaemonStatusDto);

        ConnScript helloThen = async (s, ct) => {
            var f = await FrameCodec.ReadAsync(s, ct);
            if (f?.Type == FrameType.Hello)
                await FrameCodec.WriteAsync(s, LocalFrame.HelloJson(FrameType.HelloReply, helloJson), ct);
        };
        ConnScript subscribeValid = async (s, ct) => {
            var f = await FrameCodec.ReadAsync(s, ct);
            if (f?.Type == FrameType.StatusSubscribe)
                await FrameCodec.WriteAsync(s, LocalFrame.StatusJson(FrameType.DaemonStatus, snapshotJson), ct);
        };

        await using var server = new ScriptedServer(daemons.Store.SocketPath(name), helloThen, subscribeValid);

        var r = await LocalControlProbe.ProbeAsync(daemons.Store, name, TimeProvider.System, TimeSpan.FromSeconds(5));

        await Assert.That(r.Reachable).IsTrue();
        await Assert.That(r.Hello).IsNotNull();
        await Assert.That(r.Snapshot).IsNotNull(); // the snapshot itself is perfectly valid...
        await Assert.That(r.IdentityConsistent).IsFalse(); // ...but consistency still fails closed
    }
}
