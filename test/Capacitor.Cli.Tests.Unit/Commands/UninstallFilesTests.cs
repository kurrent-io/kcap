using Capacitor.Cli.Commands;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class UninstallFilesTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Removes_config_beside_a_nested_local_root_without_following_unrelated_links() {
        var config = Tmp.CreateDir("config");
        var local = Tmp.CreateDir("config/nested/local");
        Tmp.CreateFile("config/nested/local/install.json", "registry");
        Tmp.CreateFile("config/nested/local/lifecycle.lock");
        Tmp.CreateFile("config/nested/profile.json", "delete");
        var unrelated = Tmp.CreateFile("outside/user-file", "keep");
        Directory.CreateSymbolicLink(Path.Combine(config, "linked"), Tmp.PathTo("outside"));

        UninstallFiles.DeleteConfig(config, local);

        await Assert.That(File.ReadAllText(Path.Combine(local, "install.json"))).IsEqualTo("registry");
        await Assert.That(File.Exists(Tmp.PathTo("config/nested/profile.json"))).IsFalse();
        await Assert.That(File.ReadAllText(unrelated)).IsEqualTo("keep");
    }

    /// <summary>The user-wide record of retired ports keeps a purged port from a later setup, and the unit lock is where
    /// every setup meets: an unlinked lock would let the next one lock a fresh file beside a holder of the old one.</summary>
    [Test]
    public async Task Removes_config_but_keeps_the_record_of_retired_ports_and_the_unit_lock() {
        var config  = Tmp.CreateDir("config").Path;
        var retired = Tmp.CreateFile("config/local-retired.json", "retired");
        var unit    = Tmp.CreateFile("config/local-unit.lock");
        Tmp.CreateFile("config/config.json", "delete");
        Tmp.CreateFile("config/spool/hook.jsonl", "delete");

        UninstallFiles.DeleteConfig(config, localRoot: null, keepFiles: [retired, unit]);

        await Assert.That(File.ReadAllText(retired)).IsEqualTo("retired");
        await Assert.That(File.Exists(unit)).IsTrue();
        await Assert.That(File.Exists(Tmp.PathTo("config/config.json"))).IsFalse();
        await Assert.That(Directory.Exists(Tmp.PathTo("config/spool"))).IsFalse();
    }

    [Test]
    public async Task Removes_the_whole_config_when_no_kept_file_lies_in_it() {
        var config = Tmp.CreateDir("config").Path;
        Tmp.CreateFile("config/config.json", "delete");

        UninstallFiles.DeleteConfig(config, localRoot: null, keepFiles: [Tmp.CreateFile("home/local-retired.json", "retired"), Tmp.PathTo("config/local-unit.lock")]);

        await Assert.That(Directory.Exists(config)).IsFalse();
    }
}
