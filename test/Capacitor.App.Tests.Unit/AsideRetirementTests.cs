using Capacitor.App.Services;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

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

    [Test]
    public async Task A_crash_right_after_a_rename_is_finished_on_the_next_launch() {
        var config = Config();
        var launcher = Package("kcap", "@kurrent/kcap");
        var crashing = new AsideRetirement(config, Record, step => { if (step == AsideRetirement.Renamed) throw new IOException("crash"); });

        await Assert.That(() => crashing.Retire(launcher, _ => true)).Throws<IOException>();
        await Assert.That(Directory.Exists(launcher)).IsFalse();
        await Assert.That(Directory.GetDirectories(Path.GetDirectoryName(launcher)!).Length).IsEqualTo(1);

        new AsideRetirement(config, Record).FinishInterrupted();

        await Assert.That(Directory.GetFileSystemEntries(Path.GetDirectoryName(launcher)!)).IsEmpty();
        await Assert.That(File.Exists(config.Path(Record))).IsFalse();
    }

    [Test]
    public async Task A_crash_between_two_npm_package_moves_is_finished_on_the_next_launch() {
        var config = Config();
        var launcher = Package("kcap", "@kurrent/kcap");
        var platform = Package("kcap-darwin-arm64", "@kurrent/kcap-darwin-arm64");
        var crashing = new AsideRetirement(config, Record, step => { if (step == AsideRetirement.Renamed) throw new IOException("crash"); });
        await Assert.That(() => crashing.Retire(launcher, _ => true)).Throws<IOException>();

        var next = new AsideRetirement(config, Record);
        next.FinishInterrupted();
        await Assert.That(Directory.Exists(platform)).IsTrue();
        await Assert.That(next.Retire(platform, aside => Names(aside, "@kurrent/kcap-darwin-arm64"))).IsTrue();

        await Assert.That(Directory.GetFileSystemEntries(Path.GetDirectoryName(launcher)!)).IsEmpty();
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
