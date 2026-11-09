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

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    LocalDaemonRecording Client(HostedAgent? hosted = null, string? daemonName = DaemonName) => new(Daemons.Store, hosted ?? Hosted, daemonName);

    FakeRecordingDaemon Daemon() => new(Daemons.Store.SocketPath(DaemonName), RecordingIpc.Capability) { Answer = RecordingIpc.Recording };

    [Test]
    public async Task A_hosted_hook_names_its_agent_and_the_source() {
        await using var daemon = Daemon();

        await Assert.That(await Client().TryRecordAsync(Source, skipTitle: true, CancellationToken.None)).IsEqualTo(RecordingHandOver.Taken);

        var sent = JsonSerializer.Deserialize(daemon.Received[^1].Text, RecordingIpcJsonContext.Default.RecordSourceDto);
        await Assert.That(sent).IsEqualTo(new RecordSourceDto("agent-7", "session-1", "sub-1", "/t.jsonl", "claude", "/repo", true));
    }

    /// <summary>A terminal session is left to its watcher, whatever a daemon on this machine advertises.</summary>
    [Test]
    public async Task A_terminal_hook_asks_no_daemon() {
        await using var daemon = Daemon();

        await Assert.That(await Client(HostedAgent.Terminal).TryRecordAsync(Source, false, CancellationToken.None)).IsEqualTo(RecordingHandOver.Unanswered);
        await Assert.That(daemon.Received).IsEmpty();
    }

    [Test]
    public async Task Only_hosted_Claude_goes_to_the_daemon() {
        await using var daemon = Daemon();

        await Assert.That(await Client().TryRecordAsync(Source with { Vendor = "codex" }, false, CancellationToken.None)).IsEqualTo(RecordingHandOver.Unanswered);
        await Assert.That(daemon.Received).IsEmpty();
    }

    [Test]
    public async Task A_hook_that_names_no_daemon_asks_none() {
        await using var daemon = Daemon();

        await Assert.That(await Client(daemonName: null).TryRecordAsync(Source, false, CancellationToken.None)).IsEqualTo(RecordingHandOver.Unanswered);
        await Assert.That(await Client(daemonName: null).FinishSourceAsync("session-1", CancellationToken.None)).IsFalse();
        await Assert.That(daemon.Received).IsEmpty();
    }

    [Test]
    public async Task A_finish_asks_the_hooks_own_daemon() {
        await using var daemon = Daemon();
        daemon.Answer = RecordingIpc.Finished;

        await Assert.That(await Client().FinishSourceAsync("session-1", CancellationToken.None)).IsTrue();
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
