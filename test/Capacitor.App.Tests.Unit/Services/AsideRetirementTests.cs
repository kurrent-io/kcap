using Capacitor.App.Services;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit.Services;

public class AsideRetirementTests {
    const string Record = "cli-adoption-retire";

    [TempDir] public required TempDir Tmp { get; init; }

    ConfigRoot Config() => new(Tmp.CreateDir("config"));

    string Package(string name, string packageName) {
        var dir = Tmp.CreateDir("prefix", "lib", "node_modules", "@kurrent", name);
        File.WriteAllText(Path.Combine(dir, "package.json"), $"{{\"name\":\"{packageName}\"}}");
        return dir;
    }

    static bool Names(string aside, string packageName) =>
        File.Exists(Path.Combine(aside, "package.json")) && File.ReadAllText(Path.Combine(aside, "package.json")).Contains(packageName);

    [Test]
    public async Task Npm_package_folders_are_each_recorded_moved_checked_and_deleted() {
        var config = Config();
        var launcher = Package("kcap", "@kurrent/kcap");
        var platform = Package("kcap-darwin-arm64", "@kurrent/kcap-darwin-arm64");
        var sibling = Package("other", "@kurrent/other");
        var seen = new List<string>();
        var retirement = new AsideRetirement(config, Record);

        var first = retirement.Retire(launcher, aside => { seen.Add(aside); return Names(aside, "@kurrent/kcap"); });
        var second = retirement.Retire(platform, aside => { seen.Add(aside); return Names(aside, "@kurrent/kcap-darwin-arm64"); });

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsTrue();
        await Assert.That(seen.Count).IsEqualTo(2);
        await Assert.That(seen.All(a => a.Contains(".retired-"))).IsTrue();
        await Assert.That(Directory.Exists(launcher)).IsFalse();
        await Assert.That(Directory.Exists(platform)).IsFalse();
        await Assert.That(Directory.GetFileSystemEntries(Path.GetDirectoryName(launcher)!).Length).IsEqualTo(1);
        await Assert.That(Directory.Exists(sibling)).IsTrue();
        await Assert.That(File.Exists(config.Path(Record))).IsFalse();
    }

    [Test]
    public async Task A_script_root_keeps_everything_but_versions_current_and_install_json() {
        var config = Config();
        var root = Tmp.CreateDir("kcap-root");
        var version = Directory.CreateDirectory(Path.Combine(root, "versions", "1.0.0")).FullName;
        File.WriteAllText(Path.Combine(version, "kcap"), "exe");
        var current = Path.Combine(root, "current");
        File.CreateSymbolicLink(current, version);
        var install = Path.Combine(root, "install.json");
        File.WriteAllText(install, "{}");
        File.WriteAllText(Path.Combine(root, "keep.txt"), "mine");
        var retirement = new AsideRetirement(config, Record);

        var link = retirement.Retire(current, aside => new FileInfo(aside).LinkTarget is not null);
        await Assert.That(link).IsTrue();
        await Assert.That(File.Exists(Path.Combine(version, "kcap"))).IsTrue();
        await Assert.That(retirement.Retire(Path.Combine(root, "versions"), Directory.Exists)).IsTrue();
        await Assert.That(retirement.Retire(install, File.Exists)).IsTrue();

        await Assert.That(Directory.GetFileSystemEntries(root).Select(p => Path.GetFileName(p)!).ToArray()).IsEquivalentTo(["keep.txt"]);
    }

    /// <summary>What was renamed may be a tree that replaced the one checked before the rename, so it is never
    /// deleted unchecked: it goes back for the next retirement to check.</summary>
    [Test]
    public async Task A_crash_right_after_a_rename_puts_the_unchecked_entry_back() {
        var config = Config();
        var launcher = Package("kcap", "@kurrent/kcap");
        var crashing = new AsideRetirement(config, Record, step => { if (step == AsideRetirement.Renamed) throw new IOException("crash"); });

        await Assert.That(() => crashing.Retire(launcher, _ => true)).Throws<IOException>();
        await Assert.That(Directory.Exists(launcher)).IsFalse();

        var unfinished = new AsideRetirement(config, Record).FinishInterrupted();

        await Assert.That(unfinished).IsEmpty();
        await Assert.That(Directory.GetFileSystemEntries(Path.GetDirectoryName(launcher)!)).IsEquivalentTo([launcher]);
        await Assert.That(File.Exists(config.Path(Record))).IsFalse();
    }

