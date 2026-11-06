using Capacitor.App.Services.CliAdoption;
using Capacitor.Cli.Core.Install;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit;

/// <summary>The check each renamed entry passes before it is deleted: a tree put in its place since the identity
/// check must fail it.</summary>
public class AdoptionDeletionTests {
    [TempDir] public required TempDir Tmp { get; init; }

    (OtherCliInstall Install, ScriptInstallLayout Layout) Script(string version = "1.2.3") {
        var layout = new ScriptInstallLayout(Tmp.CreateDir("kcap"));
        Tmp.CreateDir("kcap", "versions", version, "bin");
        File.CreateSymbolicLink(layout.Current, layout.VersionDir(version));
        File.WriteAllText(layout.Marker, "{}");
        var bin = Path.Combine(layout.VersionDir(version), "bin");
        return (new OtherCliInstall(OtherCliInstallKind.Script, layout.Root, version, Path.Combine(bin, "kcap"),
            Path.Combine(bin, "kcap-daemon"), "", [layout.Versions, layout.Current, layout.Marker]), layout);
    }

    static string Aside(string entry) {
        var aside = entry + ".retired-0123";
        if (new FileInfo(entry).LinkTarget is { } target) {
            File.CreateSymbolicLink(aside, target);
            File.Delete(entry);
        } else if (Directory.Exists(entry)) Directory.Move(entry, aside);
        else File.Move(entry, aside);
        return aside;
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_script_install_still_at_its_version_passes() {
        var (install, layout) = Script();

        await Assert.That(AdoptionDeletion.StillOurs(install, layout.Versions, Aside(layout.Versions))).IsTrue();
        await Assert.That(AdoptionDeletion.StillOurs(install, layout.Current, Aside(layout.Current))).IsTrue();
        await Assert.That(AdoptionDeletion.StillOurs(install, layout.Marker, Aside(layout.Marker))).IsTrue();
    }

    /// <summary>A reinstall adds its own version folder beside the old one and moves <c>current</c> to it.</summary>
    [Test, ExcludeOn(OS.Windows)]
    public async Task A_script_install_reinstalled_since_the_check_fails() {
        var (install, layout) = Script();
        Tmp.CreateDir("kcap", "versions", "2.0.0", "bin");
        File.Delete(layout.Current);
        File.CreateSymbolicLink(layout.Current, layout.VersionDir("2.0.0"));

        await Assert.That(AdoptionDeletion.StillOurs(install, layout.Versions, Aside(layout.Versions))).IsFalse();
        await Assert.That(AdoptionDeletion.StillOurs(install, layout.Current, Aside(layout.Current))).IsFalse();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_versions_folder_without_the_recorded_version_fails() {
        var (install, layout) = Script();
        Directory.Delete(layout.VersionDir("1.2.3"), recursive: true);

        await Assert.That(AdoptionDeletion.StillOurs(install, layout.Versions, Aside(layout.Versions))).IsFalse();
    }
}
