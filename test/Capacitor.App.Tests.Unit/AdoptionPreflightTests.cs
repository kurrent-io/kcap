using Capacitor.App.Services.CliAdoption;

namespace Capacitor.App.Tests.Unit;

public class AdoptionPreflightTests {
    const string Bundle = "/Applications/Kcap.app/Contents/MacOS/kcap";

    [TempDir] public required TempDir Tmp { get; init; }

    sealed record Layout(OtherCliInstall Install, string Link, string LinkFolder);

    Layout Script(string name, string version = "1.0.0", string? prefix = null) {
        var root = Tmp.CreateDir(name, ".kcap").Path;
        var bin  = Directory.CreateDirectory(Path.Combine(root, "versions", version, "bin")).FullName;
        File.WriteAllText(Path.Combine(bin, "kcap"), "");
        File.WriteAllText(Path.Combine(root, "install.json"), "{}");
        Directory.CreateSymbolicLink(Path.Combine(root, "current"), Path.Combine(root, "versions", version));
        var folder = Directory.CreateDirectory(prefix ?? Path.Combine(Tmp.Path, name, "bin")).FullName;
        var link   = Path.Combine(folder, "kcap");
        File.CreateSymbolicLink(link, Path.Combine(root, "current", "bin", "kcap"));
        var install = new OtherCliInstall(OtherCliInstallKind.Script, root, version, Path.Combine(root, "current", "bin", "kcap"),
            Path.Combine(root, "current", "bin", "kcap-daemon"), "id-" + name,
            [Path.Combine(root, "versions"), Path.Combine(root, "current"), Path.Combine(root, "install.json")]);
        return new Layout(install, link, folder);
    }

    static AdoptionEvidence Evidence(OtherCliInstall[] installs, string? onLogin, string? loginPath = "/usr/bin",
            ServiceUnitFact[]? units = null, RunningDaemonFact[]? daemons = null) =>
        new(installs, units ?? [], daemons ?? [], loginPath, onLogin);

    static AdoptionPlan Plan(AdoptionEvidence e, AdoptionRecord? record = null, string app = "2.0.0", bool system = true,
            Func<string, bool>? canWrite = null, params string[] stable) =>
        AdoptionPreflight.Plan(e, record ?? AdoptionRecord.Empty, app, Bundle, system, canWrite ?? (_ => true), stable);

    static ServiceUnitFact Unit(string name, string? kcapOnPath, bool loaded = false, int? pid = null) =>
        new($"/p/{name}.plist", name, "/x/kcap-daemon", "/usr/bin", kcapOnPath, loaded, pid, null);

    [Test]
    public async Task No_other_install_plans_nothing() {
        var plan = Plan(Evidence([], null));
        await Assert.That(plan.HoldReason).IsNull();
        await Assert.That(plan.Installs.Count).IsEqualTo(0);
        await Assert.That(plan.NeedsStableLink).IsFalse();
    }

    [Test]
    public async Task An_install_newer_than_the_app_holds_the_batch_with_newer_install() {
        var s    = Script("a", "3.0.0");
        var plan = Plan(Evidence([s.Install], s.Link), app: "2.0.0");
        await Assert.That(plan.HoldReason).IsEqualTo("newer_install");
        await Assert.That(plan.Installs.Count).IsEqualTo(1);
    }

    [Test]
    public async Task An_unknown_login_PATH_holds_with_path_unknown() {
        var s    = Script("a");
        var plan = Plan(Evidence([s.Install], null, loginPath: null));
        await Assert.That(plan.HoldReason).IsEqualTo("path_unknown");
    }

    [Test]
    public async Task A_daemon_running_from_the_install_that_is_not_the_job_pid_leaves_it_UnsupervisedDaemon() {
        var s    = Script("a");
        var e    = Evidence([s.Install], s.Link, units: [Unit("u", s.Link, loaded: true, pid: 10)],
            daemons: [new RunningDaemonFact("u", 99, "t", s.Install.DaemonBinary)]);
        var plan = Plan(e, stable: s.LinkFolder);
        await Assert.That(plan.Installs[0].Left).IsEqualTo(LeftReason.UnsupervisedDaemon);
    }

    [Test]
    public async Task A_manual_daemon_beside_an_unloaded_plist_leaves_it_UnsupervisedDaemon() {
        var s    = Script("a");
        var e    = Evidence([s.Install], s.Link, units: [Unit("u", s.Link, loaded: false)],
            daemons: [new RunningDaemonFact("u", 99, "t", s.Install.DaemonBinary)]);
        var plan = Plan(e, stable: s.LinkFolder);
        await Assert.That(plan.Installs[0].Left).IsEqualTo(LeftReason.UnsupervisedDaemon);
    }

    [Test]
    public async Task A_supervised_daemon_does_not_leave_the_install() {
        var s    = Script("a");
        var e    = Evidence([s.Install], s.Link, units: [Unit("u", s.Link, loaded: true, pid: 99)],
            daemons: [new RunningDaemonFact("u", 99, "t", s.Install.DaemonBinary)]);
        var plan = Plan(e, stable: s.LinkFolder);
        await Assert.That(plan.Installs[0].Left).IsNull();
    }