    [Test]
    public async Task A_crash_after_the_check_is_finished_on_the_next_launch() {
        var config = Config();
        var launcher = Package("kcap", "@kurrent/kcap");
        var crashing = new AsideRetirement(config, Record, step => { if (step == AsideRetirement.Checked) throw new IOException("crash"); });

        await Assert.That(() => crashing.Retire(launcher, _ => true)).Throws<IOException>();
        await Assert.That(Directory.GetDirectories(Path.GetDirectoryName(launcher)!).Length).IsEqualTo(1);

        new AsideRetirement(config, Record).FinishInterrupted();

        await Assert.That(Directory.GetFileSystemEntries(Path.GetDirectoryName(launcher)!)).IsEmpty();
        await Assert.That(File.Exists(config.Path(Record))).IsFalse();
    }

    /// <summary>A link moves as a copy and then the original's removal; a crash between them leaves the same link under
    /// both names, which is a move that never finished, not a replacement.</summary>
    [Test]
    public async Task A_link_move_cut_short_leaves_the_original_and_drops_the_copy() {
        var config = Config();
        var root = Tmp.CreateDir("kcap-root");
        var version = Directory.CreateDirectory(Path.Combine(root, "versions", "1.0.0")).FullName;
        var current = Path.Combine(root, "current");
        File.CreateSymbolicLink(current, version);
        var crashing = new AsideRetirement(config, Record, step => { if (step == AsideRetirement.Renamed) throw new IOException("crash"); });
        await Assert.That(() => crashing.Retire(current, _ => true)).Throws<IOException>();
        File.CreateSymbolicLink(current, version);

        var unfinished = new AsideRetirement(config, Record).FinishInterrupted();

        await Assert.That(unfinished).IsEmpty();
        await Assert.That(new FileInfo(current).LinkTarget).IsEqualTo(version);
        await Assert.That(Directory.GetFileSystemEntries(root).Select(p => Path.GetFileName(p)!).ToArray()).IsEquivalentTo(["versions", "current"]);
        await Assert.That(File.Exists(config.Path(Record))).IsFalse();
    }

    /// <summary>With both names taken nothing can go back, and nothing unchecked is deleted: the aside stays recorded.</summary>
    [Test]
    public async Task An_unchecked_aside_whose_original_came_back_is_kept() {
        var config = Config();
        var launcher = Package("kcap", "@kurrent/kcap");
        var crashing = new AsideRetirement(config, Record, step => { if (step == AsideRetirement.Renamed) throw new IOException("crash"); });
        await Assert.That(() => crashing.Retire(launcher, _ => true)).Throws<IOException>();
        Package("kcap", "@kurrent/kcap");

        var unfinished = new AsideRetirement(config, Record).FinishInterrupted();

        await Assert.That(unfinished).IsEquivalentTo([launcher]);
        await Assert.That(Directory.GetDirectories(Path.GetDirectoryName(launcher)!).Length).IsEqualTo(2);
        await Assert.That(File.Exists(config.Path(Record))).IsTrue();
    }

    [Test]
    public async Task A_crash_between_two_npm_package_moves_is_finished_on_the_next_launch() {
        var config = Config();
        var launcher = Package("kcap", "@kurrent/kcap");
        var platform = Package("kcap-darwin-arm64", "@kurrent/kcap-darwin-arm64");
        var crashing = new AsideRetirement(config, Record, step => { if (step == AsideRetirement.Checked) throw new IOException("crash"); });
        await Assert.That(() => crashing.Retire(launcher, _ => true)).Throws<IOException>();

        var next = new AsideRetirement(config, Record);
        next.FinishInterrupted();
        await Assert.That(Directory.Exists(platform)).IsTrue();
        await Assert.That(next.Retire(platform, aside => Names(aside, "@kurrent/kcap-darwin-arm64"))).IsTrue();

        await Assert.That(Directory.GetFileSystemEntries(Path.GetDirectoryName(launcher)!)).IsEmpty();
    }

