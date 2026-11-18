using Capacitor.App.Platform.Windows;

namespace Capacitor.App.Tests.Unit.Platform.Windows;

/// Which Kurrent Capacitor root the kcap install retires, and when a legacy copy hands its launch to kcap instead.
public class LegacyWindowsInstallTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string Local => Tmp.PathTo("Local");

    string Install(string folder, string executable = VelopackRoot.AppExecutable, string? version = null) {
        Tmp.CreateFile($"Local/{folder}/{VelopackRoot.UpdateExe}");
        Tmp.CreateFile($"Local/{folder}/current/{executable}");
        if (version is not null)
            Tmp.CreateFile($"Local/{folder}/current/sq.version",
                $"""<?xml version="1.0" encoding="utf-8"?><package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd"><metadata><id>kcap</id><version>{version}</version></metadata></package>""");
        return Tmp.PathTo("Local", folder);
    }

    /// <summary>A legacy root updates itself into the kcap package in place, so its app carries the new name.</summary>
    [Test]
    public async Task A_legacy_root_beside_the_kcap_install_is_found() {
        var own    = Install(LegacyWindowsInstall.KcapFolderName);
        var legacy = Install(LegacyWindowsInstall.FolderName);

        var found = LegacyWindowsInstall.Find(own, "1.5.0", Local, []);

        await Assert.That(found?.Root).IsEqualTo(LegacyPaths.Normalize(legacy));
    }

    [Test]
    public async Task A_legacy_root_that_never_updated_into_kcap_is_found() {
        var own    = Install(LegacyWindowsInstall.KcapFolderName);
        var legacy = Install(LegacyWindowsInstall.FolderName, VelopackRoot.LegacyAppExecutable);

        await Assert.That(LegacyWindowsInstall.Find(own, "1.5.0", Local, [])?.Root).IsEqualTo(LegacyPaths.Normalize(legacy));
    }

    [Test]
    public async Task A_KurrentCapacitor_folder_without_the_app_is_left_alone() {
        var own = Install(LegacyWindowsInstall.KcapFolderName);
        Tmp.CreateFile($"Local/{LegacyWindowsInstall.FolderName}/notes.txt");

        await Assert.That(LegacyWindowsInstall.Find(own, "1.5.0", Local, [])).IsNull();
    }

    [Test]
    public async Task A_legacy_root_an_uninstall_entry_names_elsewhere_is_found() {
        var own = Install(LegacyWindowsInstall.KcapFolderName);
        Tmp.CreateFile($"Elsewhere/{LegacyWindowsInstall.FolderName}/current/{VelopackRoot.AppExecutable}");
        var registered = Tmp.PathTo("Elsewhere", LegacyWindowsInstall.FolderName);

        var found = LegacyWindowsInstall.Find(own, "1.5.0", Tmp.PathTo("Unused"), [null, registered]);

        await Assert.That(found?.Root).IsEqualTo(LegacyPaths.Normalize(registered));
    }

    /// <summary>The entry is kcap's, but only a folder named for the old product is the legacy install.</summary>
    [Test]
    public async Task An_uninstall_entry_naming_another_folder_is_not_a_legacy_root() {
        var own = Install(LegacyWindowsInstall.KcapFolderName);
        Tmp.CreateFile($"Other/kcap-copy/current/{VelopackRoot.AppExecutable}");

        await Assert.That(LegacyWindowsInstall.Find(own, "1.5.0", Local, [Tmp.PathTo("Other", "kcap-copy")])).IsNull();
    }

    [Test]
    public async Task A_copy_running_from_the_legacy_root_retires_nothing() {
        Install(LegacyWindowsInstall.KcapFolderName);
        var legacy = Install(LegacyWindowsInstall.FolderName);

        await Assert.That(LegacyWindowsInstall.Find(legacy, "1.5.0", Local, [legacy])).IsNull();
    }

    /// <summary>Taking the task of a newer legacy copy would run an older daemon over its state.</summary>
    [Test]
    public async Task A_legacy_root_newer_than_this_install_is_left_until_it_catches_up() {
        var own = Install(LegacyWindowsInstall.KcapFolderName);
        Install(LegacyWindowsInstall.FolderName, version: "1.6.0");

        await Assert.That(LegacyWindowsInstall.Find(own, "1.5.0", Local, [])).IsNull();
        await Assert.That(LegacyWindowsInstall.Find(own, "1.6.0", Local, [])?.Version).IsEqualTo("1.6.0");
    }

    [Test]
    public async Task A_legacy_copy_hands_its_launch_to_the_kcap_install() {
        var own    = Install(LegacyWindowsInstall.KcapFolderName);
        var legacy = Install(LegacyWindowsInstall.FolderName);

        await Assert.That(LegacyWindowsInstall.Successor(legacy, Local)).IsEqualTo(Path.Combine(own, "current", VelopackRoot.AppExecutable));
        await Assert.That(LegacyWindowsInstall.Successor(own, Local)).IsNull();
    }

    [Test]
    public async Task Without_a_kcap_install_a_legacy_copy_keeps_its_launch() {
        var legacy = Install(LegacyWindowsInstall.FolderName);
        Tmp.CreateFile($"Local/{LegacyWindowsInstall.KcapFolderName}/readme.txt");

        await Assert.That(LegacyWindowsInstall.Successor(legacy, Local)).IsNull();
    }
}
