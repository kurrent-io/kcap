using Capacitor.Cli.Core.Harness.Dsh;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Dsh;

public class DshPluginInstallerTests {
    [TempDir] public required TempDir Tmp { get; init; }

    DshPaths Paths => new(new UserHome(Tmp.Path), Tmp.PathTo("dsh-home"));

    [Test]
    public async Task Install_writes_the_plugin_with_its_own_manifest_and_registers_it_in_the_home_patch() {
        var paths = Paths;

        await Assert.That(DshPluginInstaller.Install(paths)).IsTrue();

        await Assert.That(File.ReadAllText(paths.PluginFile)).IsEqualTo(DshPluginInstaller.PluginContent);
        await Assert.That(File.ReadAllText(paths.PluginManifest)).IsEqualTo(DshPluginInstaller.ManifestContent);
        await Assert.That(File.ReadAllText(paths.CordisPatch)).Contains(new Uri(paths.PluginFile).AbsoluteUri);
        await Assert.That(DshPluginInstaller.IsInstalled(paths)).IsTrue();
        await Assert.That(DshPluginInstaller.ReadMarker(paths)).IsEqualTo(CapacitorVersion.Current());
    }

    [Test]
    public async Task Reinstalling_keeps_one_managed_block() {
        var paths = Paths;

        DshPluginInstaller.Install(paths);
        DshPluginInstaller.Install(paths);

        var patch = File.ReadAllText(paths.CordisPatch);
        await Assert.That(patch.Split("kcap-dsh:begin").Length - 1).IsEqualTo(1);
    }

    [Test]
    public async Task Register_keeps_user_entries_and_drops_a_lone_empty_list() {
        var patch = Tmp.CreateFile("cordis.patch.yml", "# mine\n- insert:\n    - id: other\n      name: './other.mjs'\n");
        var empty = Tmp.CreateFile("empty.patch.yml", "# only a comment\n[]\n");

        DshPluginInstaller.Register(patch, Tmp.PathTo("plugin.mjs"));
        DshPluginInstaller.Register(empty, Tmp.PathTo("plugin.mjs"));

        var withUser = File.ReadAllText(patch);
        await Assert.That(withUser).Contains("id: other");
        await Assert.That(withUser).Contains("id: kcap");

        var fromEmpty = File.ReadAllText(empty);
        await Assert.That(fromEmpty).DoesNotContain("[]");
        await Assert.That(fromEmpty).Contains("# only a comment");
    }

    [Test]
    public async Task Unregister_strips_only_the_managed_block() {
        var patch = Tmp.CreateFile("cordis.patch.yml", "- insert:\n    - id: other\n      name: './other.mjs'\n");
        var empty = Tmp.CreateFile("empty.patch.yml", "# only a comment\n[]\n");
        DshPluginInstaller.Register(patch, Tmp.PathTo("plugin.mjs"));
        DshPluginInstaller.Register(empty, Tmp.PathTo("plugin.mjs"));

        await Assert.That(DshPluginInstaller.Unregister(patch)).IsTrue();
        await Assert.That(DshPluginInstaller.Unregister(empty)).IsTrue();

        await Assert.That(File.ReadAllText(patch).Replace("\r\n", "\n")).IsEqualTo("- insert:\n    - id: other\n      name: './other.mjs'\n");
        await Assert.That(File.ReadAllText(empty).Replace("\r\n", "\n")).IsEqualTo("# only a comment\n[]\n");
    }

    [Test]
    public async Task Install_and_remove_clean_up_an_older_per_profile_install() {
        var paths   = Paths;
        var profile = Path.Combine(paths.ProfilesDir, "tui", "cordis.patch.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        File.WriteAllText(paths.LegacyPluginFile, "// old");
        DshPluginInstaller.Register(profile, paths.LegacyPluginFile);

        DshPluginInstaller.Install(paths);

        await Assert.That(File.Exists(paths.LegacyPluginFile)).IsFalse();
        await Assert.That(DshPluginInstaller.IsRegistered(profile)).IsFalse();

        await Assert.That(DshPluginInstaller.Remove(paths)).IsTrue();
        await Assert.That(Directory.Exists(paths.PluginDir)).IsFalse();
        await Assert.That(DshPluginInstaller.IsRegistered(paths.CordisPatch)).IsFalse();
        await Assert.That(DshPluginInstaller.IsInstalled(paths)).IsFalse();
    }
}
