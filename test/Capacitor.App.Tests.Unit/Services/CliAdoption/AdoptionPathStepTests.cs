using System.Runtime.Versioning;
using Capacitor.App.Services.CliAdoption;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit.Services.CliAdoption;

[ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
public class AdoptionPathStepTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class Shim(Func<string, ShimResult> install) : ICliPathInstaller {
        public List<string> Installed { get; } = [];
        public string Disclosure => "";
        public ShimPreflight Preflight(string target) => ShimPreflight.Installable;

        public Task<ShimResult> InstallAsync(string target, CancellationToken ct) {
            Installed.Add(target);
            return Task.FromResult(install(target));
        }
    }

    sealed record Built(AdoptionPathStep Step, AdoptionRecordStore Store, AdoptedLinkInventory Inventory, FakeLoginShellProbe Probe, Shim Shim);

    string Bundle(string app = "Kcap.app") => Tmp.CreateExecutable($"Applications/{app}/Contents/MacOS/kcap", "#!/bin/sh\nexit 0\n");

    (OtherCliInstall Install, string Kcap) Script(string home = "home") {
        var kcap = Tmp.CreateExecutable($"{home}/.kcap/current/bin/kcap", "#!/bin/sh\nexit 0\n");
        var root = Tmp.PathTo(home, ".kcap");
        var install = new OtherCliInstall(OtherCliInstallKind.Script, root, "1.0.0", kcap, Path.Combine(root, "current", "bin", "kcap-daemon"),
            "id", [Path.Combine(root, "versions"), Path.Combine(root, "current"), Path.Combine(root, "install.json")]);
        return (install, kcap);
    }

    string Link(string folder, string target) {
        var link = Path.Combine(Tmp.CreateDir(folder), "kcap");
        File.CreateSymbolicLink(link, target);
        return link;
    }

    /// A shim that links <paramref name="link"/> to its target and reports <paramref name="outcome"/>.
    static Func<string, ShimResult> Links(string link, ShimOutcome outcome = ShimOutcome.Installed) => target => {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, target);
        return new ShimResult(outcome, null);
    };

    Built Build(Func<string?> loginKcap, IReadOnlyList<string> stable, Func<string, ShimResult>? shim = null) {
        var config    = new ConfigRoot(Tmp.CreateDir("config"));
        var store     = new AdoptionRecordStore(config);
        var inventory = new AdoptedLinkInventory(config);
        var probe     = new FakeLoginShellProbe { KcapPathFreshBehavior = _ => Task.FromResult(loginKcap()) };
        var fake      = new Shim(shim ?? (_ => new ShimResult(ShimOutcome.Failed, "not expected")));
        return new Built(new AdoptionPathStep(probe, inventory, store, fake, stable), store, inventory, probe, fake);
    }

    static AdoptionPlan Plan(OtherCliInstall install, bool needsStable, params LinkChange[] links) =>
        new(null, [new InstallPlan(install, null, false, links)], needsStable, []);

    static AdoptionEvidence Evidence(OtherCliInstall install, string? onLogin, params ServiceUnitFact[] units) =>
        new([install], units, [], "/usr/bin:/bin", onLogin);

    static ServiceUnitFact Unit(string storedPath, string? kcapOnPath) =>
        new("/p/u.plist", "u", "/x/kcap-daemon", storedPath, kcapOnPath, true, 10, null);

    static string Folder(string link) => Path.GetDirectoryName(link)!;

    static string? Target(string link) => new FileInfo(link).LinkTarget;

    [Test]
    public async Task Each_location_is_replaced_at_its_own_path_and_both_PATHs_resolve_to_the_bundle() {
        var bundle          = Bundle();
        var (install, kcap) = Script();
        var login           = Link("home/.local/bin", kcap);
        var service         = Link("home/bin", kcap);
        var changes         = new[] { new LinkChange(login, kcap, bundle), new LinkChange(service, kcap, bundle) };
        var built           = Build(() => login, [Folder(login), Folder(service)]);

        var applied = await built.Step.ApplyAsync(Plan(install, false, changes),
            Evidence(install, login, Unit($"{Folder(service)}:/usr/bin", service)), bundle, CancellationToken.None);

        await Assert.That(applied).IsTrue();
        await Assert.That(Target(login)).IsEqualTo(bundle);
        await Assert.That(Target(service)).IsEqualTo(bundle);
        await Assert.That(built.Probe.KcapPathForceRefreshCalls).Contains(true);
        await Assert.That(built.Store.Load().Links).IsEquivalentTo(changes);
        await Assert.That(built.Inventory.Links()).IsEmpty();
        await Assert.That(built.Shim.Installed).IsEmpty();
    }

    /// <summary>The changed link stays in the record and the inventory: both are written before a link is made, and
    /// put-back keeps what it did not restore.</summary>
    [Test]
    public async Task A_link_changed_since_preflight_is_left_and_the_step_fails_with_everything_put_back() {
        var bundle          = Bundle();
        var (install, kcap) = Script();
        var first           = Link("home/.local/bin", kcap);
        var changed         = Link("nvm/v20/bin", "/somewhere/else/kcap");
        var refused         = new LinkChange(changed, kcap, bundle);
        var built           = Build(() => first, [Folder(first)], Links(Tmp.PathTo("home/.local/bin/kcap-stable")));

        var applied = await built.Step.ApplyAsync(Plan(install, true, new LinkChange(first, kcap, bundle), refused),
            Evidence(install, first), bundle, CancellationToken.None);

        await Assert.That(applied).IsFalse();
        await Assert.That(Target(first)).IsEqualTo(kcap);
        await Assert.That(Target(changed)).IsEqualTo("/somewhere/else/kcap");
        await Assert.That(built.Store.Load().Links).IsEquivalentTo([refused]);
        await Assert.That(built.Inventory.Links()).IsEquivalentTo([changed]);
        await Assert.That(built.Shim.Installed).IsEmpty();
    }

    /// <summary>The first attempt cannot write the nvm folder, yet the inventory already holds the link: an entry is
    /// added before its link is made, never after.</summary>
    [Test]
    public async Task An_nvm_location_keeps_its_replacement_gains_a_stable_link_and_enters_the_inventory_first() {
        var bundle          = Bundle();
        var (install, kcap) = Script();
        var nvm             = Link("home/.nvm/versions/node/v20/bin", kcap);
        var stable          = Tmp.PathTo("home/.local/bin/kcap");
        var change          = new LinkChange(nvm, kcap, bundle);
        var built           = Build(() => nvm, [Folder(stable)], Links(stable, ShimOutcome.InstalledButNotOnPath));

        File.SetUnixFileMode(Folder(nvm), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        bool refused;
        try {
            refused = !await built.Step.ApplyAsync(Plan(install, true, change), Evidence(install, nvm), bundle, CancellationToken.None);
        } finally {
            File.SetUnixFileMode(Folder(nvm), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        await Assert.That(refused).IsTrue();
        await Assert.That(Target(nvm)).IsEqualTo(kcap);
        await Assert.That(built.Inventory.Links()).IsEquivalentTo([nvm]);

        var applied = await built.Step.ApplyAsync(Plan(install, true, change), Evidence(install, nvm), bundle, CancellationToken.None);

        await Assert.That(applied).IsTrue();
        await Assert.That(Target(nvm)).IsEqualTo(bundle);
        await Assert.That(built.Shim.Installed).IsEquivalentTo([bundle]);
        await Assert.That(Target(stable)).IsEqualTo(bundle);
        await Assert.That(built.Inventory.Links()).IsEquivalentTo([nvm]);
        await Assert.That(built.Store.Load().Links).IsEquivalentTo([change]);
    }

    [Test]
    public async Task A_service_only_install_gets_a_silent_stable_link() {
        var bundle      = Bundle();
        var (install, _) = Script();
        var stable      = Tmp.PathTo("home/.local/bin/kcap");
        var built       = Build(() => File.Exists(stable) ? stable : null, [Folder(stable)], Links(stable));

        var applied = await built.Step.ApplyAsync(Plan(install, true), Evidence(install, null, Unit("/usr/bin:/bin", null)), bundle,
            CancellationToken.None);

        await Assert.That(applied).IsTrue();
        await Assert.That(built.Shim.Installed).IsEquivalentTo([bundle]);
        await Assert.That(Target(stable)).IsEqualTo(bundle);
        await Assert.That(built.Store.Load().Links).IsEmpty();
    }

    [Test]
    public async Task A_login_PATH_that_found_no_kcap_must_find_the_bundle_afterwards() {
        var bundle       = Bundle();
        var (install, _) = Script();
        var stable       = Tmp.PathTo("home/.local/bin/kcap");
        var built        = Build(() => null, [Folder(stable)], Links(stable, ShimOutcome.InstalledButNotOnPath));

        var applied = await built.Step.ApplyAsync(Plan(install, true), Evidence(install, null), bundle, CancellationToken.None);

        await Assert.That(applied).IsFalse();
    }

    [Test]
    public async Task A_failing_shim_or_profile_write_puts_back_and_fails() {
        var bundle          = Bundle();
        var (install, kcap) = Script();
        var nvm             = Link("home/.nvm/versions/node/v20/bin", kcap);
        var built           = Build(() => nvm, [Tmp.PathTo("home/.local/bin")],
            _ => new ShimResult(ShimOutcome.Failed, "The shell profile could not be configured."));

        var applied = await built.Step.ApplyAsync(Plan(install, true, new LinkChange(nvm, kcap, bundle)), Evidence(install, nvm), bundle,
            CancellationToken.None);

        await Assert.That(applied).IsFalse();
        await Assert.That(built.Shim.Installed).IsEquivalentTo([bundle]);
        await Assert.That(Target(nvm)).IsEqualTo(kcap);
        await Assert.That(built.Store.Load().Links).IsEmpty();
        await Assert.That(built.Inventory.Links()).IsEmpty();
    }

    [Test]
    public async Task A_unit_PATH_without_local_bin_still_resolves_through_the_replaced_location() {
        var bundle          = Bundle();
        var (install, kcap) = Script();
        var nvm             = Link("home/.nvm/versions/node/v20/bin", kcap);
        var stable          = Tmp.PathTo("home/.local/bin/kcap");
        var built           = Build(() => nvm, [Folder(stable)], Links(stable, ShimOutcome.InstalledButNotOnPath));

        var applied = await built.Step.ApplyAsync(Plan(install, true, new LinkChange(nvm, kcap, bundle)),
            Evidence(install, nvm, Unit($"{Folder(nvm)}:/usr/bin:/bin", nvm)), bundle, CancellationToken.None);

        await Assert.That(applied).IsTrue();
        await Assert.That(Target(nvm)).IsEqualTo(bundle);
    }

    /// <summary>A fresh login shell finding the bundle is not enough on its own: the service's PATH is checked too.</summary>
    [Test]
    public async Task A_unit_PATH_that_finds_another_kcap_first_puts_everything_back() {
        var bundle          = Bundle();
        var (install, kcap) = Script();
        var login           = Link("home/.local/bin", kcap);
        var service         = Link("opt/kcap/bin", kcap);
        var earlier         = Tmp.CreateExecutable("usr-local/bin/kcap", "#!/bin/sh\nexit 0\n");
        var built           = Build(() => login, [Folder(login), Folder(service)]);

        var applied = await built.Step.ApplyAsync(
            Plan(install, false, new LinkChange(login, kcap, bundle), new LinkChange(service, kcap, bundle)),
            Evidence(install, login, Unit($"{Folder(earlier)}:{Folder(service)}", service)), bundle, CancellationToken.None);

        await Assert.That(applied).IsFalse();
        await Assert.That(Target(login)).IsEqualTo(kcap);
        await Assert.That(Target(service)).IsEqualTo(kcap);
    }

    [Test]
    public async Task A_home_with_a_space_swaps_cleanly() {
        var bundle          = Bundle("Kcap Beta.app");
        var (install, kcap) = Script("my home");
        var login           = Link("my home/.local/bin", kcap);
        var built           = Build(() => login, [Folder(login)]);

        var applied = await built.Step.ApplyAsync(Plan(install, false, new LinkChange(login, kcap, bundle)), Evidence(install, login), bundle,
            CancellationToken.None);

        await Assert.That(applied).IsTrue();
        await Assert.That(Target(login)).IsEqualTo(bundle);
        await Assert.That(Directory.GetFileSystemEntries(Folder(login))).IsEquivalentTo([login]);
    }

    [Test]
    public async Task A_held_plan_changes_nothing() {
        var bundle          = Bundle();
        var (install, kcap) = Script();
        var login           = Link("home/.local/bin", kcap);
        var built           = Build(() => login, [Folder(login)]);
        var held            = Plan(install, true, new LinkChange(login, kcap, bundle)) with { HoldReason = AdoptionPreflight.NewerInstall };

        var applied = await built.Step.ApplyAsync(held, Evidence(install, login), bundle, CancellationToken.None);

        await Assert.That(applied).IsFalse();
        await Assert.That(Target(login)).IsEqualTo(kcap);
        await Assert.That(built.Store.Load().Links).IsEmpty();
        await Assert.That(built.Shim.Installed).IsEmpty();
    }

    [Test]
    public async Task Left_and_administrator_installs_keep_their_links() {
        var bundle         = Bundle();
        var (left, leftKcap) = Script("left");
        var (root, rootKcap) = Script("root");
        var leftLink       = Link("left/.local/bin", leftKcap);
        var rootLink       = Link("root/.local/bin", rootKcap);
        var built          = Build(() => null, [Folder(leftLink), Folder(rootLink)]);
        var plan = new AdoptionPlan(null, [
            new InstallPlan(left, LeftReason.UnsupervisedDaemon, false, [new LinkChange(leftLink, leftKcap, bundle)]),
            new InstallPlan(root, null, true, [new LinkChange(rootLink, rootKcap, bundle)]),
        ], false, []);

        var applied = await built.Step.ApplyAsync(plan, new AdoptionEvidence([left, root], [], [], "/usr/bin", leftLink), bundle,
            CancellationToken.None);

        await Assert.That(applied).IsTrue();
        await Assert.That(Target(leftLink)).IsEqualTo(leftKcap);
        await Assert.That(Target(rootLink)).IsEqualTo(rootKcap);
        await Assert.That(built.Store.Load().Links).IsEmpty();
    }

    /// <summary>The record also holds an earlier attempt's links, which may belong to an install already switched and
    /// deleted: a failed attempt puts back only the links it made itself.</summary>
    [Test]
    public async Task A_failed_attempt_leaves_a_link_an_earlier_attempt_made() {
        var bundle                 = Bundle();
        var (_, earlierKcap)       = Script("earlier");
        var (install, kcap)        = Script();
        var done                   = Link("earlier/.local/bin", bundle);
        var login                  = Link("home/.local/bin", kcap);
        var earlier                = new LinkChange(done, earlierKcap, bundle);
        var built                  = Build(() => null, [Folder(done), Folder(login)]);
        built.Store.Update(r => r with { Links = [earlier] });

        var applied = await built.Step.ApplyAsync(Plan(install, false, new LinkChange(login, kcap, bundle)), Evidence(install, login), bundle,
            CancellationToken.None);

        await Assert.That(applied).IsFalse();
        await Assert.That(Target(login)).IsEqualTo(kcap);
        await Assert.That(Target(done)).IsEqualTo(bundle);
        await Assert.That(built.Store.Load().Links).IsEquivalentTo([earlier]);
    }

    /// <summary>Putting a link back to a deleted install would leave a dangling <c>kcap</c> on PATH. A relative old
    /// target is read from the link's own folder.</summary>
    [Test]
    public async Task Put_back_skips_a_link_whose_old_target_is_gone_and_reads_a_relative_one_from_its_folder() {
        var bundle    = Bundle();
        var (_, kcap) = Script();
        var gone      = Link("gone/bin", bundle);
        var relative  = Link("home/bin", bundle);
        var original  = Path.GetRelativePath(Folder(relative), kcap);
        var missing   = new LinkChange(gone, Tmp.PathTo("deleted/.kcap/current/bin/kcap"), bundle);
        var built     = Build(() => null, [Folder(gone), Folder(relative)]);
        built.Store.Update(r => r with { Links = [missing, new LinkChange(relative, original, bundle)] });

        await built.Step.PutBackAsync(CancellationToken.None);

        await Assert.That(Target(gone)).IsEqualTo(bundle);
        await Assert.That(Target(relative)).IsEqualTo(original);
        await Assert.That(built.Store.Load().Links).IsEquivalentTo([missing]);
    }

    /// <summary>A link that no longer points at the bundle it was given, such as one a bundle move carried along, is
    /// not the switch's to restore.</summary>
    [Test]
    public async Task Put_back_restores_only_a_link_still_pointing_at_its_replacement_and_can_run_again() {
        var bundle          = Bundle();
        var (install, kcap) = Script();
        var ours            = Link("home/.local/bin", bundle);
        var moved           = Link("home/bin", "/Applications/Moved.app/Contents/MacOS/kcap");
        var built           = Build(() => null, [Folder(ours), Folder(moved)]);
        built.Store.Update(r => r with { Links = [new LinkChange(ours, kcap, bundle), new LinkChange(moved, kcap, bundle)] });

        await built.Step.PutBackAsync(CancellationToken.None);
        await built.Step.PutBackAsync(CancellationToken.None);

        await Assert.That(Target(ours)).IsEqualTo(kcap);
        await Assert.That(Target(moved)).IsEqualTo("/Applications/Moved.app/Contents/MacOS/kcap");
        await Assert.That(built.Store.Load().Links).IsEquivalentTo([new LinkChange(moved, kcap, bundle)]);
    }
}
