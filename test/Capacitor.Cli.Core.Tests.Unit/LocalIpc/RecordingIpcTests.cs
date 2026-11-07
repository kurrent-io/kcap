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

    Task<RecordingHandOver> Record(CancellationToken ct = default) => RecordingIpc.RecordAsync(Daemons.Store, DaemonName, Source, ct);

    Task<bool> Finish(CancellationToken ct = default) => RecordingIpc.FinishAsync(Daemons.Store, DaemonName, "session-1", ct);

    Task<RecordingDisable> Disable(CancellationToken ct = default) => RecordingIpc.DisableAsync(Daemons.Store, DaemonName, "session-1", ct);

    FakeRecordingDaemon Daemon(params string[] capabilities) => new(Daemons.Store.SocketPath(DaemonName), capabilities);

    /// <summary>Down is how a daemon restarts, so a hook can't read this as a daemon that won't come back for its claim.</summary>
    [Test]
    public async Task No_daemon_listening_is_no_answer() {
        await Assert.That(await Record()).IsEqualTo(RecordingHandOver.Unanswered);
        await Assert.That(await Finish()).IsFalse();
    }

    /// <summary>An older daemon can't decode the frame at all, so it is never sent one. Up without the capability is a
    /// daemon that won't record: nothing of it is coming for a claim.</summary>
    [Test]
    public async Task A_daemon_without_the_capability_declines_and_is_never_sent_the_frame() {
        await using var daemon = Daemon("status/1");
        daemon.Answer = RecordingIpc.Recording;

        await Assert.That(await Record()).IsEqualTo(RecordingHandOver.Declined);
        await Assert.That(daemon.ReceivedTypes).IsEquivalentTo([FrameType.Hello]);
    }

    [Test]
    public async Task A_daemon_that_records_takes_the_source_whole() {
        await using var daemon = Daemon(RecordingIpc.Capability);
        daemon.Answer = RecordingIpc.Recording;

        await Assert.That(await Record()).IsEqualTo(RecordingHandOver.Taken);

        var sent = JsonSerializer.Deserialize(daemon.Received[^1].Text, RecordingIpcJsonContext.Default.RecordSourceDto);
        await Assert.That(sent).IsEqualTo(Source);
    }

    /// <summary>Its server's answer went with a dropped connection, and its sources are still recording. A stale claim on
    /// one of them is what a sleep leaves, so a hook reading this as a decline would start a second recorder.</summary>
    [Test]
    public async Task A_daemon_between_answers_gives_no_answer() {
        await using var daemon = Daemon(RecordingIpc.Capability);
        daemon.Answer = RecordingIpc.NotYet;

        await Assert.That(await Record()).IsEqualTo(RecordingHandOver.Unanswered);
    }

    [Test]
    public async Task A_refusal_declines() {
        await using var daemon = Daemon(RecordingIpc.Capability);
        daemon.Answer = RecordingIpc.Refused;

        await Assert.That(await Record()).IsEqualTo(RecordingHandOver.Declined);
    }

    /// <summary>A decline lets a hook take the claim and start a recorder, and an answer this build can't read says
    /// nothing of the kind: the daemon that gave it may be recording.</summary>
    [Test]
    public async Task An_answer_it_does_not_know_is_no_answer() {
        await using var daemon = Daemon(RecordingIpc.Capability);
        daemon.Answer = "an_answer_from_a_newer_daemon";

        await Assert.That(await Record()).IsEqualTo(RecordingHandOver.Unanswered);
    }

    [Test]
    public async Task A_source_that_is_not_the_agents_is_disowned() {
        await using var daemon = Daemon(RecordingIpc.Capability);
        daemon.Answer = RecordingIpc.NotTheAgents;

        await Assert.That(await Record()).IsEqualTo(RecordingHandOver.Disowned);
    }

    /// <summary>The budget ran out with the daemon quiet, which says nothing about whether it would have taken it.</summary>
    [Test]
    public async Task A_hand_over_cut_short_is_no_answer() {
        await using var daemon = Daemon(RecordingIpc.Capability);
        using var cts = new CancellationTokenSource();

        var record = Record(cts.Token);
        await daemon.Asked.WithinDeadlineAsync("the frame to reach the daemon");
        await cts.CancelAsync();

        await Assert.That(await record).IsEqualTo(RecordingHandOver.Unanswered);
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

    [Test]
    [Arguments(RecordingIpc.Stopped, RecordingDisable.Stopped)]
    [Arguments(RecordingIpc.SendInFlight, RecordingDisable.SendInFlight)]
    [Arguments(RecordingIpc.NotHeld, RecordingDisable.NotHeld)]
    public async Task A_disable_reads_the_daemons_answer(string answer, RecordingDisable read) {
        await using var daemon = Daemon(RecordingIpc.DisableCapability);
        daemon.Answer = answer;

        await Assert.That(await Disable()).IsEqualTo(read);
        await Assert.That(daemon.Received[^1].Text).IsEqualTo("session-1");
    }

    /// <summary>The command tells a person what this says, so an answer it can't read says nothing.</summary>
    [Test]
    public async Task A_disable_answer_it_does_not_know_is_no_answer() {
        await using var daemon = Daemon(RecordingIpc.DisableCapability);
        daemon.Answer = "an_answer_from_a_newer_daemon";

        await Assert.That(await Disable()).IsEqualTo(RecordingDisable.Unanswered);
    }

    [Test]
    public async Task A_daemon_without_the_disable_capability_is_never_sent_the_frame() {
        await using var daemon = Daemon(RecordingIpc.Capability);
        daemon.Answer = RecordingIpc.Stopped;

        await Assert.That(await Disable()).IsEqualTo(RecordingDisable.Unanswered);
        await Assert.That(daemon.ReceivedTypes).IsEquivalentTo([FrameType.Hello]);
    }

    [Test]
    public async Task No_daemon_listening_is_no_answer_to_a_disable() =>
        await Assert.That(await Disable()).IsEqualTo(RecordingDisable.Unanswered);

    [Test]
    public async Task A_disable_cut_short_is_no_answer() {
        await using var daemon = Daemon(RecordingIpc.DisableCapability);
        using var cts = new CancellationTokenSource();

        var disable = Disable(cts.Token);
        await daemon.Asked.WithinDeadlineAsync("the frame to reach the daemon");
        await cts.CancelAsync();

        await Assert.That(await disable).IsEqualTo(RecordingDisable.Unanswered);
    }
}
