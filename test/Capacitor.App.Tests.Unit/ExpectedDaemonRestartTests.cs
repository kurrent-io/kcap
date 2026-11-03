using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class ExpectedDaemonRestartTests {
    static readonly AttachStatus Connected = new(AttachState.Connected, null, []);
    static readonly AttachStatus Connecting = new(AttachState.Connecting, null, null);
    static readonly AttachStatus Down = new(AttachState.Unreachable, "daemon_unreachable", null);
    static readonly AttachStatus Incompatible = new(AttachState.Unreachable, "daemon_incompatible", null);

    sealed class Harness : IDisposable {
        public readonly BehaviorSubject<AttachStatus> Raw = new(Connected);
        public readonly BehaviorSubject<bool> Marker = new(false);
        public readonly FakeTimeProvider Clock = new();
        public bool JustUpdated;
        public int Reattaches;
        public readonly List<(bool Expected, TimeSpan Lasted)> Outages = [];
        public readonly ExpectedDaemonRestart Restart;
        public AttachStatus? Shown;
        /// What the pid file names as alive, read only by a harness built to probe it.
        public int? LivePid;
        public bool Starting;

        public Harness(bool probesPid = false) {
            Restart = new ExpectedDaemonRestart(Raw, Marker, () => JustUpdated,
                () => { Reattaches++; return Task.CompletedTask; }, Clock, (e, t) => Outages.Add((e, t)),
                probesPid ? () => LivePid : null);
            Restart.Status.Subscribe(s => Shown = s);
            Restart.Starting.Subscribe(s => Starting = s);
        }

        public void Dispose() => Restart.Dispose();
    }

    [Test]
    public async Task A_daemon_that_queued_a_restart_reads_as_restarting_while_it_is_down() {
        using var h = new Harness();
        h.Marker.OnNext(true);

        h.Raw.OnNext(Down);

        await Assert.That(h.Shown!.State).IsEqualTo(AttachState.Unreachable);
        await Assert.That(h.Shown.Reason).IsEqualTo(ExpectedDaemonRestart.Reason);
        await Assert.That(h.Restart.Expected).IsTrue();
    }

    [Test]
    public async Task An_outage_with_no_reason_to_expect_it_is_reported_as_is() {
        using var h = new Harness();

        h.Raw.OnNext(Down);

        await Assert.That(h.Shown).IsEqualTo(Down);
        await Assert.That(h.Restart.Expected).IsFalse();
    }

    [Test]
    public async Task An_app_that_just_updated_expects_its_daemon_to_restart_without_a_marker() {
        using var h = new Harness { JustUpdated = true };

        h.Raw.OnNext(Down);

        await Assert.That(h.Shown!.Reason).IsEqualTo(ExpectedDaemonRestart.Reason);
    }

    [Test]
    public async Task An_incompatible_daemon_is_never_relabelled() {
        using var h = new Harness { JustUpdated = true };
        h.Marker.OnNext(true);

        h.Raw.OnNext(Incompatible);

        await Assert.That(h.Shown).IsEqualTo(Incompatible);
    }

    /// Each retry restarts the attach, which reports Connecting first: that must neither flicker the label nor
    /// restart the grace period, or a restart that never comes back would never get its Start button.
    [Test]
    public async Task Retries_keep_the_label_and_the_grace_period_still_ends() {
        using var h = new Harness();
        h.Marker.OnNext(true);
        h.Raw.OnNext(Down);

        for (var i = 0; i < 80; i++) {
            h.Clock.Advance(ExpectedDaemonRestart.RetryInterval);
            h.Raw.OnNext(Connecting);
            await Assert.That(h.Shown!.Reason).IsEqualTo(ExpectedDaemonRestart.Reason);
            h.Raw.OnNext(Down);
        }
        await Assert.That(h.Reattaches).IsGreaterThanOrEqualTo(80);

        h.Clock.Advance(ExpectedDaemonRestart.Grace);

        await Assert.That(h.Shown).IsEqualTo(Down);
        await Assert.That(h.Restart.Expected).IsFalse();
    }

    [Test]
    public async Task Reconnecting_reports_how_long_the_outage_lasted_and_whether_it_was_expected() {
        using var h = new Harness();
        h.Marker.OnNext(true);
        h.Raw.OnNext(Down);
        h.Clock.Advance(TimeSpan.FromSeconds(7));
        h.Marker.OnNext(false);

        h.Raw.OnNext(Connected);

        await Assert.That(h.Shown).IsEqualTo(Connected);
        await Assert.That(h.Outages.SequenceEqual([(true, TimeSpan.FromSeconds(7))])).IsTrue();
    }

    [Test]
    public async Task Nothing_is_retried_once_the_daemon_is_back() {
        using var h = new Harness();
        h.Marker.OnNext(true);
        h.Raw.OnNext(Down);
        h.Raw.OnNext(Connected);
        var before = h.Reattaches;

        h.Clock.Advance(TimeSpan.FromSeconds(10));

        await Assert.That(h.Reattaches).IsEqualTo(before);
    }

    /// A retry cancels the attach in flight, so a daemon still handshaking is left to finish.
    [Test]
    public async Task No_retry_interrupts_an_attach_that_is_still_connecting() {
        using var h = new Harness();
        h.Marker.OnNext(true);
        h.Raw.OnNext(Down);
        h.Clock.Advance(ExpectedDaemonRestart.RetryInterval);
        var afterFirst = h.Reattaches;
        h.Raw.OnNext(Connecting);

        h.Clock.Advance(TimeSpan.FromSeconds(5));

        await Assert.That(afterFirst).IsEqualTo(1);
        await Assert.That(h.Reattaches).IsEqualTo(1);
    }

    static AttachStatus ConnectedTo(int pid) =>
        new(AttachState.Connected, null, [], Identity: new ConnectedIdentity(pid, "instance", "default", "1.0.0"));

    /// The evidence `kcap daemon status` reads as "starting — not yet serving": a live process the
    /// pid file names while the socket does not answer. It is not "not running", and it offers no
    /// Start, which would race the daemon already coming up.
    [Test]
    public async Task A_live_daemon_process_that_does_not_serve_yet_reads_as_starting() {
        using var h = new Harness(probesPid: true);
        h.Raw.OnNext(ConnectedTo(100));
        h.LivePid = 200;

        h.Raw.OnNext(Down);

        await Assert.That(h.Shown!.State).IsEqualTo(AttachState.Unreachable);
        await Assert.That(h.Shown.Reason).IsEqualTo(ExpectedDaemonRestart.StartingReason);
        await Assert.That(h.Starting).IsTrue();
        await Assert.That(h.Restart.Expected).IsTrue();
    }

    /// The daemon the app was attached to can outlive its socket while it shuts down: that is
    /// stopping, and must not read as a daemon on its way up.
    [Test]
    public async Task The_process_the_app_was_attached_to_is_stopping_not_starting() {
        using var h = new Harness(probesPid: true);
        h.Raw.OnNext(ConnectedTo(100));
        h.LivePid = 100;

        h.Raw.OnNext(Down);

        await Assert.That(h.Shown).IsEqualTo(Down);
        await Assert.That(h.Starting).IsFalse();
        await Assert.That(h.Restart.Expected).IsFalse();
    }

    /// The attach reports Unreachable once per outage, so a process that comes up later is found by
    /// the poll, and the attach is retried at once rather than on the connect backoff.
    [Test]
    public async Task A_process_that_comes_up_during_the_outage_is_found_and_reattached() {
        using var h = new Harness(probesPid: true);
        h.Raw.OnNext(ConnectedTo(100));
        h.Raw.OnNext(Down);
        await Assert.That(h.Shown).IsEqualTo(Down);

        h.LivePid = 200;
        h.Clock.Advance(ExpectedDaemonRestart.RetryInterval);

        await Assert.That(h.Shown!.Reason).IsEqualTo(ExpectedDaemonRestart.StartingReason);
        h.Clock.Advance(ExpectedDaemonRestart.RetryInterval);
        await Assert.That(h.Reattaches).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task A_retry_keeps_the_starting_label_and_a_vanished_process_drops_it() {
        using var h = new Harness(probesPid: true);
        h.LivePid = 200;
        h.Raw.OnNext(Down);

        h.Raw.OnNext(Connecting);
        await Assert.That(h.Shown!.Reason).IsEqualTo(ExpectedDaemonRestart.StartingReason);

        h.Raw.OnNext(Down);
        h.LivePid = null;
        h.Clock.Advance(ExpectedDaemonRestart.RetryInterval);

        await Assert.That(h.Shown).IsEqualTo(Down);
        await Assert.That(h.Starting).IsFalse();
    }

    /// A process that never starts serving is not success: past the grace the outage reads as an
    /// ordinary one again, Start button and all.
    [Test]
    public async Task A_daemon_that_never_serves_reads_as_down_once_the_grace_ends() {
        using var h = new Harness(probesPid: true);
        h.LivePid = 200;
        h.Raw.OnNext(Down);
        await Assert.That(h.Shown!.Reason).IsEqualTo(ExpectedDaemonRestart.StartingReason);

        h.Clock.Advance(ExpectedDaemonRestart.Grace);

        await Assert.That(h.Shown).IsEqualTo(Down);
        await Assert.That(h.Starting).IsFalse();
        await Assert.That(h.Restart.Expected).IsFalse();
    }

    [Test]
    public async Task Serving_ends_the_starting_state() {
        using var h = new Harness(probesPid: true);
        h.LivePid = 200;
        h.Raw.OnNext(Down);

        h.Raw.OnNext(ConnectedTo(200));

        await Assert.That(h.Shown!.State).IsEqualTo(AttachState.Connected);
        await Assert.That(h.Starting).IsFalse();
    }
}
