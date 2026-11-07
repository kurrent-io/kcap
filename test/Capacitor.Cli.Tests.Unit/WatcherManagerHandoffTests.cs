using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>A terminal session's hook asks the daemon its own profile names to record the session, over a real socket.
/// Any answer but a take, and no answer in the hook's budget, leaves the session to a watcher as it always was. A daemon
/// that took it is left alone through a restart inside the grace; past it the session moves to a watcher for good.</summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class WatcherManagerHandoffTests {
    const string Session   = "0123456789abcdef0123456789abcdef";
    const string OwnDaemon = "daemon-a";
    const string Url       = "http://127.0.0.1:1";

    static readonly ProcessIncarnation Claude = new(4242, "test:claude");

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));

    WatcherPaths Paths => field ??= new(Config.CreateDir("watchers"));

    RecorderClaims Claims => field ??= new(Paths.Directory, _time);

    FakeWatcherSpawner Spawner => field ??= new();

    string Transcript => field ??= Config.CreateFile("t.jsonl", "{}\n");

    /// <summary>A hook on <paramref name="profile"/>, whose daemon name that profile supplies.</summary>
    WatcherManager Hook(string daemonName = OwnDaemon, string profile = "default") => new(
        Config.Root, Resolutions.At(Url, Config.Root), new FixedCapacitorHttpClient(), SystemProcessStarter.Instance, Paths, Spawner, _time,
        new LocalDaemonRecording(Daemons.Store, HostedAgent.Terminal, daemonName: null,
            new TerminalHandOff(daemonName, profile, Url, Config.Root.Directory, Paths.Directory, _ => "/home/claude", Follow)),
        HostedAgent.Terminal);

    static ProcessIncarnation Follow(int? follows) => follows is { } pid ? new ProcessIncarnation(pid, "test:followed") : Claude;

    FakeRecordingDaemon Daemon(string name, params string[] capabilities) =>
        new(Daemons.Store.SocketPath(name), capabilities) { Answer = RecordingIpc.Recording };

    Task Ensure(WatcherManager hook) => hook.EnsureWatcherRunning(Session, Transcript, agentId: null, cwd: "/repo");

    [Test]
    public async Task A_hook_hands_a_terminal_session_to_its_daemon() {
        await using var daemon = Daemon(OwnDaemon, RecordingIpc.TerminalCapability);

        await Ensure(Hook());

        await Assert.That(Spawner.Keys).IsEmpty();
        await Assert.That(daemon.ReceivedTypes).IsEquivalentTo([FrameType.Hello, FrameType.RecordTerminalSource]);

        var sent = JsonSerializer.Deserialize(daemon.Received[^1].Text, RecordingIpcJsonContext.Default.RecordTerminalSourceDto)!;
        await Assert.That((sent.SessionId, sent.Profile, sent.ServerUrl, sent.ConfigDir, sent.WatcherDir, sent.ProcessId, sent.ProcessToken))
            .IsEqualTo((Session, "default", Url, Config.Root.Directory, Paths.Directory, Claude.Pid, Claude.StartToken));
    }

    /// <summary>A successor armed for a session resumed in another process follows that process, which is the one whose
    /// exit ends the session there.</summary>
    [Test]
    public async Task A_successor_follows_the_process_it_names() {
        await using var daemon = Daemon(OwnDaemon, RecordingIpc.TerminalCapability);

        await Hook().EnsureWatcherRunning(Session, Transcript, agentId: null, skipTitle: true, parentPid: 777);

        var sent = JsonSerializer.Deserialize(daemon.Received[^1].Text, RecordingIpcJsonContext.Default.RecordTerminalSourceDto)!;
        await Assert.That((sent.ProcessId, sent.ProcessToken, sent.SkipTitle)).IsEqualTo((777, "test:followed", true));
    }

    /// <summary>Profile B's hook asks the daemon profile B names, which isn't running, and A's daemon hears nothing. A
    /// daemon both profiles name refuses on the profile the frame carries, which the daemon's own tests pin.</summary>
    [Test]
    public async Task A_hook_on_another_profile_spawns_a_watcher() {
        await using var daemonA = Daemon(OwnDaemon, RecordingIpc.TerminalCapability);

        await Ensure(Hook(daemonName: "daemon-b", profile: "b"));

        await Assert.That(Spawner.Keys).IsEquivalentTo([Session]);
        await Assert.That(daemonA.Received).IsEmpty();
        await Assert.That(Claims.Read(Session)).IsEqualTo(RecorderClaimState.None);
    }

    [Test]
    public async Task No_daemon_spawns_a_watcher_as_today() {
        await Ensure(Hook());

        await Assert.That(Spawner.Keys).IsEquivalentTo([Session]);
    }

    /// <summary>One from before terminal sessions, one recording hosted sessions only, or one with recording off: each
    /// hears a hello and nothing it couldn't decode.</summary>
    [Test]
    [Arguments("status/1")]
    [Arguments(RecordingIpc.Capability)]
    public async Task An_older_daemon_spawns_a_watcher(string capability) {
        await using var daemon = Daemon(OwnDaemon, capability);

        await Ensure(Hook());

        await Assert.That(Spawner.Keys).IsEquivalentTo([Session]);
        await Assert.That(daemon.ReceivedTypes).IsEquivalentTo([FrameType.Hello]);
    }

    [Test]
    public async Task A_daemon_that_declines_gets_a_watcher_beside_it() {
        await using var daemon = Daemon(OwnDaemon, RecordingIpc.TerminalCapability);
        daemon.Answer = RecordingIpc.Refused;

        await Ensure(Hook());

        await Assert.That(Spawner.Keys).IsEquivalentTo([Session]);
    }

    /// <summary>The daemon was killed with the session running and is on its way back: its claim is fresh with nobody on
    /// the socket, and the hook neither asks nor spawns.</summary>
    [Test]
    public async Task A_daemon_restart_within_grace_spawns_no_watcher() {
        Claims.TryTake(Session, OwnDaemon, isHosted: false);
        _time.Advance(RecorderClaims.Grace - TimeSpan.FromSeconds(1));

        await Ensure(Hook());

        await Assert.That(Spawner.Keys).IsEmpty();
        await Assert.That(Claims.Read(Session)).IsEqualTo(RecorderClaimState.DaemonFresh);
    }

    /// <summary>Every hook of a session the daemon holds reads one claim and stops there, without the spawn lock the
    /// daemon takes to claim a source.</summary>
    [Test]
    public async Task A_fresh_claim_takes_no_spawn_lock() {
        Claims.TryTake(Session, OwnDaemon, isHosted: false);

        await Ensure(Hook());

        await Assert.That(File.Exists(Paths.SpawnLockFile(Session))).IsFalse();
    }

    /// <summary>Past the grace the hook takes the claim and starts a watcher, and from then the watcher holds the session:
    /// later hooks find it alive and never ask the daemon that came back, and its end stops the watcher with no daemon
    /// asked to finish anything.</summary>
    [Test]
    public async Task A_daemon_gone_past_grace_spawns_a_watcher_and_keeps_it_until_the_session_ends() {
        using var watcher = DummyProcess.StartSleep(60);
        var spawner = new FakeWatcherSpawner(request => {
            File.WriteAllText(Paths.PidFile(request.Key), $"{watcher.Pid}\n{PidIdentity.Capture(watcher.Pid)}");
            WatcherHeartbeat.Touch(Paths.StartedFile(request.Key), _time.GetUtcNow());
            WatcherHeartbeat.Touch(Paths.HeartbeatFile(request.Key), _time.GetUtcNow());

            return Task.CompletedTask;
        });
        var hook = new WatcherManager(
            Config.Root, Resolutions.At(Url, Config.Root), new FixedCapacitorHttpClient(), SystemProcessStarter.Instance, Paths, spawner, _time,
            new LocalDaemonRecording(Daemons.Store, HostedAgent.Terminal, daemonName: null,
                new TerminalHandOff(OwnDaemon, "default", Url, Config.Root.Directory, Paths.Directory, _ => "/home/claude", _ => Claude)),
            HostedAgent.Terminal);

        Claims.TryTake(Session, OwnDaemon, isHosted: false);
        _time.Advance(RecorderClaims.Grace + TimeSpan.FromSeconds(1));

        await Ensure(hook);

        await Assert.That(spawner.Keys).IsEquivalentTo([Session]);
        await Assert.That(Claims.Read(Session)).IsEqualTo(RecorderClaimState.None);

        await using var back = Daemon(OwnDaemon, RecordingIpc.TerminalCapability);
        await Ensure(hook);
        await Ensure(hook);

        await Assert.That(spawner.Keys).IsEquivalentTo([Session]);
        await Assert.That(back.Received).IsEmpty();

        await Assert.That(await hook.KillWatcher(Session, TextWriter.Null)).IsTrue();
        await Assert.That(watcher.WaitForExit(TimeSpan.FromSeconds(10))).IsTrue();
        await Assert.That(back.Received).IsEmpty();
    }

    /// <summary>A daemon that takes the connection and never answers costs the hook its hand-off budget and no more, and
    /// the hook then spawns behind the spawn lock as it always did.</summary>
    [Test]
    public async Task A_failed_handoff_never_blocks_the_hook_past_its_budget() {
        await using var daemon = Daemon(OwnDaemon, RecordingIpc.TerminalCapability);
        daemon.Answer = null;

        var hook = Ensure(Hook());
        await daemon.Asked.WithinDeadlineAsync("the hook to ask the daemon");
        _time.Advance(WatcherManager.HandOffBudget);
        await hook.WithinDeadlineAsync("the hook's way past the daemon");

        await Assert.That(Spawner.Keys).IsEquivalentTo([Session]);
    }

    /// <summary>A silent daemon on the hello itself: the hook has nothing to go on and moves on within the same budget.</summary>
    [Test]
    public async Task A_daemon_silent_on_its_hello_never_blocks_the_hook_past_its_budget() {
        using var silent = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        silent.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(Daemons.Store.SocketPath(OwnDaemon)));
        silent.Listen(4);
        var accepted = silent.AcceptAsync();

        var hook = Ensure(Hook());
        using var connection = await accepted.WithinDeadlineAsync("the hook to connect");
        _time.Advance(WatcherManager.HandOffBudget);
        await hook.WithinDeadlineAsync("the hook's way past the daemon");

        await Assert.That(Spawner.Keys).IsEquivalentTo([Session]);
    }
}