    /// <summary>The privileged helper renames what it checked to the checked name; anything it set aside under the
    /// planned name it never checked.</summary>
    [Test]
    public async Task A_helper_aside_is_deleted_only_once_it_carries_the_checked_name() {
        var config = Config();
        var launcher = Package("kcap", "@kurrent/kcap");
        var platform = Package("kcap-darwin-arm64", "@kurrent/kcap-darwin-arm64");
        var retirement = new AsideRetirement(config, Record);
        var checkedAside = AsideRetirement.CheckedAside(retirement.Plan(launcher).Aside);
        var uncheckedAside = retirement.Plan(platform).Aside;
        Directory.Move(launcher, checkedAside);
        Directory.Move(platform, uncheckedAside);

        var unfinished = retirement.FinishInterrupted();

        await Assert.That(unfinished).IsEmpty();
        await Assert.That(Directory.GetFileSystemEntries(Path.GetDirectoryName(launcher)!)).IsEquivalentTo([platform]);
        await Assert.That(Names(platform, "@kurrent/kcap-darwin-arm64")).IsTrue();
        await Assert.That(File.Exists(config.Path(Record))).IsFalse();
    }

    /// <summary>With the original back, an unchecked helper aside can go nowhere: it waits, recorded, for the helper to
    /// check it.</summary>
    [Test]
    public async Task An_unchecked_helper_aside_whose_original_came_back_is_kept() {
        var config = Config();
        var launcher = Package("kcap", "@kurrent/kcap");
        var retirement = new AsideRetirement(config, Record);
        var uncheckedAside = retirement.Plan(launcher).Aside;
        Directory.Move(launcher, uncheckedAside);
        Package("kcap", "@kurrent/kcap");

        var unfinished = retirement.FinishInterrupted();

        await Assert.That(unfinished).IsEquivalentTo([launcher]);
        await Assert.That(Directory.Exists(uncheckedAside)).IsTrue();
        await Assert.That(retirement.HasAside(launcher)).IsTrue();
    }

    [Test]
    public async Task A_planned_entry_that_was_never_renamed_is_left_in_place() {
        var config = Config();
        var launcher = Package("kcap", "@kurrent/kcap");
        var retirement = new AsideRetirement(config, Record);

        var plan = retirement.Plan(launcher);
        await Assert.That(plan.Original).IsEqualTo(launcher);
        await Assert.That(Directory.Exists(launcher)).IsTrue();
        retirement.FinishInterrupted();

        await Assert.That(Directory.Exists(launcher)).IsTrue();
        await Assert.That(retirement.Plan(launcher)).IsEqualTo(retirement.Plan(launcher));
    }

    [Test]
    public async Task A_tree_replaced_between_check_and_rename_is_moved_back() {
        var config = Config();
        var launcher = Package("kcap", "@kurrent/kcap");
        var retirement = new AsideRetirement(config, Record);

        var retired = retirement.Retire(launcher, _ => false);

        await Assert.That(retired).IsFalse();
        await Assert.That(File.ReadAllText(Path.Combine(launcher, "package.json"))).Contains("@kurrent/kcap");
        await Assert.That(Directory.GetFileSystemEntries(Path.GetDirectoryName(launcher)!).Length).IsEqualTo(1);
        await Assert.That(File.Exists(config.Path(Record))).IsFalse();
    }

    [Test]
    public async Task A_recorded_aside_that_is_a_link_or_lacks_the_suffix_is_never_deleted() {
        var config = Config();
        var launcher = Package("kcap", "@kurrent/kcap");
        var precious = Tmp.CreateDir("precious");
        File.WriteAllText(Path.Combine(precious, "data"), "keep");
        var retirement = new AsideRetirement(config, Record);

        var linkAside = retirement.Plan(launcher).Aside;
        File.CreateSymbolicLink(linkAside, precious);
        retirement.FinishInterrupted();
        await Assert.That(File.Exists(Path.Combine(precious, "data"))).IsTrue();

        var elsewhere = Tmp.CreateDir("unrelated");
        File.WriteAllText(Path.Combine(elsewhere, "data"), "keep");
        File.WriteAllText(config.Path(Record), string.Join('\n', launcher, elsewhere, "d") + "\n");
        retirement.FinishInterrupted();

        await Assert.That(File.Exists(Path.Combine(elsewhere, "data"))).IsTrue();
        await Assert.That(Directory.Exists(launcher)).IsTrue();
    }
}
