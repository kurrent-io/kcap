using Capacitor.App.Services;
using Capacitor.App.Services.Update;
using Capacitor.App.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// <summary>The restart hand-off on Windows, where the updater would otherwise end the daemon mid-turn.</summary>
public class UpdateCoordinatorDaemonStopTests {
    static UpdateCandidate Beta3 => new("0.12.0-beta.3", IsPrerelease: true);

    sealed class FakeDaemonStop : IUpdateDaemonStop {
        public readonly TaskCompletionSource<DaemonHandoff> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan? Holds;
        public int Calls;
        public int Releases;
        public CancellationToken Token;

        public async Task<DaemonHandoff> StopAsync(Action<TimeSpan> waiting, CancellationToken ct) {
            Token = ct;
            Interlocked.Increment(ref Calls);
            if (Holds is { } wait) waiting(wait);
            return await Result.Task.WaitAsync(ct);
        }

        public void Release() => Interlocked.Increment(ref Releases);
    }

    sealed class Harness {
        public readonly FakeAppUpdater Updater = new() { NextCheck = Beta3 };
        public readonly FakeLifecycleSurface Surface = new();
        public readonly FakeDaemonStop Stop = new();
        public int Quits;
        public readonly UpdateCoordinator Coordinator;

        /// <param name="waitingAnswer">The answer to the waiting prompt; null leaves it open until it is closed.</param>
        public Harness(bool? waitingAnswer = null, bool withStop = true) {
            Surface.ConfirmBehavior = (prompt, ct) => prompt.Kind != LifecyclePrompt.KindUpdateWaiting ? Task.FromResult(true)
                : waitingAnswer is { } answer ? Task.FromResult(answer)
                : Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => false, TaskScheduler.Default);
            Coordinator = new UpdateCoordinator(Updater, Surface, new FakeTimeProvider(), () => Interlocked.Increment(ref Quits),
                CancellationToken.None, daemonStop: withStop ? Stop : null);
        }

