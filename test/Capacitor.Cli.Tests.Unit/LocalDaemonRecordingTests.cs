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

        await Assert.That(await Client().TryRecordAsync(Source, skipTitle: true, CancellationToken.None)).IsTrue();

        var sent = JsonSerializer.Deserialize(daemon.Received[^1].Text, RecordingIpcJsonContext.Default.RecordSourceDto);
        await Assert.That(sent).IsEqualTo(new RecordSourceDto("agent-7", "session-1", "sub-1", "/t.jsonl", "claude", "/repo", true));
    }

    /// <summary>A terminal session is left to its watcher, whatever a daemon on this machine advertises.</summary>
    [Test]
    public async Task A_terminal_hook_asks_no_daemon() {
        await using var daemon = Daemon();

        await Assert.That(await Client(HostedAgent.Terminal).TryRecordAsync(Source, false, CancellationToken.None)).IsFalse();
        await Assert.That(daemon.Received).IsEmpty();
    }

    [Test]
    public async Task Only_hosted_Claude_goes_to_the_daemon() {
        await using var daemon = Daemon();

        await Assert.That(await Client().TryRecordAsync(Source with { Vendor = "codex" }, false, CancellationToken.None)).IsFalse();
        await Assert.That(daemon.Received).IsEmpty();
    }

    [Test]
    public async Task A_hook_that_names_no_daemon_asks_none() {
        await using var daemon = Daemon();

        await Assert.That(await Client(daemonName: null).TryRecordAsync(Source, false, CancellationToken.None)).IsFalse();
        await Assert.That(await Client(daemonName: null).FinishSourceAsync("session-1", CancellationToken.None)).IsFalse();
        await Assert.That(daemon.Received).IsEmpty();
    }

    [Test]
    public async Task A_finish_asks_the_hooks_own_daemon() {
        await using var daemon = Daemon();
        daemon.Answer = RecordingIpc.Finished;

        await Assert.That(await Client().FinishSourceAsync("session-1", CancellationToken.None)).IsTrue();
    }
}
