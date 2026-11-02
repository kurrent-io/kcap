using Capacitor.App.Platform.Windows;
using TUnit.Assertions.Enums;
using static Capacitor.App.Platform.Windows.LegacyRetirementStep;

namespace Capacitor.App.Tests.Unit;

public class LegacyRetirementPlanTests {
    static readonly string Root    = Path.GetFullPath("/local/KurrentCapacitor");
    static readonly string OwnRoot = Path.GetFullPath("/local/kcap");
    static readonly string OwnCli  = Path.Combine(OwnRoot, "current", "bin", "kcap.exe");
    static readonly DateTime Started = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);

    static string In(params string[] segments) => Path.Combine(Root, Path.Combine(segments));

    static LegacyRetirementFacts Facts => new(
        new LegacyWindowsInstall(Root, "1.4.0"), OwnRoot, OwnCli, [], [], [], [], null, []);

    [Test]
    public async Task Every_step_comes_in_order_with_the_updater_stopped_before_the_app() {
        var app     = new RunningImage(12, In("current", VelopackRoot.AppExecutable), Started);
        var updater = new RunningImage(11, In(VelopackRoot.UpdateExe), Started);
        var facts = Facts with {
            Processes        = [app, updater],
            UninstallEntries = [new(UninstallEntry.KcapKey, Root + Path.DirectorySeparatorChar), new("KurrentCapacitor", Root)],
            Shortcuts        = ["Kurrent Capacitor.lnk"],
            Autostart        = ["KurrentCapacitor"],
            LegacyCliOnPath  = In("current", "bin", "kcap.exe"),
        };

        var steps = LegacyRetirementPlan.Build(facts, ownPid: 1);

        await Assert.That(steps.Select(step => step.GetType().Name)).IsEquivalentTo([
            nameof(StopProcesses), nameof(RepointDaemons), nameof(RemoveShortcuts), nameof(RemoveAutostart),
            nameof(RepointUninstallEntry), nameof(MoveUserPath), nameof(RemoveRoot), nameof(RemoveUninstallEntries),
        ], CollectionOrdering.Matching);
        await Assert.That(((StopProcesses)steps[0]).Processes.Select(p => p.Pid)).IsEquivalentTo([11, 12], CollectionOrdering.Matching);
        await Assert.That(steps.OfType<RepointUninstallEntry>().Single()).IsEqualTo(new RepointUninstallEntry(Root, OwnRoot));
        await Assert.That(steps.OfType<MoveUserPath>().Single()).IsEqualTo(new MoveUserPath(In("current", "bin", "kcap.exe"), OwnCli));
        await Assert.That(steps.OfType<RemoveUninstallEntries>().Single().Keys).IsEquivalentTo(["KurrentCapacitor"]);
    }

    /// <summary>The daemon moves through its task, never by being killed, and a hook or MCP server the CLI runs is
    /// left to finish; this process and anything outside the root are never candidates.</summary>
    [Test]
    public async Task Only_the_app_and_updater_inside_the_legacy_root_are_stopped() {
        var facts = Facts with {
            Processes = [
                new RunningImage(20, In("current", "bin", "kcap-daemon.exe"), Started),
                new RunningImage(21, In("current", "bin", "kcap.exe"), Started),
                new RunningImage(22, Path.GetFullPath("/other/Update.exe"), Started),
                new RunningImage(23, Path.Combine(OwnRoot, "current", VelopackRoot.AppExecutable), Started),
                new RunningImage(1, In("current", VelopackRoot.AppExecutable), Started),
            ],
        };

        var steps = LegacyRetirementPlan.Build(facts, ownPid: 1);

        await Assert.That(steps.OfType<StopProcesses>()).IsEmpty();
        await Assert.That(steps[0]).IsEqualTo(new RepointDaemons(Root));
    }

    [Test]
    public async Task A_root_that_holds_the_config_directory_is_kept() {
        var facts = Facts with {
            UninstallEntries = [new(UninstallEntry.KcapKey, Root), new("KurrentCapacitor", Root)],
            KeptPaths        = [Path.GetFullPath("/home/u/.config/kcap"), In("data", "config")],
        };

        var steps = LegacyRetirementPlan.Build(facts, ownPid: 1);

        await Assert.That(steps.OfType<RemoveRoot>()).IsEmpty();
        await Assert.That(steps.OfType<RemoveUninstallEntries>()).IsEmpty();
        await Assert.That(steps.OfType<RepointUninstallEntry>()).IsNotEmpty();
    }

    [Test]
    public async Task A_kcap_entry_that_already_names_this_install_is_left_alone() {
        var facts = Facts with { UninstallEntries = [new(UninstallEntry.KcapKey, OwnRoot), new("Other", null)] };

        var steps = LegacyRetirementPlan.Build(facts, ownPid: 1);

        await Assert.That(steps.Select(step => step.GetType().Name))
            .IsEquivalentTo([nameof(RepointDaemons), nameof(RemoveRoot)], CollectionOrdering.Matching);
    }
}
