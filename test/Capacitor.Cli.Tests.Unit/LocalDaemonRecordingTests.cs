using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Recording;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit;

[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class LocalDaemonRecordingTests {
    const string DaemonName = "daemon-a";

    static readonly RecordingSource Source = new("session-1", "sub-1", "/t.jsonl", "claude", "/repo");
    static readonly HostedAgent Hosted = new("agent-7", false, DaemonBridge.None);
    static readonly ProcessIncarnation Claude = new(4242, "test:claude");

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    int _reads;

    TerminalHandOff Terminal(ProcessIncarnation? process = null, string daemonName = DaemonName) => new(
        daemonName, "work", "https://kcap.example", "/config", "/config/watchers",
        transcript => {
            _reads++;

            return "/home/claude";
        },
        follows => {
            _reads++;

            return follows is { } pid ? new ProcessIncarnation(pid, "test:followed") : process;
        });

    LocalDaemonRecording Client(HostedAgent? hosted = null, string? daemonName = DaemonName, TerminalHandOff? terminal = null) =>
        new(Daemons.Store, hosted ?? Hosted, daemonName, terminal);

    /// <summary>A daemon on its socket with its recording marker written, as one with recording on leaves it.</summary>
    FakeRecordingDaemon Daemon(params string[] capabilities) {
        DaemonRecordingMarker.Sync(Daemons.Store, DaemonName, recording: true);

        return new(Daemons.Store.SocketPath(DaemonName), capabilities.Length > 0 ? capabilities : [RecordingIpc.Capability]) { Answer = RecordingIpc.Recording };
    }

    [Test]
    public async Task A_hosted_hook_names_its_agent_and_the_source() {
        await using var daemon = Daemon();

        await Assert.That(await Client().TryRecordAsync(Source, skipTitle: true, follows: null, CancellationToken.None)).IsEqualTo(RecordingHandOver.Taken);

        var sent = JsonSerializer.Deserialize(daemon.Received[^1].Text, RecordingIpcJsonContext.Default.RecordSourceDto);
        await Assert.That(sent).IsEqualTo(new RecordSourceDto("agent-7", "session-1", "sub-1", "/t.jsonl", "claude", "/repo", true));
    }

    /// <summary>The daemon holds every one of these against its own, since a terminal session has no agent to tie it to.</summary>
    [Test]
    public async Task A_terminal_hook_names_what_it_resolved_to_the_daemon_of_its_profile() {
        await using var daemon = Daemon(RecordingIpc.TerminalCapability);

        var handOver = await Client(HostedAgent.Terminal, daemonName: null, Terminal(Claude)).TryRecordAsync(Source, skipTitle: true, follows: null, CancellationToken.None);

        await Assert.That(handOver).IsEqualTo(RecordingHandOver.Taken);
        await Assert.That(daemon.ReceivedTypes).IsEquivalentTo([FrameType.Hello, FrameType.RecordTerminalSource]);

        var sent = JsonSerializer.Deserialize(daemon.Received[^1].Text, RecordingIpcJsonContext.Default.RecordTerminalSourceDto);
        await Assert.That(sent).IsEqualTo(new RecordTerminalSourceDto(
            "session-1", "sub-1", "/t.jsonl", "claude", "/repo", true, "work", "https://kcap.example", "/config", "/config/watchers", "/home/claude", 4242, "test:claude"));
    }

    /// <summary>A successor armed for a resumed session follows the process the session moved to, not the hook's own.</summary>
    [Test]
    public async Task A_terminal_source_follows_the_process_its_caller_names() {
        await using var daemon = Daemon(RecordingIpc.TerminalCapability);

        await Client(HostedAgent.Terminal, daemonName: null, Terminal(Claude)).TryRecordAsync(Source, false, follows: 777, CancellationToken.None);

        var sent = JsonSerializer.Deserialize(daemon.Received[^1].Text, RecordingIpcJsonContext.Default.RecordTerminalSourceDto)!;
        await Assert.That((sent.ProcessId, sent.ProcessToken)).IsEqualTo((777, "test:followed"));
    }

    /// <summary>With recording off, or a daemon from before terminal sessions, a hook spends its hello and nothing else:
    /// no process walk and no account registry.</summary>
    [Test]
    [Arguments("status/1")]
    [Arguments(RecordingIpc.Capability)]
    public async Task A_daemon_that_takes_no_terminal_session_costs_the_hook_its_hello(string capability) {
        await using var daemon = Daemon(capability);

        var handOver = await Client(HostedAgent.Terminal, daemonName: null, Terminal(Claude)).TryRecordAsync(Source, false, null, CancellationToken.None);

        await Assert.That(handOver).IsEqualTo(RecordingHandOver.Declined);
        await Assert.That(daemon.ReceivedTypes).IsEquivalentTo([FrameType.Hello]);
        await Assert.That(_reads).IsEqualTo(0);
    }

    /// <summary>No marker is a daemon with recording off, which a terminal hook never opens a socket to.</summary>
    [Test]
    public async Task A_terminal_hook_asks_no_daemon_that_left_no_recording_marker() {
        await using var daemon = Daemon(RecordingIpc.TerminalCapability);
        DaemonRecordingMarker.Delete(Daemons.Store, DaemonName);

        var handOver = await Client(HostedAgent.Terminal, daemonName: null, Terminal(Claude)).TryRecordAsync(Source, false, null, CancellationToken.None);

        await Assert.That(handOver).IsEqualTo(RecordingHandOver.Declined);
        await Assert.That(daemon.Received).IsEmpty();
        await Assert.That(_reads).IsEqualTo(0);
    }

    /// <summary>Nothing would end a source with no process behind it, so the hook keeps its watcher, whose parent monitor
    /// has the same blind spot it always had.</summary>
    [Test]
    public async Task A_terminal_hook_with_no_process_to_follow_hands_nothing_over() {
        await using var daemon = Daemon(RecordingIpc.TerminalCapability);

        var handOver = await Client(HostedAgent.Terminal, daemonName: null, Terminal(process: null)).TryRecordAsync(Source, false, null, CancellationToken.None);

        await Assert.That(handOver).IsEqualTo(RecordingHandOver.Declined);
        await Assert.That(daemon.ReceivedTypes).IsEquivalentTo([FrameType.Hello]);
    }

    [Test]
    public async Task A_terminal_hook_asks_only_the_daemon_its_profile_names() {
        await using var daemon = Daemon(RecordingIpc.TerminalCapability);

        var handOver = await Client(HostedAgent.Terminal, daemonName: null, Terminal(Claude, daemonName: "daemon-b")).TryRecordAsync(Source, false, null, CancellationToken.None);

        await Assert.That(handOver).IsNotEqualTo(RecordingHandOver.Taken);
        await Assert.That(daemon.Received).IsEmpty();
    }

    /// <summary>A hosted run that names no agent isn't a terminal session.</summary>
    [Test]
    public async Task A_hosted_run_with_no_agent_id_asks_no_daemon() {
        await using var daemon = Daemon(RecordingIpc.TerminalCapability);
        var unnamed = HostedAgent.Terminal with { Kind = HostedSessionKind.HostedAgent };

        await Assert.That(await Client(unnamed, daemonName: null, Terminal(Claude)).TryRecordAsync(Source, false, null, CancellationToken.None)).IsEqualTo(RecordingHandOver.Unanswered);
        await Assert.That(daemon.Received).IsEmpty();
    }

    [Test]
    public async Task Only_Claude_goes_to_the_daemon() {
        await using var daemon = Daemon(RecordingIpc.Capability, RecordingIpc.TerminalCapability);

        await Assert.That(await Client().TryRecordAsync(Source with { Vendor = "codex" }, false, null, CancellationToken.None)).IsEqualTo(RecordingHandOver.Unanswered);
        await Assert.That(await Client(HostedAgent.Terminal, null, Terminal(Claude)).TryRecordAsync(Source with { Vendor = "codex" }, false, null, CancellationToken.None))
            .IsEqualTo(RecordingHandOver.Unanswered);
        await Assert.That(daemon.Received).IsEmpty();
    }

    [Test]
    public async Task A_hook_that_names_no_daemon_asks_none() {
        await using var daemon = Daemon();

        await Assert.That(await Client(daemonName: null).TryRecordAsync(Source, false, null, CancellationToken.None)).IsEqualTo(RecordingHandOver.Unanswered);
        await Assert.That(await Client(daemonName: null).FinishSourceAsync("session-1", holder: null, from: null, CancellationToken.None)).IsEqualTo(RecordingFinish.Unanswered);
        await Assert.That(daemon.Received).IsEmpty();
    }

    /// <summary>A claim says which daemon holds the source, whatever the hook's own environment names.</summary>
    [Test]
    public async Task A_finish_asks_the_daemon_the_claim_names() {
        await using var daemon = Daemon();
        daemon.Answer = RecordingIpc.Finished;

        await Assert.That(await Client(HostedAgent.Terminal, daemonName: null).FinishSourceAsync("session-1", DaemonName, from: null, CancellationToken.None)).IsEqualTo(RecordingFinish.Finished);
    }

    [Test]
    public async Task A_finish_with_no_holder_asks_the_hooks_own_daemon() {
        await using var daemon = Daemon();
        daemon.Answer = RecordingIpc.Finished;

        await Assert.That(await Client().FinishSourceAsync("session-1", holder: null, from: null, CancellationToken.None)).IsEqualTo(RecordingFinish.Finished);
        await Assert.That(await Client(HostedAgent.Terminal, null, Terminal(Claude)).FinishSourceAsync("session-1", holder: null, from: null, CancellationToken.None))
            .IsEqualTo(RecordingFinish.Finished);
    }

    /// <summary><c>kcap disable</c> runs in a terminal as often as not, so the claim says who to ask, not the environment.</summary>
    [Test]
    public async Task A_disable_asks_the_daemon_it_is_told_to_from_outside_an_agent() {
        await using var daemon = new FakeRecordingDaemon(Daemons.Store.SocketPath(DaemonName), RecordingIpc.DisableCapability) {
            Answer = JsonSerializer.Serialize(new DisableSessionAckDto(RecordingIpc.Stopped, false, null), RecordingIpcJsonContext.Default.DisableSessionAckDto),
        };

        var answer = await Client(HostedAgent.Terminal, daemonName: null).DisableSessionAsync(DaemonName, "session-1", CancellationToken.None);

        await Assert.That(answer.Outcome).IsEqualTo(RecordingDisable.Stopped);
    }

    Task<RecordingDisableAnswer> DisableAsync() =>
        Client(HostedAgent.Terminal, daemonName: null).DisableSessionAsync(DaemonName, "session-1", CancellationToken.None);

    /// <summary>A pid file naming a live daemon, with nothing on its socket, is that daemon starting up: it may restore
    /// the session once it binds, so the stop can't be called confirmed.</summary>
    [Test]
    public async Task A_live_daemon_with_nothing_on_its_socket_is_silent_to_a_disable() {
        Daemons.Store.EnsureDirectory();
        await File.WriteAllTextAsync(Daemons.Store.PidPath(DaemonName), $"{Environment.ProcessId}\n{PidIdentity.Capture(Environment.ProcessId)}");

        await Assert.That((await DisableAsync()).Outcome).IsEqualTo(RecordingDisable.Silent);
    }

    /// <summary>Between the old process and its successor there is no live pid, and the queued restart says one is coming.</summary>
    [Test]
    public async Task A_daemon_with_a_restart_queued_and_nothing_on_its_socket_is_silent_to_a_disable() {
        DaemonRestartMarker.Write(Daemons.Store, DaemonName, new DaemonRestartMarker("1.0.0", DaemonRestartMarker.SelfDetected, DateTimeOffset.UnixEpoch));

        await Assert.That((await DisableAsync()).Outcome).IsEqualTo(RecordingDisable.Silent);
    }

    /// <summary>No daemon at all is the common case, and it leaves nothing to doubt.</summary>
    [Test]
    public async Task No_daemon_at_all_is_no_socket_to_a_disable() =>
        await Assert.That((await DisableAsync()).Outcome).IsEqualTo(RecordingDisable.NoSocket);
}
