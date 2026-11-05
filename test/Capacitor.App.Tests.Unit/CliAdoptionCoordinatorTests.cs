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

        using (var launch = world.Launch()) await world.FirstLookAsync(launch);

        await Assert.That(Target(world.Link)).IsEqualTo(world.Bundle.Cli);
        await Assert.That(world.PlistProgram()).IsEqualTo(world.Bundle.Daemon);
        await Assert.That(Directory.Exists(world.Root)).IsFalse();
        await Assert.That(Directory.Exists(world.Platform)).IsFalse();
        await Assert.That(world.Refresh.Calls).IsEqualTo(1);
        await Assert.That(world.Machine.Reattaches).IsEqualTo(1);
        await Assert.That(world.AppState.State.ShimOffered).IsTrue();

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

    /// <summary>A crash between an aside rename and its deletion leaves both names; the next batch finishes it first.</summary>
    [Test]
    public async Task An_interrupted_deletion_is_finished_before_the_batch_runs() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Evidence = _ => Task.FromResult(new AdoptionEvidence([], [], [], world.LoginPath, null));
        var leftover = Directory.CreateDirectory(Path.Combine(world.Prefix, "old-kcap")).FullName;
        var aside = new AsideRetirement(world.Config, "cli-adoption-retire").Plan(leftover).Aside;
        Directory.Move(leftover, aside);

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        await Assert.That(Directory.Exists(aside)).IsFalse();
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
        await Assert.That(world.AppState.State.ShimOffered).IsFalse();
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
            waiting = (world.Machine.EvidenceReads, asides.All(a => Directory.Exists(a.Aside)));
            foreach (var aside in asides) Directory.Delete(aside.Aside, recursive: true);
        };

        using (var launch = world.Launch()) await world.FirstLookAsync(launch);

        await Assert.That(world.Machine.Waits.Single()).IsEqualTo((Helper, (string?)$"tok-{Helper}", AdoptionPrivilegedStep.SettleWait));
        await Assert.That(waiting).IsEqualTo((0, true));
        await Assert.That(world.Record.Installs.Single().State).IsEqualTo(InstallState.Switched);
        await Assert.That(world.Record.Privileged).IsNull();
        await Assert.That(world.Admin.Prompts).IsEmpty();
        await Assert.That(world.Notifier.Notified.Single()).Contains("was removed");
    }

    /// <summary>An aside the helper left after go can only be removed as root, and a helper whose start identity was
    /// never read is waited for the whole bound: the install is left for Finish switching, and the app raises no
    /// prompt of its own.</summary>
    [Test]
    public async Task A_part_deleted_aside_after_go_is_left_DeletionCutShort_without_a_prompt() {
        var world = new AdoptionWorld(Tmp);
        world.RootOwned();
        var asides = world.InterruptedHelper(startToken: null);
        Directory.Delete(asides[0].Aside, recursive: true);
        AdoptionWorld.RootOnly(asides[1].Aside);
        try {
            using var launch = world.Launch();
            await world.FirstLookAsync(launch);

            await Assert.That(world.Machine.Waits.Single()).IsEqualTo((Helper, (string?)null, AdoptionPrivilegedStep.SettleWait));
            var left = world.Record.Installs.Single();
            await Assert.That(left.State).IsEqualTo(InstallState.Left);
            await Assert.That(left.Reason).IsEqualTo(LeftReason.DeletionCutShort);
            await Assert.That(world.Record.Privileged).IsNull();
            await Assert.That(Directory.Exists(asides[1].Aside)).IsTrue();
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
        Directory.Delete(asides[0].Aside, recursive: true);
        AdoptionWorld.RootOnly(asides[1].Aside);
        try {
            using var launch = world.Launch();
            await world.FirstLookAsync(launch);
            await launch.FinishSwitchingAsync().WaitAsync(AdoptionWorld.Bounded);
            await world.SettledAsync(launch);

            var spec = world.Admin.Specs.Single();
            await Assert.That(spec.Links).IsEmpty();
            await Assert.That(spec.Entries.Single(e => e.Entry.Original == asides[1].Original).Entry.Aside).IsEqualTo(asides[1].Aside);
            await Assert.That(Path.Exists(asides[1].Aside)).IsFalse();
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
        world.Admin.Answer = PrivilegedResult.CutShort;
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

    /// <summary>The checks run again once the helper has swapped the link: a daemon started from the install while
    /// the prompt was open keeps it, the helper puts the link back, and nothing is noticed or asked again this launch,
    /// however the holder goes away: not by a recheck, nor by the batch running again for a daemon move it deferred.</summary>
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
    /// record, and a reference rewritten to it between launches both reruns the refresh and keeps its files.</summary>
    [Test]
    public async Task A_reference_recreated_between_launches_holds_the_deletion() {
        var world = new AdoptionWorld(Tmp);
        world.Machine.Processes = [world.OldSession(4300)];
        world.Machine.Alive.Add(4300);
        using (var launch = world.Launch()) await world.FirstLookAsync(launch);
        await Assert.That(world.Record.Installs.Single().HeldBy).IsEqualTo("pid:4300");

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
        await Assert.That(world.Record.CutoverUtc).IsEqualTo(world.Time.GetUtcNow().UtcDateTime);
        await Assert.That(world.Notifier.Notified.Count).IsEqualTo(1);
    }
}
