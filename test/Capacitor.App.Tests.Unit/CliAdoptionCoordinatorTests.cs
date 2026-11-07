using System.Runtime.Versioning;
using Capacitor.App.Services;
using Capacitor.App.Services.CliAdoption;
using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;
using TUnit.Assertions.Enums;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit;

[ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
public class CliAdoptionCoordinatorTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static string? Target(string link) => new FileInfo(link).LinkTarget;

    const string FinishSwitching = "Finish switching to the app's kcap…";
    const int Helper = AdoptionWorld.Administrator.HelperPid;

    [Test]
    public async Task A_user_owned_npm_install_is_switched_end_to_end_and_noticed_once() {
        var world = new AdoptionWorld(Tmp);
        bool found;

        using (var launch = world.Launch()) {
            await world.FirstLookAsync(launch);
            found = launch.FoundInstall;
        }

        await Assert.That(Target(world.Link)).IsEqualTo(world.Bundle.Cli);
        await Assert.That(world.PlistProgram()).IsEqualTo(world.Bundle.Daemon);
        await Assert.That(Directory.Exists(world.Root)).IsFalse();
        await Assert.That(Directory.Exists(world.Platform)).IsFalse();
        await Assert.That(world.Refresh.Calls).IsEqualTo(1);
        await Assert.That(world.Machine.Reattaches).IsEqualTo(1);
        await Assert.That(found).IsTrue();

        var record = world.Record;
        await Assert.That(record.Installs.Single().State).IsEqualTo(InstallState.Switched);
        await Assert.That(record.MachineSwitched).IsTrue();
        await Assert.That(record.CutoverUtc).IsNotNull();
        await Assert.That(record.Links).IsEmpty();
        await Assert.That(world.Events).IsEquivalentTo(
            ["adoption_found", "adoption_integrations", "adoption_path", "adoption_repoint", "adoption_retired", "adoption_switched", "adoption_switched"],
            CollectionOrdering.Matching);
        await Assert.That(world.Notifier.Notified.Single()).Contains($"npm install of kcap {AdoptionWorld.Version} was removed");

        using (var next = world.Launch()) await world.FirstLookAsync(next);

        await Assert.That(world.Notifier.Notified.Count).IsEqualTo(1);
        await Assert.That(world.Record.NoticesShown).IsEquivalentTo([AdoptionNotices.SwitchedKey(world.Install)]);
        await Assert.That(world.Refresh.Calls).IsEqualTo(1);
    }

    /// <summary>Integrations and PATH move before the daemon, and a deferred daemon move undoes neither: the Mac runs the
    /// app's kcap with the old install still on disk, which works. A later run moves the daemon, and the service file
    /// that named the install meanwhile is no integration, so the refresh is not run again.</summary>
    [Test]
    public async Task A_busy_repoint_keeps_PATH_and_integrations_and_stays_pending() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Repoint = _ => new MutationOutcome.Refused("daemon_busy", RecoverySurface.None);

        using (var launch = world.Launch()) {
            await world.FirstLookAsync(launch);

            await Assert.That(Target(world.Link)).IsEqualTo(world.Bundle.Cli);
            await Assert.That(world.Record.CutoverUtc).IsNotNull();
            await Assert.That(Directory.Exists(world.Root)).IsTrue();
            await Assert.That(Directory.Exists(world.Platform)).IsTrue();
            await Assert.That(world.Record.Pending).IsTrue();
            await Assert.That(world.Record.Installs.Single().HeldBy).IsNull();
            await Assert.That(world.Machine.ProcessReads).IsEqualTo(0);
            await Assert.That(world.Logged("adoption_repoint").Single()["result"]).IsEqualTo("deferred");
            await Assert.That(world.Logged("adoption_held").Single()["reason"]).IsEqualTo("daemon_busy");
            await Assert.That(world.Notifier.Notified).IsEmpty();

            // While the daemon move waits, a recheck runs the whole batch again; still busy, it deletes nothing.
            await world.RecheckAsync(launch);

            await Assert.That(world.Machine.Repoints).IsEqualTo(2);
            await Assert.That(world.Machine.ProcessReads).IsEqualTo(0);
            await Assert.That(Directory.Exists(world.Root)).IsTrue();
        }

        world.Machine.Repoint = null;
        using (var next = world.Launch()) await world.FirstLookAsync(next);

        await Assert.That(world.Refresh.Calls).IsEqualTo(1);
        await Assert.That(world.PlistProgram()).IsEqualTo(world.Bundle.Daemon);
        await Assert.That(Directory.Exists(world.Root)).IsFalse();
        await Assert.That(world.Record.MachineSwitched).IsTrue();
        await Assert.That(world.Notifier.Notified.Count).IsEqualTo(1);
    }

    /// <summary>A first look that meets a daemon not yet attached, or busy, defers the move; the daemon turning idle
    /// reruns the batch from fresh evidence, with no relaunch.</summary>
    [Test]
    public async Task A_deferred_daemon_move_runs_on_the_next_idle_report_without_a_relaunch() {
        var world = new AdoptionWorld(Tmp);
        var refused = 0;
        world.Machine.Repoint = _ => refused++ == 0 ? new MutationOutcome.Refused(AdoptionRepoint.Busy, RecoverySurface.None) : null;

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);
        await Assert.That(Directory.Exists(world.Root)).IsTrue();

        await world.IdleAsync(launch);

        await Assert.That(world.Machine.Repoints).IsEqualTo(2);
        await Assert.That(world.PlistProgram()).IsEqualTo(world.Bundle.Daemon);
        await Assert.That(Directory.Exists(world.Root)).IsFalse();
        await Assert.That(world.Record.MachineSwitched).IsTrue();
    }

    /// <summary>A hosted agent that stays open ends its turn: that alone reruns the batch, since the move keeps the
    /// agent for the new daemon.</summary>
    [Test]
    public async Task A_deferred_daemon_move_runs_when_an_open_agent_finishes_its_turn() {
        var world = new AdoptionWorld(Tmp);
        var refused = 0;
        world.Machine.Repoint = _ => refused++ == 0 ? new MutationOutcome.Refused(AdoptionRepoint.Busy, RecoverySurface.None) : null;
        world.Activity.OnNext(new DaemonActivity(1, Busy: true));

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);
        world.Activity.OnNext(new DaemonActivity(1, Busy: true, Quiet: true));
        await world.SettledAsync(launch);

        await Assert.That(world.Machine.Repoints).IsEqualTo(2);
        await Assert.That(world.PlistProgram()).IsEqualTo(world.Bundle.Daemon);
        await Assert.That(Directory.Exists(world.Root)).IsFalse();
    }

    [Test]
    public async Task A_deferred_daemon_move_runs_again_on_the_recheck_tick() {
        var world = new AdoptionWorld(Tmp);
        var refused = 0;
        world.Machine.Repoint = _ => refused++ == 0 ? new MutationOutcome.Refused("daemon_busy", RecoverySurface.None) : null;

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);
        await world.RecheckAsync(launch);

        await Assert.That(Directory.Exists(world.Root)).IsFalse();
        await Assert.That(world.Record.MachineSwitched).IsTrue();
    }

    /// <summary>A refusal the daemon gives, one of its units being busy, is not the app's view of it: the idle report a
    /// new attach brings, or any other, must not rerun the batch, or it would rerun, defer and reattach for hours. It
    /// waits for the tick.</summary>
    [Test]
    public async Task A_daemon_busy_deferral_waits_for_the_tick_however_the_daemon_reattaches() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Repoint = _ => new MutationOutcome.Refused("daemon_busy", RecoverySurface.None);

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);
        await world.IdleAsync(launch);

        await Assert.That(world.Machine.EvidenceReads).IsEqualTo(1);
        await Assert.That(world.Machine.Repoints).IsEqualTo(1);

        await world.RecheckAsync(launch);

        await Assert.That(world.Machine.EvidenceReads).IsEqualTo(2);
        await Assert.That(world.Machine.Repoints).IsEqualTo(2);
    }

    [Test]
    public async Task An_all_deferred_move_requests_no_reattach() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Repoint = _ => new MutationOutcome.Refused(AdoptionRepoint.Busy, RecoverySurface.None);

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        await Assert.That(world.Machine.Reattaches).IsEqualTo(0);
        await Assert.That(world.Machine.EvidenceReads).IsEqualTo(1);
    }

    /// <summary>An idle report that lands during a run is replayed when the run ends, but not on a coordinator the app
    /// has disposed in the meantime.</summary>
    [Test]
    public async Task A_retry_after_dispose_runs_nothing() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Repoint = _ => new MutationOutcome.Refused(AdoptionRepoint.Busy, RecoverySurface.None);
        var launch = world.Launch();
        await world.FirstLookAsync(launch);

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        world.Machine.Evidence = async _ => {
            entered.TrySetResult();
            await release.Task;
            return world.Found();
        };
        world.Activity.OnNext(new DaemonActivity(0, Busy: false, Quiet: true));
        await entered.Task.WaitAsync(AdoptionWorld.Bounded);
        var held = launch.Settled;

        world.Activity.OnNext(null);
        world.Activity.OnNext(new DaemonActivity(0, Busy: false, Quiet: true));
        launch.Dispose();
        release.SetResult();
        await held.WaitAsync(AdoptionWorld.Bounded);

        await Assert.That(ReferenceEquals(launch.Settled, held)).IsTrue();
        await Assert.That(world.Machine.EvidenceReads).IsEqualTo(2);
    }

    /// <summary>The first install's repoint restarts the app's daemon, so the second meets it unattached and defers;
    /// the attach that follows reports it idle, and that one report reruns the batch, which finishes the second.</summary>
    [Test]
    public async Task A_second_install_refused_while_the_daemon_restarted_is_retried_once_it_reports_idle() {
        var world = new AdoptionWorld(Tmp);
        var second = world.AddInstall("nvm", "flow", 5100);
        var refused = 0;
        world.Machine.Repoint = install =>
            install == second.Install && refused++ == 0 ? new MutationOutcome.Refused(AdoptionRepoint.Busy, RecoverySurface.None) : null;

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        await Assert.That(world.Machine.EvidenceReads).IsEqualTo(2);
        await Assert.That(world.Machine.Repoints).IsEqualTo(3);
        await Assert.That(Directory.Exists(world.Root)).IsFalse();
        await Assert.That(Directory.Exists(second.Root)).IsFalse();
        await Assert.That(Directory.Exists(second.Platform)).IsFalse();
        await Assert.That(world.Record.Installs.All(i => i.State == InstallState.Switched)).IsTrue();
        await Assert.That(world.Record.MachineSwitched).IsTrue();
    }

    /// <summary>A held launch records no intent, so the holders an earlier launch found are still in the record: a
    /// recheck acting on them would delete during a launch whose plan was held.</summary>
    [Test]
    [Arguments(AdoptionBatch.EvidenceUnavailable)]
    [Arguments(AdoptionPreflight.NewerInstall)]
    public async Task A_held_launch_arms_no_recheck_over_an_earlier_launch_s_holders(string hold) {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Processes = [world.OldSession(4300)];
        world.Machine.Alive.Add(4300);
        using (var launch = world.Launch()) await world.FirstLookAsync(launch);

        var newer = world.Install with { Root = Path.Combine(world.Home, ".kcap"), Version = "9.0.0" };
        world.Machine.Evidence = hold == AdoptionBatch.EvidenceUnavailable
            ? _ => throw new IOException("Could not determine the state of service 'io.kurrent.kcap.daemon.mac'.")
            : _ => Task.FromResult(world.Found() with { Installs = [newer] });
        using var held = world.Launch();
        await world.FirstLookAsync(held);
        await Assert.That(world.Logged("adoption_held").Single()["reason"]).IsEqualTo(hold);

        world.Machine.Alive.Clear();
        world.Machine.Processes = [];
        var reads = world.Machine.ProcessReads;
        await world.RecheckAsync(held);

        await Assert.That(world.Machine.ProcessReads).IsEqualTo(reads);
        await Assert.That(Directory.Exists(world.Root)).IsTrue();
        await Assert.That(Directory.Exists(world.Platform)).IsTrue();
    }

    /// <summary>An install that cannot be read while its binaries are there is being changed, as mid
    /// <c>npm install -g</c>: it is held, never read as gone.</summary>
    [Test]
    public async Task An_install_that_cannot_be_read_while_its_binaries_are_there_is_held() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Processes = [world.OldSession(4300)];
        world.Machine.Alive.Add(4300);
        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        world.Machine.Alive.Clear();
        world.Machine.Processes = [];
        world.Machine.InstallsUnreadable = true;
        await world.RecheckAsync(launch);

        await Assert.That(world.Record.Installs.Single().HeldBy).IsEqualTo(AdoptionDeletion.IdentityUnreadable);
        await Assert.That(Directory.Exists(world.Root)).IsTrue();
        await Assert.That(Directory.Exists(world.Platform)).IsTrue();
    }

    /// <summary>The entry holding the binaries goes first, so a retirement cut short after it leaves an install that
    /// reads as gone, and the next check finishes it.</summary>
    [Test]
    public async Task A_retirement_cut_short_after_the_binaries_went_is_finished() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Processes = [world.OldSession(4300)];
        world.Machine.Alive.Add(4300);
        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        Directory.Delete(world.Platform, recursive: true);
        world.Machine.Alive.Clear();
        world.Machine.Processes = [];
        await world.RecheckAsync(launch);

        await Assert.That(Directory.Exists(world.Root)).IsFalse();
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
    }

    /// <summary>A package reinstalled at the same path after the identity check carries another version; its renamed
    /// folder is moved back, never deleted.</summary>
    [Test]
    public async Task A_package_reinstalled_at_another_version_is_never_deleted() {
        var world = new AdoptionWorld(Tmp);
        File.WriteAllText(Path.Combine(world.Platform, "package.json"), """{"name":"@kurrent/kcap-darwin-arm64","version":"9.9.9"}""");

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        await Assert.That(world.Record.Installs.Single().HeldBy).IsEqualTo(AdoptionDeletion.Changed);
        await Assert.That(File.Exists(world.Install.DaemonBinary)).IsTrue();
        await Assert.That(Directory.Exists(world.Root)).IsTrue();
    }

    /// <summary>A real process list holds the app's own process; one without it could not be read, and would say that
    /// nothing runs from the install.</summary>
    [Test]
    public async Task A_process_list_without_the_app_itself_holds_the_deletion() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.ProcessesUnreadable = true;

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        await Assert.That(world.Record.Installs.Single().HeldBy).IsEqualTo(AdoptionDeletion.ProcessesUnreadable);
        await Assert.That(Directory.Exists(world.Root)).IsTrue();
        await Assert.That(Directory.Exists(world.Platform)).IsTrue();
    }

    /// <summary>An aside an earlier run left that cannot be deleted holds its own install, and only that: the rest of
    /// the batch still runs, and nothing of that install is retired past it.</summary>
    [Test]
    public async Task An_aside_that_cannot_be_deleted_holds_its_install_and_the_rest_runs() {
        var world = new AdoptionWorld(Tmp);
        var aside = new AsideRetirement(world.Config, "cli-adoption-retire").Plan(world.Root).Aside;
        Directory.CreateDirectory(aside);
        File.WriteAllText(Path.Combine(aside, "left-behind"), "");
        File.SetUnixFileMode(aside, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try {
            using var launch = world.Launch();
            await world.FirstLookAsync(launch);

            var failed = world.Logged("adoption_retired").Single();
            await Assert.That(failed["install"]).IsEqualTo(world.Root);
            await Assert.That(failed["result"]).IsEqualTo("failed");
            await Assert.That(Target(world.Link)).IsEqualTo(world.Bundle.Cli);
            await Assert.That(world.PlistProgram()).IsEqualTo(world.Bundle.Daemon);
            await Assert.That(world.Record.Installs.Single().HeldBy).IsEqualTo(AdoptionDeletion.Unfinished + world.Root);
            await Assert.That(Directory.Exists(world.Root)).IsTrue();
            await Assert.That(Directory.Exists(world.Platform)).IsTrue();
        } finally {
            File.SetUnixFileMode(aside, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>A daemon its service did not start leaves the install, and the notice says which daemon to stop; once
    /// it is stopped, the next launch finishes the switch.</summary>
    [Test]
    public async Task A_left_install_says_what_finishes_it_and_switches_once_that_is_done() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.OtherDaemons.Add(new RunningDaemonFact("manual", 4999, "tok-4999", world.Install.DaemonBinary));

        using (var launch = world.Launch()) await world.FirstLookAsync(launch);

        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Left);
        await Assert.That(world.Notifier.Notified.Single()).Contains("kcap daemon stop --name manual");
        await Assert.That(Target(world.Link)).IsEqualTo("../lib/node_modules/@kurrent/kcap/bin/kcap.js");
        await Assert.That(world.Record.MachineSwitched).IsFalse();

        world.Machine.OtherDaemons.Clear();
        using (var next = world.Launch()) await world.FirstLookAsync(next);

        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
        await Assert.That(Directory.Exists(world.Root)).IsFalse();
        await Assert.That(world.Record.MachineSwitched).IsTrue();
        await Assert.That(world.Notifier.Notified.Count).IsEqualTo(2);
    }

    /// <summary>A second daemon running from the install is named once the first is stopped: another cause is another
    /// outcome, recorded and told on its own.</summary>
    [Test]
    public async Task A_left_install_names_the_next_daemon_once_the_first_is_stopped() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.OtherDaemons.Add(new RunningDaemonFact("manual", 4999, "tok-4999", world.Install.DaemonBinary));
        world.Machine.OtherDaemons.Add(new RunningDaemonFact("spare", 5999, "tok-5999", world.Install.DaemonBinary));

        using (var launch = world.Launch()) await world.FirstLookAsync(launch);
        world.Machine.OtherDaemons.RemoveAt(0);
        using (var next = world.Launch()) await world.FirstLookAsync(next);

        await Assert.That(world.Record.Installs.Single().HeldBy).IsEqualTo("spare");
        await Assert.That(world.Notifier.Notified.Count).IsEqualTo(2);
        await Assert.That(world.Notifier.Notified[0]).Contains("kcap daemon stop --name manual");
        await Assert.That(world.Notifier.Notified[1]).Contains("kcap daemon stop --name spare");
    }

    /// <summary>Fixing the shell profile does not reach the PATH a service saved when it was installed. Once only that
    /// PATH lists the install's folder, the outcome is a new one, told once with how to give that service a fresh PATH,
    /// and the switch finishes when it has one.</summary>
    [Test]
    public async Task A_folder_left_on_a_service_PATH_is_told_on_its_own_and_switches_once_that_PATH_is_fresh() {
        var world = new AdoptionWorld(Tmp);
        var own   = world.Install.CliBinary;
        var (login, stored) = (own, own);
        world.Machine.Evidence = _ => {
            var found = world.Found();
            return Task.FromResult(found with {
                KcapOnLoginPath = login, Units = [.. found.Units.Select(u => u with { KcapOnStoredPath = stored })],
            });
        };

        using (var launch = world.Launch()) await world.FirstLookAsync(launch);

        await Assert.That(world.Record.Installs.Single().Reason).IsEqualTo(LeftReason.OwnFolderOnPath);
        await Assert.That(world.Notifier.Notified.Single()).Contains("shell profile");

        login = world.Link;
        using (var next = world.Launch()) await world.FirstLookAsync(next);

        await Assert.That(world.Record.Installs.Single().Reason).IsEqualTo(LeftReason.OwnFolderOnServicePath);
        await Assert.That(world.Notifier.Notified.Count).IsEqualTo(2);
        await Assert.That(world.Notifier.Notified[^1]).Contains($"kcap daemon service install --replace --verify --name {AdoptionWorld.DaemonName}");
        await Assert.That(Directory.Exists(world.Root)).IsTrue();

        stored = world.Link;
        using (var last = world.Launch()) await world.FirstLookAsync(last);

        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
        await Assert.That(Directory.Exists(world.Root)).IsFalse();
        await Assert.That(world.Notifier.Notified.Count).IsEqualTo(3);
    }

    /// <summary>A login shell that already finds the bundle elsewhere leaves the install's own link in place on every PATH
    /// that reaches it; the switch points that link at the bundle too before the install goes.</summary>
    [Test]
    public async Task A_link_the_login_shell_no_longer_finds_first_is_pointed_at_the_bundle() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Evidence = _ => {
            var found = world.Found();
            return Task.FromResult(found with {
                KcapOnLoginPath = world.Bundle.Cli, Units = [.. found.Units.Select(u => u with { KcapOnStoredPath = world.Bundle.Cli })],
            });
        };

        using (var launch = world.Launch()) await world.FirstLookAsync(launch);

        await Assert.That(Target(world.Link)).IsEqualTo(world.Bundle.Cli);
        await Assert.That(Directory.Exists(world.Root)).IsFalse();
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
    }

    /// <summary>The lane proves only the app's daemon answering; the service file must name the bundle's daemon too.</summary>
    [Test]
    public async Task A_repoint_that_leaves_the_service_file_on_the_old_daemon_is_not_a_move() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Repoint = _ => {
            world.Machine.Jobs[AdoptionWorld.DaemonName] = new LaunchdJob(true, AdoptionWorld.NewDaemonPid, world.Bundle.Daemon);
            return new MutationOutcome.Succeeded();
        };

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        await Assert.That(world.Logged("adoption_repoint").Single()["reason"]).IsEqualTo($"unit_not_moved:{AdoptionWorld.DaemonName}");
        await Assert.That(Directory.Exists(world.Root)).IsTrue();
        await Assert.That(world.Record.Pending).IsTrue();
        await Assert.That(world.Notifier.Notified).IsEmpty();
    }

    /// <summary>For the app's own daemon, which was running, only the lane's proof of a successor counts: a plist and
    /// a job that look moved do not.</summary>
    [Test]
    public async Task A_running_daemon_the_lane_could_not_prove_is_not_a_move() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Repoint = _ => {
            world.WritePlist(world.Bundle.Daemon);
            world.Machine.Jobs[AdoptionWorld.DaemonName] = new LaunchdJob(true, AdoptionWorld.NewDaemonPid, world.Bundle.Daemon);
            return new MutationOutcome.AttentionSkew("predecessor_still_running");
        };

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        await Assert.That(world.Logged("adoption_repoint").Single()["result"]).IsEqualTo("failed");
        await Assert.That(Directory.Exists(world.Root)).IsTrue();
        await Assert.That(world.Record.Pending).IsTrue();
    }

    /// <summary>The repoint drops its preimage once the bootstrap succeeds, so a successor the lane cannot prove has no
    /// rollback, and once nothing names the install it must still stay as the fallback, told once, until a later run
    /// sees the app's daemon answer from the bundle.</summary>
    [Test]
    public async Task A_daemon_move_the_lane_could_not_prove_keeps_the_install_until_a_later_run_proves_it() {
        var world = new AdoptionWorld(Tmp);
        var answers = false;
        world.Machine.Repoint = _ => {
            world.WritePlist(world.Bundle.Daemon);
            world.Machine.Jobs[AdoptionWorld.DaemonName] = new LaunchdJob(true, AdoptionWorld.NewDaemonPid, world.Bundle.Daemon);
            return answers ? new MutationOutcome.Succeeded() : new MutationOutcome.UnconfirmedNoAttach();
        };

        using (var launch = world.Launch()) await world.FirstLookAsync(launch);

        await Assert.That(world.Record.Pending).IsTrue();
        await Assert.That(world.Notifier.Notified.Single()).Contains($"daemon '{AdoptionWorld.DaemonName}'");
        await Assert.That(world.Notifier.Notified.Single()).Contains($"kept the npm install of kcap {AdoptionWorld.Version}");

        using (var next = world.Launch()) {
            await world.FirstLookAsync(next);
            await world.RecheckAsync(next);
        }

        await Assert.That(world.Found().Installs).IsEmpty();
        await Assert.That(Directory.Exists(world.Root)).IsTrue();
        await Assert.That(Directory.Exists(world.Platform)).IsTrue();
        await Assert.That(world.Machine.Repoints).IsEqualTo(2);
        await Assert.That(world.Record.Pending).IsTrue();
        await Assert.That(world.Notifier.Notified.Count).IsEqualTo(1);

        answers = true;
        using (var last = world.Launch()) await world.FirstLookAsync(last);

        await Assert.That(Directory.Exists(world.Root)).IsFalse();
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
        await Assert.That(world.Record.UnprovenMoves).IsEmpty();
        await Assert.That(world.Notifier.Notified.Count).IsEqualTo(2);
        await Assert.That(world.Notifier.Notified[^1]).Contains("was removed");
    }

    /// <summary>The lane proves only the app's own daemon. Another service of the install, rewritten onto the bundle but
    /// stopped since, names nothing the next run could see, so the install stays the fallback until that service runs
    /// again.</summary>
    [Test]
    public async Task Another_service_that_stopped_after_its_move_keeps_the_install_until_it_runs_again() {
        var world = new AdoptionWorld(Tmp);
        var plist = Path.Combine(world.Home, "Library", "LaunchAgents", "io.kurrent.kcap.daemon.flow.plist");
        var program = world.Install.DaemonBinary;
        world.Machine.Jobs["flow"] = new LaunchdJob(true, 4300, program);
        world.Machine.Evidence = _ => {
            var found = world.Found();
            var job = world.Machine.Jobs["flow"];
            ServiceUnitFact flow = new(plist, "flow", program, found.Units[0].StoredPath, null, job.Loaded, job.Pid, job.Loaded ? job.Program : null);
            return Task.FromResult(found with { Units = [.. found.Units, flow] });
        };
        var runsAgain = false;
        world.Machine.Repoint = _ => {
            world.WritePlist(world.Bundle.Daemon);
            world.Machine.Jobs[AdoptionWorld.DaemonName] = new LaunchdJob(true, AdoptionWorld.NewDaemonPid, world.Bundle.Daemon);
            program = world.Bundle.Daemon;
            File.WriteAllText(plist, $"<plist><dict><key>ProgramArguments</key><array><string>{program}</string></array></dict></plist>");
            world.Machine.Jobs["flow"] = new LaunchdJob(true, runsAgain ? 4400 : null, program);
            return new MutationOutcome.Succeeded();
        };

        using (var launch = world.Launch()) await world.FirstLookAsync(launch);

        await Assert.That(world.Logged("adoption_repoint").Single()["reason"]).IsEqualTo("unit_stopped:flow");
        await Assert.That(world.Record.UnprovenMoves.Single().Daemon).IsEqualTo("flow");
        await Assert.That(Directory.Exists(world.Root)).IsTrue();

        using (var next = world.Launch()) await world.FirstLookAsync(next);

        await Assert.That(Directory.Exists(world.Root)).IsTrue();
        await Assert.That(world.Record.Pending).IsTrue();

        runsAgain = true;
        using (var last = world.Launch()) await world.FirstLookAsync(last);

        await Assert.That(Directory.Exists(world.Root)).IsFalse();
        await Assert.That(world.Record.UnprovenMoves).IsEmpty();
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
    }

    /// <summary>A fallback the user removed by hand is nothing to keep: the next run lets the unproven move go instead of
    /// asking a repoint about an install that is gone.</summary>
    [Test]
    public async Task An_unproven_fallback_removed_by_hand_is_let_go() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Repoint = _ => {
            world.WritePlist(world.Bundle.Daemon);
            world.Machine.Jobs[AdoptionWorld.DaemonName] = new LaunchdJob(true, AdoptionWorld.NewDaemonPid, world.Bundle.Daemon);
            return new MutationOutcome.UnconfirmedNoAttach();
        };
        using (var launch = world.Launch()) await world.FirstLookAsync(launch);
        await Assert.That(world.Record.UnprovenMoves).IsNotEmpty();

        Directory.Delete(world.Root, recursive: true);
        Directory.Delete(world.Platform, recursive: true);
        using (var next = world.Launch()) await world.FirstLookAsync(next);

        await Assert.That(world.Machine.Repoints).IsEqualTo(1);
        await Assert.That(world.Record.UnprovenMoves).IsEmpty();
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
    }

    /// <summary>A service that was not running gets no successor to answer, so its plist is the proof, and launchd must
    /// hold no job for it: a stopped service is never started by the switch.</summary>
    [Test]
    public async Task A_stopped_service_counts_once_its_plist_names_the_bundle_daemon() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Jobs[AdoptionWorld.DaemonName] = new LaunchdJob(false, null, null);
        world.Machine.Repoint = _ => {
            world.WritePlist(world.Bundle.Daemon);
            return new MutationOutcome.UnconfirmedNoAttach();
        };

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        await Assert.That(world.Logged("adoption_repoint").Single()["result"]).IsEqualTo("ok");
        await Assert.That(world.Machine.Jobs[AdoptionWorld.DaemonName].Loaded).IsFalse();
        await Assert.That(Directory.Exists(world.Root)).IsFalse();
        await Assert.That(world.Record.MachineSwitched).IsTrue();
    }

    /// <summary>A launch that stops before the deletion check leaves no holder for a recheck to act on: only a launch
    /// that reached the check has proven the PATH and the daemon moved.</summary>
    [Test]
    public async Task A_launch_stopped_before_the_deletion_check_leaves_rechecks_nothing_to_delete() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Processes = [world.OldSession(4300)];
        world.Machine.Alive.Add(4300);
        using (var launch = world.Launch()) await world.FirstLookAsync(launch);

        world.Machine.Sites = [new ReferenceSite(Path.Combine(world.Home, ".codex", "hooks.json"), world.Install.CliBinary, true)];
        world.Refresh.ExitCode = 1;
        using var next = world.Launch();
        await world.FirstLookAsync(next);

        await Assert.That(world.Logged("adoption_integrations")[^1]["result"]).IsEqualTo("failed");
        await Assert.That(world.Record.Installs.Single().HeldBy).IsNull();

        world.Machine.Alive.Clear();
        world.Machine.Processes = [];
        world.Machine.Sites = [];
        var reads = world.Machine.ProcessReads;
        await world.RecheckAsync(next);

        await Assert.That(world.Machine.ProcessReads).IsEqualTo(reads);
        await Assert.That(Directory.Exists(world.Root)).IsTrue();
    }

    /// <summary>A crash between an aside rename and its deletion leaves both names; the next batch finishes it first.
    /// One the helper set aside and never checked may be a tree that replaced the one checked before the prompt, so it
    /// goes back to its name instead.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task An_interrupted_deletion_is_finished_before_the_batch_runs(bool helperChecked) {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Evidence = _ => Task.FromResult(new AdoptionEvidence([], [], [], world.LoginPath, null));
        var leftover = Directory.CreateDirectory(Path.Combine(world.Prefix, "old-kcap")).FullName;
        var aside = new AsideRetirement(world.Config, "cli-adoption-retire").Plan(leftover).Aside;
        var setAside = helperChecked ? AsideRetirement.CheckedAside(aside) : aside;
        Directory.Move(leftover, setAside);

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        await Assert.That(Directory.Exists(setAside)).IsFalse();
        await Assert.That(Directory.Exists(leftover)).IsEqualTo(!helperChecked);
        await Assert.That(File.Exists(world.Config.Path("cli-adoption-retire"))).IsFalse();
    }

    [Test]
    public async Task A_recheck_with_the_holder_still_alive_reads_nothing_else() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Processes = [world.OldSession(4300)];
        world.Machine.Alive.Add(4300);

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        var held = world.Record.Installs.Single();
        await Assert.That(held.State).IsEqualTo(InstallState.Pending);
        await Assert.That(held.HeldBy).IsEqualTo("pid:4300");
        await Assert.That(held.HolderPid).IsEqualTo(4300);
        await Assert.That(held.HolderStartToken).IsEqualTo("tok-4300");
        await Assert.That(Directory.Exists(world.Root)).IsTrue();
        var reads = (world.Machine.EvidenceReads, world.Machine.ProcessReads, world.Machine.SiteReads);

        await world.RecheckAsync(launch);
        await world.RecheckAsync(launch);

        await Assert.That((world.Machine.EvidenceReads, world.Machine.ProcessReads, world.Machine.SiteReads)).IsEqualTo(reads);
        await Assert.That(Directory.Exists(world.Root)).IsTrue();
        await Assert.That(world.Record.Pending).IsTrue();
    }

    [Test]
    public async Task A_recheck_after_the_holder_exits_retires_without_a_second_notice() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Processes = [world.OldSession(4300)];
        world.Machine.Alive.Add(4300);

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        await Assert.That(world.Notifier.Notified.Single()).Contains("removed once nothing still uses it");

        world.Machine.Alive.Clear();
        world.Machine.Processes = [];
        await world.RecheckAsync(launch);

        await Assert.That(Directory.Exists(world.Root)).IsFalse();
        await Assert.That(Directory.Exists(world.Platform)).IsFalse();
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
        await Assert.That(world.Record.MachineSwitched).IsTrue();
        await Assert.That(world.Notifier.Notified.Count).IsEqualTo(1);

        // Nothing is pending, so no timer is left to fire.
        var reads = world.Machine.ProcessReads;
        await world.RecheckAsync(launch);
        await Assert.That(world.Machine.ProcessReads).IsEqualTo(reads);
    }

    [Test]
    public async Task No_timer_runs_when_nothing_is_pending() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Evidence = _ => Task.FromResult(new AdoptionEvidence([], [], [], world.LoginPath, null));

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);
        await world.RecheckAsync(launch);
        await world.RecheckAsync(launch);

        await Assert.That(world.Machine.EvidenceReads).IsEqualTo(1);
        await Assert.That(world.Machine.ProcessReads).IsEqualTo(0);
        await Assert.That(world.Machine.SiteReads).IsEqualTo(0);
        await Assert.That(world.RecordWritten).IsFalse();
        await Assert.That(world.Events).IsEmpty();
    }

    [Test, NotInParallel]
    public async Task FirstLook_completes_when_skipped_on_KCAP_APP_CLI_PATH_and_when_the_run_throws() {
        var world = new AdoptionWorld(Tmp);

        using (EnvScope.Exclusive(CliAdoptionCoordinator.CliOverride, Path.Combine(Tmp.Path, "dev", "kcap"))) {
            using var skipped = world.Launch();
            skipped.Start();
            await skipped.FirstLook.WaitAsync(AdoptionWorld.Bounded);
        }
        await Assert.That(world.Machine.EvidenceReads).IsEqualTo(0);

        using (EnvScope.Exclusive(CliAdoptionCoordinator.CliOverride, null)) {
            world.Machine.Evidence = _ => throw new InvalidOperationException("boom");
            using var throwing = world.Launch();
            throwing.Start();
            await throwing.FirstLook.WaitAsync(AdoptionWorld.Bounded);

            await Assert.That(throwing.FirstLook.IsCompletedSuccessfully).IsTrue();
            await Assert.That(world.Logged("adoption_held").Single()["reason"]).IsEqualTo("unexpected_error");
        }

        using var off = new CliAdoptionCoordinator(null, world.Time, (_, _) => { }, CancellationToken.None);
        off.Start();
        await off.FirstLook.WaitAsync(AdoptionWorld.Bounded);
        await Assert.That(world.Machine.EvidenceReads).IsEqualTo(1);
    }

    /// <summary>A same-version reinstall between the identity check and the rename passes every layout check: only its
    /// daemon's identity tells it apart, so it goes back and the install is held as changed.</summary>
    [Test]
    public async Task A_same_version_reinstall_found_after_the_rename_is_put_back() {
        var world = new AdoptionWorld(Tmp);
        var reinstalled = world.Machine.Identity;
        world.Machine.Identity = path => path.Contains(".retired-", StringComparison.Ordinal) ? "16777232:9999:2:2" : reinstalled(path);

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        await Assert.That(Directory.Exists(world.Root)).IsTrue();
        await Assert.That(Directory.Exists(world.Platform)).IsTrue();
        await Assert.That(world.Logged("adoption_retired").Any(e => e["reason"] == AdoptionDeletion.Changed)).IsTrue();
    }

    /// <summary>A same-version reinstall begun once the platform package is gone brings that package back before the
    /// launcher goes: the launcher left at its path may be the reinstall's, so the retirement stops there.</summary>
    [Test]
    public async Task A_reinstall_begun_between_entries_keeps_the_rest() {
        var world = new AdoptionWorld(Tmp);
        var identity = world.Machine.Identity;
        world.Machine.Identity = path => {
            if (path.Contains(".retired-", StringComparison.Ordinal)) Directory.CreateDirectory(Path.Combine(world.Platform, "bin"));
            return identity(path);
        };

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        await Assert.That(File.Exists(Path.Combine(world.Root, "package.json"))).IsTrue();
        await Assert.That(world.Logged("adoption_retired").Any(e => e["reason"] == AdoptionDeletion.Changed)).IsTrue();
    }

    /// <summary>A batch held over an install it found changes nothing, so the ordinary offer to put kcap on PATH must not
    /// follow it either, this launch.</summary>
    [Test]
    public async Task A_batch_held_over_a_found_install_holds_the_shim_offer() {
        var world = new AdoptionWorld(Tmp);
        var newer = world.Install with { Root = Path.Combine(world.Home, ".kcap"), Version = "9.0.0" };
        world.Machine.Evidence = _ => Task.FromResult(world.Found() with { Installs = [newer] });

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        await Assert.That(world.Logged("adoption_held").Single()["reason"]).IsEqualTo(AdoptionPreflight.NewerInstall);
        await Assert.That(launch.FoundInstall).IsTrue();
    }

    /// <summary>A launchctl answer that cannot be read says nothing about what is installed: reading it as "nothing
    /// found" would let a later batch skip the install it could not see.</summary>
    [Test]
    public async Task Evidence_that_cannot_be_read_holds_the_batch_and_changes_nothing() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Evidence = _ => throw new IOException("Could not determine the state of service 'io.kurrent.kcap.daemon.mac'.");

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        await Assert.That(world.Logged("adoption_held").Single()["reason"]).IsEqualTo("evidence_unavailable");
        await Assert.That(world.RecordWritten).IsFalse();
        await Assert.That(world.Refresh.Calls).IsEqualTo(0);
        await Assert.That(launch.FoundInstall).IsFalse();
    }

    /// <summary>A root-owned install is switched after everything the user can do alone: when the prompt opens, the
    /// user-owned install beside it is gone, the daemon runs the bundle's and the root-owned link still finds the old
    /// install. One prompt, its link on the bundle, its package folders gone and the helper's record cleared.</summary>
    [Test]
    public async Task A_root_owned_install_runs_last_behind_one_prompt_and_is_switched() {
        var world = new AdoptionWorld(Tmp);
        var mine  = world.AddInstall("nvm", "flow", 5100);
        world.RootOwned();
        (bool MineGone, string? Daemon, string? Link)? atPrompt = null;
        world.Admin.WhenPrompted = () => atPrompt = (!Directory.Exists(mine.Platform), world.PlistProgram(), Target(world.Link));

        using (var launch = world.Launch()) await world.FirstLookAsync(launch);

        await Assert.That(world.Admin.Prompts.Count).IsEqualTo(1);
        await Assert.That(atPrompt).IsEqualTo((true, world.Bundle.Daemon, AdoptionWorld.NpmLinkTarget));
        var spec = world.Admin.Specs.Single();
        await Assert.That(spec.Links.Select(l => l.Path)).IsEquivalentTo([world.Link]);
        await Assert.That(spec.Entries.Select(e => e.Entry.Original)).IsEquivalentTo(world.Install.OwnedEntries);
        await Assert.That(Target(world.Link)).IsEqualTo(world.Bundle.Cli);
        await Assert.That(Directory.Exists(world.Root)).IsFalse();
        await Assert.That(Directory.Exists(world.Platform)).IsFalse();
        await Assert.That(world.Record.Installs.All(i => i.State == InstallState.Switched)).IsTrue();
        await Assert.That(world.Record.Privileged).IsNull();
        await Assert.That(world.Record.MachineSwitched).IsTrue();
        await Assert.That(world.Notifier.Notified.Count(n => n.Contains("was removed"))).IsEqualTo(2);

        using (var next = world.Launch()) await world.FirstLookAsync(next);

        await Assert.That(world.Admin.Prompts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task The_prompt_text_names_what_it_removes_and_that_every_account_shares_it() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();

        using (var launch = world.Launch()) await world.FirstLookAsync(launch);

        var prompt = world.Admin.Prompts.Single();
        await Assert.That(prompt).Contains($"npm install of kcap {AdoptionWorld.Version} at {world.Root}");
        await Assert.That(prompt).Contains("Every account on this Mac shares");
    }

    /// <summary>macOS shows its own wording in the administrator prompt, so the app says what it removes and why that
    /// takes a password before the prompt opens.</summary>
    [Test]
    public async Task The_app_explains_the_password_before_the_administrator_prompt() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        var promptsWhenExplained = -1;
        world.Explain = _ => {
            promptsWhenExplained = world.Admin.Prompts.Count;
            return true;
        };

        using (var launch = world.Launch()) await world.FirstLookAsync(launch);

        var explained = world.Explained.Single();
        await Assert.That(explained.Kind).IsEqualTo(LifecyclePrompt.KindRemoveSharedInstall);
        await Assert.That(explained.Disclosure).Contains($"npm install of kcap {AdoptionWorld.Version} at {world.Root}");
        await Assert.That(explained.Disclosure).Contains("administrator password");
        await Assert.That(promptsWhenExplained).IsEqualTo(0);
        await Assert.That(world.Admin.Prompts.Count).IsEqualTo(1);
    }

    /// <summary>Not now changes nothing the helper would have, and leaves the install as a cancelled prompt does.</summary>
    [Test]
    public async Task Declining_the_explanation_leaves_the_install_without_a_prompt() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        world.Explain = _ => false;

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        var left = world.Record.Installs.Single();
        await Assert.That(left.State).IsEqualTo(InstallState.Left);
        await Assert.That(left.Reason).IsEqualTo(LeftReason.PromptCancelled);
        await Assert.That(world.Admin.Prompts).IsEmpty();
        await Assert.That(AdoptionWorld.Offered(launch)).IsTrue();
        await Assert.That(Target(world.Link)).IsEqualTo(AdoptionWorld.NpmLinkTarget);
        await Assert.That(Directory.Exists(world.Platform)).IsTrue();
        await Assert.That(world.Notifier.Notified.Single()).Contains(FinishSwitching);
    }

    /// <summary>Finish switching is the user asking for the prompt, so it opens with no explanation in front of it.</summary>
    [Test]
    public async Task Finish_switching_raises_the_prompt_without_explaining_again() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        world.Explain = _ => false;
        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        await launch.FinishSwitchingAsync().WaitAsync(AdoptionWorld.Bounded);
        await world.SettledAsync(launch);

        await Assert.That(world.Explained.Count).IsEqualTo(1);
        await Assert.That(world.Admin.Prompts.Count).IsEqualTo(1);
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
    }

    /// <summary>A dismissed prompt changes nothing the helper would have, and is never raised again unasked: not by a
    /// recheck or an idle report, nor on the next launch, which still offers Finish switching.</summary>
    [Test]
    public async Task A_cancelled_prompt_leaves_the_install_and_offers_Finish_switching() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        world.Admin.Answer = PrivilegedResult.Cancelled;

        using (var launch = world.Launch()) {
            await world.FirstLookAsync(launch);

            var left = world.Record.Installs.Single();
            await Assert.That(left.State).IsEqualTo(InstallState.Left);
            await Assert.That(left.Reason).IsEqualTo(LeftReason.PromptCancelled);
            await Assert.That(AdoptionWorld.Offered(launch)).IsTrue();
            await Assert.That(world.Record.Privileged).IsNull();
            await Assert.That(Target(world.Link)).IsEqualTo(AdoptionWorld.NpmLinkTarget);
            await Assert.That(Directory.Exists(world.Platform)).IsTrue();
            await Assert.That(world.Notifier.Notified.Single()).Contains(FinishSwitching);

            await world.RecheckAsync(launch);
            await world.IdleAsync(launch);
        }
        using (var next = world.Launch()) {
            await world.FirstLookAsync(next);
            await Assert.That(AdoptionWorld.Offered(next)).IsTrue();
        }

        await Assert.That(world.Admin.Prompts.Count).IsEqualTo(1);
        await Assert.That(world.Notifier.Notified.Count).IsEqualTo(1);
        await Assert.That(Directory.Exists(world.Platform)).IsTrue();
    }

    /// <summary>Another account still using the shared install keeps it, with its links put back and a notice saying
    /// why; Finish switching, once that account has stopped, removes it.</summary>
    [Test]
    public async Task A_shared_install_another_account_still_uses_is_kept_for_Finish_switching() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        world.Admin.Answer = PrivilegedResult.InUse;
        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        var left = world.Record.Installs.Single();
        await Assert.That(left.State).IsEqualTo(InstallState.Left);
        await Assert.That(left.Reason).IsEqualTo(LeftReason.SharedInstallInUse);
        await Assert.That(world.Record.Privileged).IsNull();
        await Assert.That(Target(world.Link)).IsEqualTo(AdoptionWorld.NpmLinkTarget);
        await Assert.That(Directory.Exists(world.Platform)).IsTrue();
        await Assert.That(AdoptionWorld.Offered(launch)).IsTrue();
        await Assert.That(world.Notifier.Notified.Single()).Contains("another account").And.Contains(FinishSwitching);

        world.Admin.Answer = null;
        await launch.FinishSwitchingAsync().WaitAsync(AdoptionWorld.Bounded);
        await world.SettledAsync(launch);

        await Assert.That(world.Admin.Prompts.Count).IsEqualTo(2);
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
        await Assert.That(Directory.Exists(world.Platform)).IsFalse();
    }

    /// <summary>An install made writable after a cancelled prompt is the user's to remove: Finish switching takes it
    /// without a second prompt, rather than handing a helper links this run already swapped.</summary>
    [Test]
    public async Task An_install_made_writable_after_a_cancelled_prompt_is_switched_without_one() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        world.Admin.Answer = PrivilegedResult.Cancelled;
        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        world.UserCanWrite = _ => true;
        await launch.FinishSwitchingAsync().WaitAsync(AdoptionWorld.Bounded);
        await world.SettledAsync(launch);

        await Assert.That(world.Admin.Prompts.Count).IsEqualTo(1);
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
        await Assert.That(Directory.Exists(world.Platform)).IsFalse();
    }

    /// <summary>A reinstall at the root a cancelled prompt left is another install: the left record lets it go instead
    /// of reopening an install that is gone, Finish switching is no longer offered, and the reinstall is reported.</summary>
    [Test]
    public async Task A_reinstall_over_a_left_install_retires_its_record() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        world.Admin.Answer = PrivilegedResult.Cancelled;
        using var launch = world.Launch();
        await world.FirstLookAsync(launch);
        await Assert.That(AdoptionWorld.Offered(launch)).IsTrue();

        var reinstalled = world.Install with { Identity = "16777232:777:3:3" };
        world.Machine.Evidence = _ => Task.FromResult(world.Found() with { Installs = [reinstalled] });
        world.Admin.Answer = null;
        await launch.FinishSwitchingAsync().WaitAsync(AdoptionWorld.Bounded);
        await world.SettledAsync(launch);

        await Assert.That(world.Record.Installs).IsEmpty();
        await Assert.That(AdoptionWorld.Offered(launch)).IsFalse();
        await Assert.That(world.Admin.Prompts.Count).IsEqualTo(1);
        await Assert.That(Directory.Exists(world.Platform)).IsTrue();
        await Assert.That(world.Notifier.Notified[^1]).Contains("left it in place");
    }

    [Test]
    public async Task Finish_switching_reruns_the_batch_for_that_install_and_prompts_again() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        world.Admin.Answer = PrivilegedResult.Cancelled;
        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        world.Admin.Answer = null;
        await launch.FinishSwitchingAsync().WaitAsync(AdoptionWorld.Bounded);
        await world.SettledAsync(launch);

        await Assert.That(world.Admin.Prompts.Count).IsEqualTo(2);
        await Assert.That(world.Admin.Specs[1].ControlFifo).IsNotEqualTo(world.Admin.Specs[0].ControlFifo);
        await Assert.That(world.Admin.Specs[1].StatusFile).IsNotEqualTo(world.Admin.Specs[0].StatusFile);
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
        await Assert.That(Target(world.Link)).IsEqualTo(world.Bundle.Cli);
        await Assert.That(Directory.Exists(world.Root)).IsFalse();
        await Assert.That(Directory.Exists(world.Platform)).IsFalse();
        await Assert.That(AdoptionWorld.Offered(launch)).IsFalse();
        await Assert.That(world.Record.MachineSwitched).IsTrue();
        await Assert.That(world.Notifier.Notified[^1]).Contains("was removed");
    }

    /// <summary>A quit while the helper deleted, after go: the next launch waits for that same helper before it reads
    /// anything or finishes an aside itself, then settles from what the helper left.</summary>
    [Test]
    public async Task A_helper_still_running_at_launch_is_waited_for_before_anything_else() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        var asides = world.InterruptedHelper($"tok-{Helper}");
        world.Machine.Alive.Add(Helper);
        (int EvidenceReads, bool AsidesThere)? waiting = null;
        world.Machine.WhileWaiting = () => {
            waiting = (world.Machine.EvidenceReads, asides.All(a => Directory.Exists(AsideRetirement.CheckedAside(a.Aside))));
            foreach (var aside in asides) Directory.Delete(AsideRetirement.CheckedAside(aside.Aside), recursive: true);
        };

        using (var launch = world.Launch()) await world.FirstLookAsync(launch);

        await Assert.That(world.Machine.Waits.Single()).IsEqualTo((Helper, (string?)$"tok-{Helper}", AdoptionPrivilegedStep.SettleWait));
        await Assert.That(waiting).IsEqualTo((0, true));
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
        await Assert.That(world.Record.Privileged).IsNull();
        await Assert.That(world.Admin.Prompts).IsEmpty();
        await Assert.That(world.Notifier.Notified.Single()).Contains("was removed");
    }

    /// <summary>A helper still deleting when the wait is up keeps its record: its half-done files are not read, and
    /// neither the launch nor Finish switching starts a second helper beside it.</summary>
    [Test]
    public async Task A_helper_still_running_past_the_wait_is_left_to_finish() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        var asides = world.InterruptedHelper($"tok-{Helper}");
        world.Machine.Alive.Add(Helper);
        world.Machine.Lingering.Add(Helper);

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);
        await launch.FinishSwitchingAsync().WaitAsync(AdoptionWorld.Bounded);
        await world.SettledAsync(launch);

        await Assert.That(world.Record.Privileged).IsNotNull();
        await Assert.That(world.Admin.Prompts).IsEmpty();
        await Assert.That(world.Explained).IsEmpty();
        await Assert.That(asides.All(a => Directory.Exists(AsideRetirement.CheckedAside(a.Aside)))).IsTrue();
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Pending);
        await Assert.That(world.Record.Installs.Single().HeldBy).IsEqualTo(AdoptionPrivilegedStep.HelperRunning);
    }

    /// <summary>An aside the helper left after go can only be removed as root, and a helper whose start identity was
    /// never read is waited for the whole bound: the install is left for Finish switching, and the app raises no
    /// prompt of its own.</summary>
    [Test]
    public async Task A_part_deleted_aside_after_go_is_left_DeletionCutShort_without_a_prompt() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        var asides = world.InterruptedHelper(startToken: null);
        Directory.Delete(AsideRetirement.CheckedAside(asides[0].Aside), recursive: true);
        AdoptionWorld.RootOnly(AsideRetirement.CheckedAside(asides[1].Aside));
        try {
            using var launch = world.Launch();
            await world.FirstLookAsync(launch);

            await Assert.That(world.Machine.Waits.Single()).IsEqualTo((Helper, (string?)null, AdoptionPrivilegedStep.SettleWait));
            var left = world.Record.Installs.Single();
            await Assert.That(left.State).IsEqualTo(InstallState.Left);
            await Assert.That(left.Reason).IsEqualTo(LeftReason.DeletionCutShort);
            await Assert.That(world.Record.Privileged).IsNull();
            await Assert.That(Directory.Exists(AsideRetirement.CheckedAside(asides[1].Aside))).IsTrue();
            await Assert.That(AdoptionWorld.Offered(launch)).IsTrue();
            await Assert.That(world.Notifier.Notified.Single()).Contains(FinishSwitching);

            await world.RecheckAsync(launch);

            await Assert.That(world.Admin.Prompts).IsEmpty();
        } finally {
            world.ReleaseRootOnly();
        }
    }

    /// <summary>Finishing what a helper left after go: the link is already on the bundle, so the helper is handed no
    /// link to swap, only the aside to delete.</summary>
    [Test]
    public async Task Finish_switching_deletes_what_a_cut_short_helper_left() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        var asides = world.InterruptedHelper(startToken: null);
        Directory.Delete(AsideRetirement.CheckedAside(asides[0].Aside), recursive: true);
        AdoptionWorld.RootOnly(AsideRetirement.CheckedAside(asides[1].Aside));
        try {
            using var launch = world.Launch();
            await world.FirstLookAsync(launch);
            await launch.FinishSwitchingAsync().WaitAsync(AdoptionWorld.Bounded);
            await world.SettledAsync(launch);

            var spec = world.Admin.Specs.Single();
            await Assert.That(spec.Links).IsEmpty();
            await Assert.That(spec.Entries.Single(e => e.Entry.Original == asides[1].Original).Entry.Aside).IsEqualTo(asides[1].Aside);
            await Assert.That(Path.Exists(AsideRetirement.CheckedAside(asides[1].Aside))).IsFalse();
            await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
            await Assert.That(AdoptionWorld.Offered(launch)).IsFalse();
            await Assert.That(world.Notifier.Notified[^1]).Contains("was removed");
        } finally {
            world.ReleaseRootOnly();
        }
    }

    /// <summary>A helper the app saw stop after go, with an aside it could not delete, leaves the install the same way
    /// one settled at the next launch does.</summary>
    [Test]
    public async Task A_deletion_the_helper_cut_short_is_left_DeletionCutShort() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        world.Admin.Cut = AdoptionWorld.Administrator.CutShortAt.WithAsidesLeft;
        try {
            using var launch = world.Launch();
            await world.FirstLookAsync(launch);

            var left = world.Record.Installs.Single();
            await Assert.That(left.State).IsEqualTo(InstallState.Left);
            await Assert.That(left.Reason).IsEqualTo(LeftReason.DeletionCutShort);
            await Assert.That(world.Record.Privileged).IsNull();
            await Assert.That(AdoptionWorld.Offered(launch)).IsTrue();
            await Assert.That(world.Notifier.Notified.Single()).Contains(FinishSwitching);
        } finally {
            world.ReleaseRootOnly();
        }
    }

    /// <summary>A helper that ends before go has set nothing aside: with every file in place the install is pending, a
    /// working state with its link on the bundle. Finish switching is offered meanwhile, and the next launch's prompt
    /// finishes it with no link left to swap.</summary>
    [Test]
    public async Task A_helper_cut_short_before_go_with_everything_in_place_stays_pending() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        world.Admin.Cut = AdoptionWorld.Administrator.CutShortAt.BeforeGo;

        using (var launch = world.Launch()) {
            await world.FirstLookAsync(launch);

            var settled = world.Logged("adoption_privileged").Single(e => e["result"] == "settled");
            await Assert.That((settled["reason"], settled["go_sent"])).IsEqualTo(("in_place", "false"));
            await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Pending);
            await Assert.That(world.Record.Privileged).IsNull();
            await Assert.That(Target(world.Link)).IsEqualTo(world.Bundle.Cli);
            await Assert.That(Directory.Exists(world.Platform)).IsTrue();
            await Assert.That(world.Notifier.Notified).IsEmpty();
            await Assert.That(AdoptionWorld.Offered(launch)).IsTrue();
        }

        world.Admin.Cut = AdoptionWorld.Administrator.CutShortAt.Never;
        using (var next = world.Launch()) await world.FirstLookAsync(next);

        await Assert.That(world.Admin.Specs[1].Links).IsEmpty();
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
        await Assert.That(Directory.Exists(world.Platform)).IsFalse();
    }

    /// <summary>A helper stopped after go but before it set anything aside left every entry in place: Finish switching
    /// is offered, and takes it behind a new prompt, without waiting for the next launch.</summary>
    [Test]
    public async Task A_helper_cut_short_after_go_with_everything_in_place_is_offered_Finish_switching() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        world.Admin.Cut = AdoptionWorld.Administrator.CutShortAt.AfterGo;
        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        var settled = world.Logged("adoption_privileged").Single(e => e["result"] == "settled");
        await Assert.That((settled["reason"], settled["go_sent"])).IsEqualTo(("in_place", "true"));
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Pending);
        await Assert.That(AdoptionWorld.Offered(launch)).IsTrue();

        world.Admin.Cut = AdoptionWorld.Administrator.CutShortAt.Never;
        await launch.FinishSwitchingAsync().WaitAsync(AdoptionWorld.Bounded);
        await world.SettledAsync(launch);

        await Assert.That(world.Admin.Prompts.Count).IsEqualTo(2);
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
        await Assert.That(AdoptionWorld.Offered(launch)).IsFalse();
    }

    /// <summary>A helper that deleted everything but never reported it, after go, left nothing to finish.</summary>
    [Test]
    public async Task A_helper_cut_short_after_deleting_everything_is_switched() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        world.Admin.Cut = AdoptionWorld.Administrator.CutShortAt.AfterDeleting;

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        var settled = world.Logged("adoption_privileged").Single(e => e["result"] == "settled");
        await Assert.That((settled["reason"], settled["go_sent"])).IsEqualTo(("deleted", "true"));
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
        await Assert.That(world.Record.Privileged).IsNull();
        await Assert.That(AdoptionWorld.Offered(launch)).IsFalse();
        await Assert.That(world.Notifier.Notified.Single()).Contains("was removed");
    }

    /// <summary>A root-owned install whose daemon move waited is not prompted for when the move goes through on the
    /// daemon's idle report, nor on a recheck: the prompt waits for the next launch, or Finish switching, which is
    /// offered from then on.</summary>
    [Test]
    public async Task A_root_owned_install_whose_daemon_move_waited_is_prompted_for_on_the_next_launch_only() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        var refused = 0;
        world.Machine.Repoint = _ => refused++ == 0 ? new MutationOutcome.Refused(AdoptionRepoint.Busy, RecoverySurface.None) : null;

        using (var launch = world.Launch()) {
            await world.FirstLookAsync(launch);
            await world.IdleAsync(launch);

            await Assert.That(world.Machine.Repoints).IsEqualTo(2);
            await Assert.That(world.PlistProgram()).IsEqualTo(world.Bundle.Daemon);

            await world.RecheckAsync(launch);

            await Assert.That(world.Admin.Prompts).IsEmpty();
            await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Pending);
            await Assert.That(Directory.Exists(world.Platform)).IsTrue();
            await Assert.That(AdoptionWorld.Offered(launch)).IsTrue();
        }

        using (var next = world.Launch()) await world.FirstLookAsync(next);

        await Assert.That(world.Admin.Prompts.Count).IsEqualTo(1);
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
    }

    /// <summary>Finish switching takes a root-owned install whose daemon moved on the idle report behind its prompt,
    /// without waiting for the next launch, and explains it first: no prompt this session got that far.</summary>
    [Test]
    public async Task Finish_switching_prompts_for_a_root_owned_install_moved_on_the_idle_report() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        var refused = 0;
        world.Machine.Repoint = _ => refused++ == 0 ? new MutationOutcome.Refused(AdoptionRepoint.Busy, RecoverySurface.None) : null;
        using var launch = world.Launch();
        await world.FirstLookAsync(launch);
        await Assert.That(AdoptionWorld.Offered(launch)).IsFalse();
        await world.IdleAsync(launch);
        await Assert.That(AdoptionWorld.Offered(launch)).IsTrue();

        await launch.FinishSwitchingAsync().WaitAsync(AdoptionWorld.Bounded);
        await world.SettledAsync(launch);

        await Assert.That(world.Explained.Count).IsEqualTo(1);
        await Assert.That(world.Admin.Prompts.Count).IsEqualTo(1);
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
        await Assert.That(AdoptionWorld.Offered(launch)).IsFalse();
    }

    /// <summary>An install left because the app ran from ~/Applications is not offered there; once the app runs from
    /// /Applications it is, without a prompt of its own, and Finish switching then takes it behind one.</summary>
    [Test]
    public async Task A_shared_install_left_for_a_private_app_is_offered_once_the_app_is_in_Applications() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        world.Bundle = world.Bundle with { InSystemApplications = false };

        using (var launch = world.Launch()) {
            await world.FirstLookAsync(launch);

            await Assert.That(world.Record.Installs.Single().Reason).IsEqualTo(LeftReason.SharedInstallPrivateApp);
            await Assert.That(AdoptionWorld.Offered(launch)).IsFalse();
            await Assert.That(world.Notifier.Notified.Single()).Contains($"move kcap to /Applications, open it and choose {FinishSwitching}");
        }

        world.Bundle = world.Bundle with { InSystemApplications = true };
        using var moved = world.Launch();
        await world.FirstLookAsync(moved);

        await Assert.That(AdoptionWorld.Offered(moved)).IsTrue();
        await Assert.That(world.Admin.Prompts).IsEmpty();
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Left);

        await moved.FinishSwitchingAsync().WaitAsync(AdoptionWorld.Bounded);
        await world.SettledAsync(moved);

        await Assert.That(world.Admin.Prompts.Count).IsEqualTo(1);
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
        await Assert.That(Directory.Exists(world.Platform)).IsFalse();
        await Assert.That(AdoptionWorld.Offered(moved)).IsFalse();
    }

    /// <summary>A pending install the evidence no longer finds is not planned, so the privileged step itself keeps a
    /// machine-wide install from being switched for an app installed for one user.</summary>
    [Test]
    public async Task A_pending_install_the_evidence_no_longer_finds_is_left_while_the_app_is_in_user_Applications() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        world.Bundle = world.Bundle with { InSystemApplications = false };
        world.SwitchedAllButTheFiles();

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        await Assert.That(world.Logged("adoption_found")).IsEmpty();
        var left = world.Record.Installs.Single();
        await Assert.That(left.State).IsEqualTo(InstallState.Left);
        await Assert.That(left.Reason).IsEqualTo(LeftReason.SharedInstallPrivateApp);
        await Assert.That(world.Admin.Prompts).IsEmpty();
        await Assert.That(Directory.Exists(world.Platform)).IsTrue();
    }

    /// <summary>A Finish run can leave the install it took back in for a reason the user can clear, here a daemon they
    /// started from the old install after cancelling the prompt. The switched machine checks that install again on each
    /// launch, as its notice says, and switches it once the daemon is stopped; an install that is only reported stays
    /// untouched throughout.</summary>
    [Test]
    public async Task A_reopened_install_left_for_a_rederived_reason_is_checked_again_on_a_switched_machine() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        world.Admin.Answer = PrivilegedResult.Cancelled;
        using (var launch = world.Launch()) await world.FirstLookAsync(launch);
        await Assert.That(world.Record.MachineSwitched).IsTrue();

        var later = world.AddInstall("nvm", "flow", 5100);
        world.Machine.OtherDaemons.Add(new RunningDaemonFact("manual", 4999, "tok-4999", world.Install.DaemonBinary));
        world.Admin.Answer = null;
        using (var finishing = world.Launch()) {
            await world.FirstLookAsync(finishing);
            await finishing.FinishSwitchingAsync().WaitAsync(AdoptionWorld.Bounded);
            await world.SettledAsync(finishing);
        }

        var left = world.Record.Installs.Single(i => i.Install.Root == world.Root);
        await Assert.That((left.State, left.Reason)).IsEqualTo((InstallState.Left, (LeftReason?)LeftReason.UnsupervisedDaemon));
        await Assert.That(world.Notifier.Notified[^1]).Contains("kcap daemon stop --name manual");
        await Assert.That(world.Admin.Prompts.Count).IsEqualTo(1);

        using (var stillRunning = world.Launch()) await world.FirstLookAsync(stillRunning);

        await Assert.That(world.Record.Installs.Single(i => i.Install.Root == world.Root).State).IsEqualTo(InstallState.Left);
        await Assert.That(world.Admin.Prompts.Count).IsEqualTo(1);

        world.Machine.OtherDaemons.Clear();
        using (var next = world.Launch()) await world.FirstLookAsync(next);

        await Assert.That(world.Admin.Prompts.Count).IsEqualTo(2);
        await Assert.That(world.Record.Installs.Single(i => i.Install.Root == world.Root).State).IsEqualTo(InstallState.Switched);
        await Assert.That(Directory.Exists(world.Platform)).IsFalse();
        await Assert.That(world.Record.MachineSwitched).IsTrue();
        await Assert.That(world.Record.Installs.Select(i => i.Install.Root)).IsEquivalentTo([world.Root]);
        await Assert.That(Directory.Exists(later.Platform)).IsTrue();
        await Assert.That(Target(later.Link)).IsEqualTo(AdoptionWorld.NpmLinkTarget);
        await Assert.That(AdoptionWorld.ProgramOf(later)).IsEqualTo(later.Install.DaemonBinary);
        var asked = world.Admin.Specs.SelectMany(spec => spec.Entries).Select(e => e.Entry.Original);
        await Assert.That(asked.Any(e => e.StartsWith(later.Root, StringComparison.Ordinal))).IsFalse();
    }

    /// <summary>A pending root-owned install with nothing of it left is switched, wherever the app runs: there is
    /// nothing for the ~/Applications rule to keep.</summary>
    [Test]
    public async Task A_pending_install_with_nothing_left_is_switched_even_from_user_Applications() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        world.Bundle = world.Bundle with { InSystemApplications = false };
        world.SwitchedAllButTheFiles();
        Directory.Delete(world.Root, recursive: true);
        Directory.Delete(world.Platform, recursive: true);

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
        await Assert.That(world.Logged("adoption_retired").Single()["reason"]).IsEqualTo(AdoptionPrivilegedStep.NothingLeft);
        await Assert.That(world.Admin.Prompts).IsEmpty();
    }

    /// <summary>Finish switching never clears the machine's switch: an install that appeared after it is only reported,
    /// and stays untouched and unprompted for on later launches, even while the reopened install stays held.</summary>
    [Test]
    public async Task Finish_switching_keeps_a_switched_machine_switched_and_a_reported_install_untouched() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        world.Admin.Answer = PrivilegedResult.Cancelled;
        using (var launch = world.Launch()) await world.FirstLookAsync(launch);
        await Assert.That(world.Record.MachineSwitched).IsTrue();

        var later = world.AddInstall("nvm", "flow", 5100);
        world.Machine.Processes = [world.OldSession(4300)];
        world.Machine.Alive.Add(4300);
        world.Admin.Answer = null;
        using (var finishing = world.Launch()) {
            await world.FirstLookAsync(finishing);
            await finishing.FinishSwitchingAsync().WaitAsync(AdoptionWorld.Bounded);
            await world.SettledAsync(finishing);

            var reopened = world.Record.Installs.Single(i => i.Install.Root == world.Root);
            await Assert.That(reopened.State).IsEqualTo(InstallState.Pending);
            await Assert.That(reopened.HeldBy).IsEqualTo("pid:4300");
            await Assert.That(world.Record.MachineSwitched).IsTrue();
        }
        using (var next = world.Launch()) await world.FirstLookAsync(next);

        await Assert.That(world.Admin.Prompts.Count).IsEqualTo(1);
        await Assert.That(world.Record.MachineSwitched).IsTrue();
        await Assert.That(world.Record.Installs.Select(i => i.Install.Root)).IsEquivalentTo([world.Root]);
        await Assert.That(Directory.Exists(later.Platform)).IsTrue();
        await Assert.That(Target(later.Link)).IsEqualTo(AdoptionWorld.NpmLinkTarget);
        await Assert.That(AdoptionWorld.ProgramOf(later)).IsEqualTo(later.Install.DaemonBinary);
        await Assert.That(world.Machine.Repoints).IsEqualTo(1);
        await Assert.That(world.Notifier.Notified.Count(n => n.Contains(later.Root))).IsEqualTo(1);
    }

    /// <summary>The checks run again once the helper has swapped the link: a daemon started from the install while
    /// the prompt was open keeps it, the helper puts the link back, and nothing is noticed or asked again this launch,
    /// however the holder goes away: not by a recheck, nor by the batch running again for a daemon move it deferred.
    /// Once a recheck sees the holder gone, Finish switching is offered.</summary>
    [Test]
    public async Task A_manual_daemon_started_while_the_prompt_was_open_puts_everything_back() {
        var world = new AdoptionWorld(Tmp);
        var deferred = world.AddInstall("nvm", "flow", 5100);
        var refused  = 0;
        world.Machine.Repoint = install =>
            install == deferred.Install && refused++ < 2 ? new MutationOutcome.Refused(AdoptionRepoint.Busy, RecoverySurface.None) : null;
        world.RootOwned();
        world.Admin.WhileSwapped = () => {
            world.Machine.Processes = [new ProcessFact(4999, world.Install.DaemonBinary, null, world.Time.GetUtcNow().UtcDateTime)];
            world.Machine.Alive.Add(4999);
        };

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        var kept = world.Record.Installs.Single(i => i.Install.Root == world.Root);
        await Assert.That(kept.State).IsEqualTo(InstallState.Pending);
        await Assert.That(world.Logged("adoption_kept").Single(e => e["install"] == world.Root)["reason"]).IsEqualTo("pid:4999");
        await Assert.That(world.Logged("adoption_privileged").Single()["result"]).IsEqualTo("put_back");
        await Assert.That(Target(world.Link)).IsEqualTo(AdoptionWorld.NpmLinkTarget);
        await Assert.That(Directory.Exists(world.Root)).IsTrue();
        await Assert.That(Directory.Exists(world.Platform)).IsTrue();
        await Assert.That(world.Record.Privileged).IsNull();
        await Assert.That(world.Notifier.Notified).IsEmpty();
        await Assert.That(AdoptionWorld.Offered(launch)).IsFalse();

        world.Machine.Processes = [];
        world.Machine.Alive.Clear();
        await world.RecheckAsync(launch);
        await world.IdleAsync(launch);

        await Assert.That(Directory.Exists(deferred.Platform)).IsFalse();
        await Assert.That(world.Admin.Prompts.Count).IsEqualTo(1);
        await Assert.That(Directory.Exists(world.Platform)).IsTrue();
        await Assert.That(world.Record.Pending).IsTrue();
        await Assert.That(AdoptionWorld.Offered(launch)).IsTrue();
    }

    /// <summary>Nothing is asked while something still holds a root-owned install, and a recheck, which runs as this
    /// user, never deletes it once the holder is gone: the next launch's prompt does.</summary>
    [Test]
    public async Task A_held_root_owned_install_is_neither_prompted_for_nor_deleted_by_a_recheck() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        world.Machine.Processes = [world.OldSession(4300)];
        world.Machine.Alive.Add(4300);

        using (var launch = world.Launch()) {
            await world.FirstLookAsync(launch);

            await Assert.That(world.Record.Installs.Single().HeldBy).IsEqualTo("pid:4300");
            await Assert.That(world.Admin.Prompts).IsEmpty();

            world.Machine.Processes = [];
            world.Machine.Alive.Clear();
            await world.RecheckAsync(launch);

            await Assert.That(Directory.Exists(world.Platform)).IsTrue();
            await Assert.That(world.Admin.Prompts).IsEmpty();
        }

        using (var next = world.Launch()) await world.FirstLookAsync(next);

        await Assert.That(world.Admin.Prompts.Count).IsEqualTo(1);
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
        await Assert.That(Directory.Exists(world.Platform)).IsFalse();
    }

    /// <summary>A left install the user removed while a PATH still finds no bundle stays left; once the login shell and
    /// every service find the bundle's kcap, it was finished by hand: recorded switched, with no notice and no prompt.</summary>
    [Test]
    public async Task A_left_install_the_user_removed_by_hand_is_recorded_switched_silently() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        world.Admin.Answer = PrivilegedResult.Cancelled;
        using (var launch = world.Launch()) await world.FirstLookAsync(launch);

        Directory.Delete(world.Root, recursive: true);
        Directory.Delete(world.Platform, recursive: true);
        using (var dangling = world.Launch()) await world.FirstLookAsync(dangling);

        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Left);

        KcapLinkSwap.Replace(world.Link, AdoptionWorld.NpmLinkTarget, world.Bundle.Cli);
        using (var next = world.Launch()) {
            await world.FirstLookAsync(next);
            await Assert.That(AdoptionWorld.Offered(next)).IsFalse();
        }

        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
        await Assert.That(world.Record.MachineSwitched).IsTrue();
        await Assert.That(world.Notifier.Notified.Count).IsEqualTo(1);
        await Assert.That(world.Admin.Prompts.Count).IsEqualTo(1);
    }

    /// <summary>Running it alone before a replacement exists would leave no kcap on that PATH.</summary>
    [Test]
    public async Task No_bare_sudo_npm_uninstall_appears_in_any_notice() {
        var world  = new AdoptionWorld(Tmp);
        var script = world.Install with { Kind = OtherCliInstallKind.Script };
        foreach (var install in new[] { world.Install, script }) {
            string[] notices = [
                AdoptionNotices.Switched(install, removed: true), AdoptionNotices.Switched(install, removed: false),
                AdoptionNotices.Reported(install),
                .. Enum.GetValues<LeftReason>().SelectMany(r =>
                    new[] { AdoptionNotices.Left(install, r, null), AdoptionNotices.Left(install, r, "mac") }),
            ];
            foreach (var notice in notices) await Assert.That(notice).DoesNotContain("npm uninstall");
        }
        foreach (var reason in new[] { LeftReason.PromptCancelled, LeftReason.DeletionCutShort })
            await Assert.That(AdoptionNotices.Left(world.Install, reason, null)).Contains(FinishSwitching);

        world.RootOwned();
        world.Admin.Answer = PrivilegedResult.Cancelled;
        using (var launch = world.Launch()) await world.FirstLookAsync(launch);

        await Assert.That(world.Notifier.Notified).IsNotEmpty();
        foreach (var notice in world.Notifier.Notified) await Assert.That(notice).DoesNotContain("npm uninstall");
    }

    [Test]
    public async Task An_install_appearing_after_the_machine_switched_is_noticed_once_and_kept() {
        var world = new AdoptionWorld(Tmp);
        new AdoptionRecordStore(world.Config).Update(r => r with { MachineSwitched = true });

        for (var run = 0; run < 2; run++) {
            using var launch = world.Launch();
            await world.FirstLookAsync(launch);
        }

        await Assert.That(world.Notifier.Notified.Single()).Contains(world.Root);
        await Assert.That(world.Record.NoticesShown).IsEquivalentTo([AdoptionNotices.ReportedKey(world.Install)]);
        await Assert.That(world.Logged("adoption_kept")[0]["reason"]).IsEqualTo("machine_switched");
        await Assert.That(Target(world.Link)).IsEqualTo("../lib/node_modules/@kurrent/kcap/bin/kcap.js");
        await Assert.That(Directory.Exists(world.Root)).IsTrue();
        await Assert.That(world.Refresh.Calls).IsEqualTo(0);
        await Assert.That(world.Machine.Repoints).IsEqualTo(0);
        await Assert.That(world.Record.Installs).IsEmpty();
    }

    /// <summary>Once the switch moved everything, the install is invisible to the evidence; it stays pending from the
    /// record, and a reference rewritten to it between launches both reruns the refresh and keeps its files. The refresh
    /// leaves it naming the install, so the cutover stays where it was.</summary>
    [Test]
    public async Task A_reference_recreated_between_launches_holds_the_deletion() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Processes = [world.OldSession(4300)];
        world.Machine.Alive.Add(4300);
        using (var launch = world.Launch()) await world.FirstLookAsync(launch);
        await Assert.That(world.Record.Installs.Single().HeldBy).IsEqualTo("pid:4300");
        var cutover = world.Record.CutoverUtc;

        var hooks = Path.Combine(world.Home, ".codex", "hooks.json");
        world.Machine.Processes = [];
        world.Machine.Alive.Clear();
        world.Machine.Sites = [new ReferenceSite(hooks, $$"""{"command":"{{world.Install.CliBinary}} hook"}""", true)];
        world.Time.Advance(TimeSpan.FromHours(2));

        using (var next = world.Launch()) await world.FirstLookAsync(next);

        var held = world.Record.Installs.Single();
        await Assert.That(held.State).IsEqualTo(InstallState.Pending);
        await Assert.That(held.HeldBy).IsEqualTo($"site:{hooks}");
        await Assert.That(Directory.Exists(world.Root)).IsTrue();
        await Assert.That(Directory.Exists(world.Platform)).IsTrue();
        await Assert.That(world.Refresh.Calls).IsEqualTo(2);
        await Assert.That(world.Record.CutoverUtc).IsEqualTo(cutover);
        await Assert.That(world.Notifier.Notified.Count).IsEqualTo(1);
    }

    /// <summary>A site that cannot be read may name nothing, so it never reruns the refresh; it still holds the deletion.</summary>
    [Test]
    public async Task An_unreadable_site_reruns_no_refresh_and_holds_the_deletion() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Processes = [world.OldSession(4300)];
        world.Machine.Alive.Add(4300);
        using (var launch = world.Launch()) await world.FirstLookAsync(launch);

        var hooks = Path.Combine(world.Home, ".codex", "hooks.json");
        world.Machine.Processes = [];
        world.Machine.Alive.Clear();
        world.Machine.Sites = [new ReferenceSite(hooks, null, false)];
        using var next = world.Launch();
        await world.FirstLookAsync(next);
        await world.RecheckAsync(next);

        await Assert.That(world.Refresh.Calls).IsEqualTo(1);
        await Assert.That(world.Record.Installs.Single().HeldBy).IsEqualTo($"site:{hooks}");
        await Assert.That(Directory.Exists(world.Root)).IsTrue();
    }

    /// <summary>A reference the refresh cannot repoint reruns it once per launch, never on a recheck, and never moves
    /// the cutover: once the user removes it, an agent the app's daemon resumed after the first cutover does not hold
    /// the deletion.</summary>
    [Test]
    public async Task A_site_the_refresh_cannot_repoint_reruns_it_once_per_launch_and_keeps_the_cutover() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Processes = [world.OldSession(4300)];
        world.Machine.Alive.Add(4300);
        using (var launch = world.Launch()) await world.FirstLookAsync(launch);
        var cutover = world.Record.CutoverUtc!.Value;

        var hooks = Path.Combine(world.Home, ".codex", "hooks.json");
        world.Machine.Alive.Clear();
        world.Machine.Processes = [new ProcessFact(4400, "/opt/homebrew/bin/codex", null, cutover.AddMinutes(30))];
        world.Machine.Sites = [new ReferenceSite(hooks, $$"""{"command":"{{world.Install.CliBinary}} hook"}""", true)];
        world.Time.Advance(TimeSpan.FromHours(1));
        using (var next = world.Launch()) {
            await world.FirstLookAsync(next);
            await world.RecheckAsync(next);
            await world.RecheckAsync(next);
        }

        await Assert.That(world.Refresh.Calls).IsEqualTo(2);
        await Assert.That(world.Record.Installs.Single().HeldBy).IsEqualTo($"site:{hooks}");

        using var last = world.Launch();
        await world.FirstLookAsync(last);

        await Assert.That(world.Refresh.Calls).IsEqualTo(3);
        await Assert.That(world.Record.CutoverUtc).IsEqualTo(cutover);

        world.Machine.Sites = [];
        await world.RecheckAsync(last);

        await Assert.That(world.Refresh.Calls).IsEqualTo(3);
        await Assert.That(Directory.Exists(world.Root)).IsFalse();
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
    }

    /// <summary>A reference the refresh does repoint moves the cutover to that refresh's finish.</summary>
    [Test]
    public async Task A_site_the_refresh_repoints_moves_the_cutover() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Processes = [world.OldSession(4300)];
        world.Machine.Alive.Add(4300);
        using (var launch = world.Launch()) await world.FirstLookAsync(launch);

        world.Machine.Alive.Clear();
        world.Machine.Processes = [];
        world.Machine.Sites = [new ReferenceSite(Path.Combine(world.Home, ".codex", "hooks.json"), world.Install.CliBinary, true)];
        world.Refresh.Rewrites = () => world.Machine.Sites = [];
        world.Time.Advance(TimeSpan.FromHours(1));
        using (var next = world.Launch()) await world.FirstLookAsync(next);

        await Assert.That(world.Refresh.Calls).IsEqualTo(2);
        await Assert.That(world.Record.CutoverUtc).IsEqualTo(world.Time.GetUtcNow().UtcDateTime);
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
    }
}