        public LifecyclePrompt? WaitingPrompt => Surface.Prompts.ToArray().FirstOrDefault(p => p.Kind == LifecyclePrompt.KindUpdateWaiting);
    }

    static async Task WaitUntilAsync(Func<bool> condition, string what) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task The_updater_takes_over_only_once_the_daemon_has_stopped() {
        var h = new Harness();

        await h.Coordinator.RunMenuActionAsync();
        await WaitUntilAsync(() => Volatile.Read(ref h.Stop.Calls) == 1, "the daemon stop");
        await Task.Delay(50);
        await Assert.That(Volatile.Read(ref h.Quits)).IsEqualTo(0);
        h.Coordinator.ApplyPendingOnExit();
        await Assert.That(h.Updater.ApplyOnExitCalls).IsEmpty();

        h.Stop.Result.SetResult(DaemonHandoff.Stopped);
        await WaitUntilAsync(() => Volatile.Read(ref h.Quits) == 1, "the quit");
        h.Coordinator.ApplyPendingOnExit();

        await Assert.That(h.Updater.ApplyOnExitCalls.Count).IsEqualTo(1);
        await Assert.That(h.Coordinator.DaemonHandoff).IsEqualTo(DaemonHandoff.Stopped);
        await Assert.That(h.Coordinator.DaemonLeftStopped).IsTrue();
        await Assert.That(h.Stop.Releases).IsEqualTo(0);
    }

    /// <summary>A daemon that cannot be stopped never blocks the update: the updater ends it, as it always did.</summary>
    [Test]
    public async Task A_daemon_that_does_not_stop_still_lets_the_update_apply() {
        var h = new Harness();
        h.Stop.Result.SetResult(DaemonHandoff.Interrupted);

        await h.Coordinator.RunMenuActionAsync();
        await WaitUntilAsync(() => Volatile.Read(ref h.Quits) == 1, "the quit");
        h.Coordinator.ApplyPendingOnExit();

        await Assert.That(h.Updater.ApplyOnExitCalls.Count).IsEqualTo(1);
        await Assert.That(h.Coordinator.DaemonHandoff).IsEqualTo(DaemonHandoff.Interrupted);
        await Assert.That(h.Coordinator.DaemonLeftStopped).IsFalse();
    }

    /// <summary>macOS and Linux: no stop is wired, so the restart quits at once and the daemon is left running.</summary>
    [Test]
    public async Task Without_a_daemon_stop_the_restart_quits_at_once() {
        var h = new Harness(withStop: false);

        await h.Coordinator.RunMenuActionAsync();

        await Assert.That(Volatile.Read(ref h.Quits)).IsEqualTo(1);
        await Assert.That(h.Stop.Calls).IsEqualTo(0);
        await Assert.That(h.Coordinator.DaemonHandoff).IsEqualTo(DaemonHandoff.None);
        h.Coordinator.ApplyPendingOnExit();
        await Assert.That(h.Updater.ApplyOnExitCalls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Not_now_while_the_daemon_waits_withdraws_the_stop_and_keeps_the_update() {
        var h = new Harness(waitingAnswer: false);
        h.Stop.Holds = TimeSpan.FromMinutes(5);

        await h.Coordinator.RunMenuActionAsync();
        await WaitUntilAsync(() => Volatile.Read(ref h.Stop.Releases) == 1, "the release");

        await Assert.That(h.WaitingPrompt!.Disclosure).Contains("After 5 minutes it stops anyway");
        await Assert.That(h.Stop.Token.IsCancellationRequested).IsTrue();
        await Assert.That(Volatile.Read(ref h.Quits)).IsEqualTo(0);
        await Assert.That(h.Coordinator.ReadyVersion).IsEqualTo(Beta3.Version);
        await Assert.That(h.Coordinator.DaemonHandoff).IsEqualTo(DaemonHandoff.None);

        await h.Coordinator.RunMenuActionAsync();
        await WaitUntilAsync(() => Volatile.Read(ref h.Stop.Calls) == 2, "the second stop");
    }

    [Test]
    public async Task Restart_now_cuts_the_wait_short_and_applies() {
        var h = new Harness(waitingAnswer: true);
        h.Stop.Holds = TimeSpan.FromMinutes(5);

        await h.Coordinator.RunMenuActionAsync();
        await WaitUntilAsync(() => Volatile.Read(ref h.Quits) == 1, "the quit");
        h.Coordinator.ApplyPendingOnExit();

        await Assert.That(h.Stop.Token.IsCancellationRequested).IsTrue();
        await Assert.That(h.Coordinator.DaemonHandoff).IsEqualTo(DaemonHandoff.Interrupted);
        await Assert.That(h.Updater.ApplyOnExitCalls.Count).IsEqualTo(1);
    }

    /// <summary>The waiting prompt closing because the daemon stopped is not the user putting the update off.</summary>
    [Test]
    public async Task The_waiting_prompt_closes_by_itself_once_the_daemon_stops() {
        var h = new Harness();
        h.Stop.Holds = TimeSpan.FromMinutes(5);

        await h.Coordinator.RunMenuActionAsync();
        await WaitUntilAsync(() => h.WaitingPrompt is not null, "the waiting prompt");
        h.Stop.Result.SetResult(DaemonHandoff.Stopped);
        await WaitUntilAsync(() => Volatile.Read(ref h.Quits) == 1, "the quit");

        await Assert.That(h.Coordinator.DaemonHandoff).IsEqualTo(DaemonHandoff.Stopped);
        await Assert.That(h.Stop.Releases).IsEqualTo(0);
    }

    [Test]
    public async Task A_second_restart_request_joins_the_stop_already_running() {
        var h = new Harness();

        await h.Coordinator.RunMenuActionAsync();
        await WaitUntilAsync(() => Volatile.Read(ref h.Stop.Calls) == 1, "the daemon stop");
        await h.Coordinator.RunMenuActionAsync();
        await Task.Delay(50);

        await Assert.That(Volatile.Read(ref h.Stop.Calls)).IsEqualTo(1);
        h.Stop.Result.SetResult(DaemonHandoff.Stopped);
        await WaitUntilAsync(() => Volatile.Read(ref h.Quits) == 1, "the quit");
    }

    [Test]
    public async Task The_closing_window_says_what_became_of_the_daemon() {
        await Assert.That(UpdateFinishViewModel.ClosingStep(false, DaemonHandoff.None).Detail).IsEqualTo("The daemon and its agents keep running");
        await Assert.That(UpdateFinishViewModel.ClosingStep(true, DaemonHandoff.Stopped).Detail).Contains("Eligible agents resume after the update");
        await Assert.That(UpdateFinishViewModel.ClosingStep(true, DaemonHandoff.Interrupted).Detail).Contains("Running agents are interrupted");
        await Assert.That(UpdateFinishViewModel.ClosingStep(true, DaemonHandoff.None).Title).IsEqualTo("Closing kcap and the daemon");
    }
}
