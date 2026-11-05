using System.Runtime.Versioning;
using Capacitor.App.Services;
using Capacitor.App.Services.CliAdoption;
using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;
using TUnit.Assertions.Enums;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit;

[ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
public class CliAdoptionCoordinatorTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static string? Target(string link) => new FileInfo(link).LinkTarget;

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

        world.Machine.Evidence = _ => Task.FromResult(world.AfterSwitch());
        using (var next = world.Launch()) await world.FirstLookAsync(next);

        await Assert.That(world.Notifier.Notified.Count).IsEqualTo(1);
        await Assert.That(world.Record.NoticesShown).IsEquivalentTo([AdoptionNotices.SwitchedKey(world.Install)]);
        await Assert.That(world.Refresh.Calls).IsEqualTo(1);
    }

    /// <summary>Integrations and PATH move before the daemon, and a deferred daemon move undoes neither: the Mac runs the
    /// app's kcap with the old install still on disk, which works. The next launch moves the daemon, and the service
    /// file that named the install meanwhile is no integration, so the refresh is not run again.</summary>
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

            // Only a launch proves the daemon moved, so a recheck leaves an install that never reached the deletion alone.
            await world.RecheckAsync(launch);

            await Assert.That(world.Machine.ProcessReads).IsEqualTo(0);
            await Assert.That(world.Machine.SiteReads).IsEqualTo(0);
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
        world.Machine.Evidence = _ => {
            var found = world.Found();
            return Task.FromResult(found with {
                Units = [.. found.Units.Select(u => u with { Loaded = false, JobPid = null, LoadedProgram = null })], Daemons = [],
            });
        };
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

        world.Machine.Evidence = _ => Task.FromResult(world.AfterSwitch());
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

    [Test]
    public async Task A_batch_needing_administrator_rights_changes_nothing() {
        var world = new AdoptionWorld(Tmp);
        world.UserCanWrite = folder => !folder.StartsWith(world.Prefix, StringComparison.Ordinal);

        using var launch = world.Launch();
        await world.FirstLookAsync(launch);

        await Assert.That(world.Logged("adoption_held").Single()["reason"]).IsEqualTo(AdoptionPreflight.AdministratorSupportPending);
        await Assert.That(Target(world.Link)).IsEqualTo("../lib/node_modules/@kurrent/kcap/bin/kcap.js");
        await Assert.That(world.PlistProgram()).IsEqualTo(world.Install.DaemonBinary);
        await Assert.That(Directory.Exists(world.Root)).IsTrue();
        await Assert.That(world.RecordWritten).IsFalse();
        await Assert.That(world.Refresh.Calls).IsEqualTo(0);
        await Assert.That(world.Machine.Repoints).IsEqualTo(0);
        await Assert.That(world.AppState.State.ShimOffered).IsFalse();
        await Assert.That(world.Notifier.Notified).IsEmpty();
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
        world.Machine.Evidence = _ => Task.FromResult(world.AfterSwitch());
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
