using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.Services.Update;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class StaleDaemonNudgeTests {
    const string AppDaemon = "/Applications/kcap.app/Contents/MacOS/kcap-daemon";
    const string NpmDaemon = "/opt/homebrew/lib/node_modules/@kurrent/kcap/node_modules/@kurrent/kcap-darwin-arm64/bin/kcap-daemon";

    static readonly AttachStatus Connected = new(AttachState.Connected, null, [RestartIpc.TurnBoundaryCapability]);

    sealed class Harness : IDisposable {
        public readonly FakeTimeProvider Clock = new();
        public readonly BehaviorSubject<AttachStatus> Status = new(Connected);
        public readonly BehaviorSubject<(string?, int?)> Daemon = new(("1.4.0+abc", 4815));
        public readonly BehaviorSubject<bool> Pending = new(false);
        public readonly List<string> Modes = [];
        public readonly List<(string Mode, string Result)> Outcomes = [];
        public readonly List<DaemonOnOtherInstall?> OtherInstalls = [];
        public string? Executable = AppDaemon;
        public readonly StaleDaemonNudge Nudge;
        readonly IDisposable _otherInstalls;

        public Harness() {
            Nudge = new StaleDaemonNudge("1.4.1", Status, Daemon, Pending, _ => Executable,
                (version, path) => path.StartsWith("/Applications/kcap.app/", StringComparison.Ordinal) ? null : new DaemonOnOtherInstall(version, path),
                (mode, _) => { Modes.Add(mode); return Task.FromResult("queued"); }, (mode, result) => Outcomes.Add((mode, result)), Clock, CancellationToken.None);
            _otherInstalls = Nudge.OtherInstall.Subscribe(OtherInstalls.Add);
        }

        public void Dispose() {
            _otherInstalls.Dispose();
            Nudge.Dispose();
        }
    }

    [Test]
    public async Task An_older_daemon_on_the_app_binary_is_asked_once_for_a_turn_boundary_restart_after_the_grace() {
        using var h = new Harness();

        h.Clock.Advance(StaleDaemonNudge.Grace - TimeSpan.FromSeconds(1));
        await Assert.That(h.Modes).IsEmpty();
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(h.Modes).IsEquivalentTo([RestartIpc.TurnBoundaryMode]);
        await Assert.That(h.Outcomes).IsEquivalentTo([(RestartIpc.TurnBoundaryMode, "queued")]);

        h.Status.OnNext(new AttachStatus(AttachState.Unreachable, ExpectedDaemonRestart.Reason, null));
        h.Status.OnNext(Connected);
        h.Clock.Advance(StaleDaemonNudge.Grace * 2);
        await Assert.That(h.Modes.Count).IsEqualTo(1);
    }

    /// A daemon that does not advertise the turn-boundary mode would read it as a restart that ignores turns.
    [Test]
    public async Task A_daemon_without_the_turn_boundary_mode_is_asked_when_idle() {
        using var h = new Harness();
        h.Status.OnNext(new AttachStatus(AttachState.Connected, null, ["status/1"]));

        h.Clock.Advance(StaleDaemonNudge.Grace);

        await Assert.That(h.Modes).IsEquivalentTo([StaleDaemonNudge.WhenIdleMode]);
    }

    /// Restarting a daemon that runs another install starts the same old version again, and a when-idle request would
    /// sit behind its idle agents for as long as they stay.
    [Test]
    public async Task A_daemon_on_another_install_is_reported_not_asked_until_it_catches_up() {
        using var h = new Harness();
        h.Executable = NpmDaemon;

        h.Clock.Advance(StaleDaemonNudge.Grace);

        await Assert.That(h.Modes).IsEmpty();
        await Assert.That(h.OtherInstalls[^1]).IsEqualTo(new DaemonOnOtherInstall("1.4.0+abc", NpmDaemon));

        h.Daemon.OnNext(("1.4.1+def", 5120));
        await Assert.That(h.OtherInstalls[^1]).IsNull();
        h.Clock.Advance(StaleDaemonNudge.Grace);
        await Assert.That(h.Modes).IsEmpty();
    }

    /// A when-idle restart an earlier app queued sits behind idle agents and would bring back the same version, so it
    /// must not hide the other install.
    [Test]
    public async Task A_daemon_on_another_install_is_reported_with_a_restart_already_queued() {
        using var h = new Harness();
        h.Pending.OnNext(true);
        h.Executable = NpmDaemon;

        h.Clock.Advance(StaleDaemonNudge.Grace);

        await Assert.That(h.OtherInstalls[^1]).IsEqualTo(new DaemonOnOtherInstall("1.4.0+abc", NpmDaemon));
        await Assert.That(h.Modes).IsEmpty();
    }

    /// The other install is read again each time the daemon comes back, so its report follows the version it runs.
    [Test]
    public async Task A_daemon_back_on_its_other_install_but_still_behind_is_reported_at_its_new_version() {
        using var h = new Harness();
        h.Executable = NpmDaemon;
        h.Clock.Advance(StaleDaemonNudge.Grace);

        h.Status.OnNext(new AttachStatus(AttachState.Unreachable, ExpectedDaemonRestart.Reason, null));
        h.Daemon.OnNext(("1.4.0-beta.9", 5120));
        h.Status.OnNext(Connected);
        h.Clock.Advance(StaleDaemonNudge.Grace);

        await Assert.That(h.OtherInstalls[^1]).IsEqualTo(new DaemonOnOtherInstall("1.4.0-beta.9", NpmDaemon));
        await Assert.That(h.Modes).IsEmpty();
    }

    /// A process that will not name its binary is treated as this app's: asking it costs at most a restart into the same version.
    [Test]
    public async Task A_daemon_whose_binary_cannot_be_read_is_still_asked() {
        using var h = new Harness();
        h.Executable = null;

        h.Clock.Advance(StaleDaemonNudge.Grace);

        await Assert.That(h.Modes).IsEquivalentTo([RestartIpc.TurnBoundaryMode]);
        await Assert.That(h.OtherInstalls.All(other => other is null)).IsTrue();
    }

    /// The daemon's own poll queuing the restart, or the daemon catching up, ends the wait without a request.
    [Test]
    public async Task A_daemon_that_queues_its_own_restart_or_catches_up_is_left_alone() {
        using var queued = new Harness();
        queued.Clock.Advance(TimeSpan.FromSeconds(10));
        queued.Pending.OnNext(true);
        queued.Clock.Advance(StaleDaemonNudge.Grace);
        await Assert.That(queued.Modes).IsEmpty();

        using var current = new Harness();
        current.Daemon.OnNext(("1.4.1+def", 4815));
        current.Clock.Advance(StaleDaemonNudge.Grace);
        await Assert.That(current.Modes).IsEmpty();
    }

    [Test]
    [Arguments("1.4.0", true)]
    [Arguments("1.4.1+abc", false)]
    [Arguments("1.5.0", false)]
    [Arguments(null, false)]
    public async Task Only_an_older_daemon_is_behind(string? daemon, bool behind) {
        await Assert.That(StaleDaemonNudge.Behind(daemon, "1.4.1")).IsEqualTo(behind);
    }
}
