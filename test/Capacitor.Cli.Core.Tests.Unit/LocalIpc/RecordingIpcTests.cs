using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

/// <summary>A hook arranges a recorder whatever happens here, so every way of getting no answer reads as no.</summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class RecordingIpcTests {
    const string DaemonName = "daemon-a";

    static readonly RecordSourceDto Source = new("agent-7", "session-1", "sub-1", "/t.jsonl", "claude", "/repo", true);

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    Task<bool> Record(CancellationToken ct = default) => RecordingIpc.RecordAsync(Daemons.Store, DaemonName, Source, ct);

    Task<bool> Finish(CancellationToken ct = default) => RecordingIpc.FinishAsync(Daemons.Store, DaemonName, "session-1", ct);

    FakeRecordingDaemon Daemon(params string[] capabilities) => new(Daemons.Store.SocketPath(DaemonName), capabilities);

    [Test]
    public async Task No_daemon_listening_answers_no() {
        await Assert.That(await Record()).IsFalse();
        await Assert.That(await Finish()).IsFalse();
    }

    /// <summary>An older daemon can't decode the frame at all, so it is never sent one.</summary>
    [Test]
    public async Task A_daemon_without_the_capability_is_never_sent_the_frame() {
        await using var daemon = Daemon("status/1");
        daemon.Answer = RecordingIpc.Recording;

        await Assert.That(await Record()).IsFalse();
        await Assert.That(daemon.ReceivedTypes).IsEquivalentTo([FrameType.Hello]);
    }

    [Test]
    public async Task A_daemon_that_records_takes_the_source_whole() {
        await using var daemon = Daemon(RecordingIpc.Capability);
        daemon.Answer = RecordingIpc.Recording;

        await Assert.That(await Record()).IsTrue();

        var sent = JsonSerializer.Deserialize(daemon.Received[^1].Text, RecordingIpcJsonContext.Default.RecordSourceDto);
        await Assert.That(sent).IsEqualTo(Source);
    }

    [Test]
    public async Task A_refusal_answers_no() {
        await using var daemon = Daemon(RecordingIpc.Capability);
        daemon.Answer = RecordingIpc.Refused;

        await Assert.That(await Record()).IsFalse();
    }

    [Test]
    public async Task A_finished_source_says_so() {
        await using var daemon = Daemon(RecordingIpc.Capability);
        daemon.Answer = RecordingIpc.Finished;

        await Assert.That(await Finish()).IsTrue();
        await Assert.That(daemon.Received[^1].Text).IsEqualTo("session-1");
    }

    [Test]
    public async Task A_source_the_daemon_does_not_hold_is_not_finished() {
        await using var daemon = Daemon(RecordingIpc.Capability);
        daemon.Answer = RecordingIpc.NotHeld;

        await Assert.That(await Finish()).IsFalse();
    }

    /// <summary>A hook asks only of a daemon with a claim. One that has since stopped taking new sources still holds that
    /// one, and only it can finish it.</summary>
    [Test]
    public async Task A_finish_needs_no_capability() {
        await using var daemon = Daemon("status/1");
        daemon.Answer = RecordingIpc.Finished;

        await Assert.That(await Finish()).IsTrue();
        await Assert.That(daemon.ReceivedTypes).IsEquivalentTo([FrameType.FinishSource]);
    }

    /// <summary>The caller's budget ran out: the asking is done, and a hook has to move on.</summary>
    [Test]
    public async Task A_wait_cut_short_answers_no() {
        await using var daemon = Daemon(RecordingIpc.Capability);
        using var cts = new CancellationTokenSource();

        var finish = Finish(cts.Token);
        await daemon.Asked.WithinDeadlineAsync("the frame to reach the daemon");
        await cts.CancelAsync();

        await Assert.That(await finish).IsFalse();
    }
}