    [Test]
    public async Task A_PATH_entry_naming_the_install_folder_leaves_it_OwnFolderOnPath() {
        var s    = Script("a");
        var own  = Path.Combine(s.Install.Root, "current", "bin", "kcap");
        var plan = Plan(Evidence([s.Install], own));
        await Assert.That(plan.Installs[0].Left).IsEqualTo(LeftReason.OwnFolderOnPath);
    }

    [Test]
    public async Task A_left_install_that_wins_the_login_PATH_does_not_hold_an_eligible_one() {
        var left = Script("a");
        var ok   = Script("b");
        var own  = Path.Combine(left.Install.Root, "current", "bin", "kcap");
        var plan = Plan(Evidence([left.Install, ok.Install], own, units: [Unit("u", ok.Link)]), stable: ok.LinkFolder);
        await Assert.That(plan.HoldReason).IsNull();
        await Assert.That(plan.Installs[0].Left).IsEqualTo(LeftReason.OwnFolderOnPath);
        await Assert.That(plan.Installs[1].Left).IsNull();
        await Assert.That(plan.Installs[1].Links.Single().Path).IsEqualTo(ok.Link);
    }

    [Test]
    public async Task Custom_prefix_and_nvm_links_are_planned_in_place_and_need_a_stable_link() {
        var s    = Script("a", prefix: Path.Combine(Tmp.Path, "nvm", "v1", "bin"));
        var plan = Plan(Evidence([s.Install], s.Link), stable: "/usr/local/bin");
        var link = plan.Installs[0].Links.Single();
        await Assert.That(link.Path).IsEqualTo(s.Link);
        await Assert.That(link.Original).IsEqualTo(Path.Combine(s.Install.Root, "current", "bin", "kcap"));
        await Assert.That(link.Replacement).IsEqualTo(Bundle);
        await Assert.That(plan.NeedsStableLink).IsTrue();
    }

    [Test]
    public async Task A_link_in_a_stable_folder_needs_no_stable_link() {
        var s    = Script("a");
        var plan = Plan(Evidence([s.Install], s.Link), stable: s.LinkFolder);
        await Assert.That(plan.NeedsStableLink).IsFalse();
    }

    [Test]
    public async Task A_root_owned_folder_or_link_needs_administrator() {
        var s     = Script("a");
        var plan  = Plan(Evidence([s.Install], s.Link), canWrite: f => f != s.LinkFolder, stable: s.LinkFolder);
        await Assert.That(plan.Installs[0].NeedsAdministrator).IsTrue();
        var owned = Plan(Evidence([s.Install], s.Link), canWrite: f => f != s.Install.Root, stable: s.LinkFolder);
        await Assert.That(owned.Installs[0].NeedsAdministrator).IsTrue();
    }

    [Test]
    public async Task A_root_owned_install_with_the_app_in_user_Applications_is_left_SharedInstallPrivateApp() {
        var s    = Script("a");
        var plan = Plan(Evidence([s.Install], s.Link), system: false, canWrite: f => f != s.LinkFolder, stable: s.LinkFolder);
        await Assert.That(plan.Installs[0].Left).IsEqualTo(LeftReason.SharedInstallPrivateApp);
        await Assert.That(plan.HoldReason).IsNull();
    }

    [Test]
    public async Task An_install_only_a_service_names_with_no_kcap_on_any_PATH_needs_a_stable_link() {
        var s    = Script("a");
        var plan = Plan(Evidence([s.Install], null, units: [Unit("u", null)]));
        await Assert.That(plan.Installs[0].Links.Count).IsEqualTo(0);
        await Assert.That(plan.NeedsStableLink).IsTrue();
    }

    [Test]
    public async Task After_the_machine_switched_a_new_install_is_reported_not_planned() {
        var s    = Script("a");
        var plan = Plan(Evidence([s.Install], s.Link), AdoptionRecord.Empty with { MachineSwitched = true });
        await Assert.That(plan.Installs.Count).IsEqualTo(0);
        await Assert.That(plan.Reported.Single()).IsEqualTo(s.Install);
        await Assert.That(plan.HoldReason).IsNull();
    }

    [Test]
    public async Task A_recorded_install_whose_identity_changed_is_planned_afresh() {
        var s        = Script("a");
        var recorded = new RecordedInstall(s.Install with { Identity = "old" }, InstallState.Switched, null, null, null, null);
        var plan     = Plan(Evidence([s.Install], s.Link), AdoptionRecord.Empty with { Installs = [recorded] }, stable: s.LinkFolder);
        await Assert.That(plan.Installs.Single().Install.Identity).IsEqualTo(s.Install.Identity);
        await Assert.That(plan.Installs[0].Links.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Until_PR_C_a_batch_needing_administrator_holds() {
        var s    = Script("a");
        var plan = Plan(Evidence([s.Install], s.Link), canWrite: f => f != s.LinkFolder, stable: s.LinkFolder);
        await Assert.That(plan.HoldReason).IsEqualTo("administrator_support_pending");
    }

    [Test]
    public async Task Precedence_is_newer_then_path_unknown_then_administrator() {
        var s    = Script("a", "3.0.0");
        var plan = Plan(Evidence([s.Install], s.Link, loginPath: null), canWrite: _ => false);
        await Assert.That(plan.HoldReason).IsEqualTo("newer_install");
    }
}
