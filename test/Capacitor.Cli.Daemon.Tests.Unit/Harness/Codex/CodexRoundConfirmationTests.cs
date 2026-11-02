using Capacitor.Cli.Daemon.Harness.Codex;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

public class CodexRoundConfirmationTests {
    static readonly TimeSpan Window = TimeSpan.FromSeconds(30);
    static readonly TimeSpan Poll   = TimeSpan.FromSeconds(2);

    /// <summary>Advances the fake clock until the confirmation settles. Each step waits briefly in real
    /// time, so a continuation that resumed on the pool has registered its next delay before the clock
    /// moves on.</summary>
    static async Task<CodexRoundConfirmation.Result> Settle(FakeTimeProvider time, Task<CodexRoundConfirmation.Result> task) {
        var deadline = DateTime.UtcNow + BoundedWait.Deadline;
        while (!task.IsCompleted) {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Stalled waiting for the confirmation to settle: not done within {BoundedWait.DeadlineSeconds}s.");

            time.Advance(Poll);
            await Task.WhenAny(task, Task.Delay(5));
        }

        return await task;
    }

    static Task<CodexRoundConfirmation.Result> Confirm(
            FakeTimeProvider time, Func<long> length, Func<Task<bool>> resubmit, Func<ulong>? attestations = null,
            ulong attestationBaseline = 0, Func<bool>? isCurrent = null) =>
        CodexRoundConfirmation.ConfirmAsync(
            length, baseline: 10, attestations ?? (() => attestationBaseline), attestationBaseline, resubmit,
            Window, Poll, time, CancellationToken.None, isCurrent);

    [Test]
    public async Task A_turn_in_the_first_window_needs_no_resubmit() {
        var resubmits = 0;

        var result = await Confirm(new FakeTimeProvider(), () => 42, () => { resubmits++; return Task.FromResult(true); });

        await Assert.That(result.Verdict).IsEqualTo(CodexRoundConfirmation.Verdict.Started);
        await Assert.That(resubmits).IsEqualTo(0);
    }

    /// <summary>A prompt-submit hook relayed after delivery proves the turn even before the rollout grows.</summary>
    [Test]
    public async Task A_relayed_turn_counts_without_rollout_growth() {
        var resubmits = 0;

        var result = await Confirm(
            new FakeTimeProvider(), () => 10, () => { resubmits++; return Task.FromResult(true); },
            attestations: () => 4, attestationBaseline: 3);

        await Assert.That(result.Verdict).IsEqualTo(CodexRoundConfirmation.Verdict.Started);
        await Assert.That(resubmits).IsEqualTo(0);
    }

    /// <summary>An attestation an earlier turn left standing says nothing about this round: the Enter is
    /// still resent, and with no growth and no newer attestation the round did not start.</summary>
    [Test]
    public async Task An_attestation_from_before_delivery_does_not_count() {
        var time      = new FakeTimeProvider();
        var resubmits = 0;

        var result = await Settle(time, Confirm(
            time, () => 10, () => { resubmits++; return Task.FromResult(true); },
            attestations: () => 3, attestationBaseline: 3));

        await Assert.That(result).IsEqualTo(new CodexRoundConfirmation.Result(CodexRoundConfirmation.Verdict.NotStarted, true));
        await Assert.That(resubmits).IsEqualTo(1);
    }

    [Test]
    public async Task An_attestation_after_the_resubmit_is_credited_to_it() {
        var   time         = new FakeTimeProvider();
        ulong attestations = 3;

        var result = await Settle(time, Confirm(time, () => 10, () => {
            attestations++;
            return Task.FromResult(true);
        }, attestations: () => attestations, attestationBaseline: 3));

        await Assert.That(result).IsEqualTo(new CodexRoundConfirmation.Result(CodexRoundConfirmation.Verdict.StartedAfterResubmit, true));
    }

    [Test]
    public async Task Growth_after_the_resubmit_is_credited_to_it() {
        var  time      = new FakeTimeProvider();
        long length    = 10;
        var  resubmits = 0;

        var result = await Settle(time, Confirm(time, () => length, () => {
            resubmits++;
            length = 42;
            return Task.FromResult(true);
        }));

        await Assert.That(result).IsEqualTo(new CodexRoundConfirmation.Result(CodexRoundConfirmation.Verdict.StartedAfterResubmit, true));
        await Assert.That(resubmits).IsEqualTo(1);
    }

    [Test]
    public async Task No_turn_after_one_resubmit_is_not_started() {
        var time      = new FakeTimeProvider();
        var resubmits = 0;

        var result = await Settle(time, Confirm(time, () => 10, () => { resubmits++; return Task.FromResult(true); }));

        await Assert.That(result).IsEqualTo(new CodexRoundConfirmation.Result(CodexRoundConfirmation.Verdict.NotStarted, true));
        await Assert.That(resubmits).IsEqualTo(1);
    }

    /// <summary>A withheld Enter (a dialog on screen, a stop) still ends in a verdict, and says it was withheld.</summary>
    [Test]
    public async Task A_withheld_resubmit_still_ends_not_started() {
        var time = new FakeTimeProvider();

        var result = await Settle(time, Confirm(time, () => 10, () => Task.FromResult(false)));

        await Assert.That(result).IsEqualTo(new CodexRoundConfirmation.Result(CodexRoundConfirmation.Verdict.NotStarted, false));
    }

    /// <summary>An Enter parked in a write the terminal never drains cannot hold the verdict back, and
    /// does not count as written.</summary>
    [Test]
    public async Task A_resubmit_that_never_returns_still_ends_not_started() {
        var time  = new FakeTimeProvider();
        var stuck = new TaskCompletionSource<bool>();

        var result = await Settle(time, Confirm(time, () => 10, () => stuck.Task));

        await Assert.That(result).IsEqualTo(new CodexRoundConfirmation.Result(CodexRoundConfirmation.Verdict.NotStarted, false));
    }

    /// <summary>A turn the Enter started while its write was still pending is credited to the resubmit.</summary>
    [Test]
    public async Task Growth_while_the_resubmit_is_pending_is_credited_to_it() {
        var  time    = new FakeTimeProvider();
        long length  = 10;
        var  pending = new TaskCompletionSource<bool>();

        var result = await Settle(time, Confirm(time, () => length, () => {
            length = 42;
            return pending.Task;
        }));

        await Assert.That(result).IsEqualTo(new CodexRoundConfirmation.Result(CodexRoundConfirmation.Verdict.StartedAfterResubmit, false));
    }

    /// <summary>An unreadable rollout is a measurement gap: nothing is resubmitted and nothing fails.</summary>
    [Test]
    public async Task An_unreadable_rollout_is_unavailable_without_a_resubmit() {
        var time      = new FakeTimeProvider();
        var resubmits = 0;

        var result = await Settle(time, Confirm(time, () => -1, () => { resubmits++; return Task.FromResult(true); }));

        await Assert.That(result.Verdict).IsEqualTo(CodexRoundConfirmation.Verdict.Unavailable);
        await Assert.That(resubmits).IsEqualTo(0);
    }

    [Test]
    public async Task A_superseded_round_is_never_resubmitted() {
        var time      = new FakeTimeProvider();
        var resubmits = 0;

        var result = await Settle(time, Confirm(
            time, () => 10, () => { resubmits++; return Task.FromResult(true); }, isCurrent: () => false));

        await Assert.That(result.Verdict).IsEqualTo(CodexRoundConfirmation.Verdict.Superseded);
        await Assert.That(resubmits).IsEqualTo(0);
    }
}
