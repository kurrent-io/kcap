using Capacitor.App.Services;
using Capacitor.App.Services.CliAdoption;
using Capacitor.Cli.Core;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit;

public class OtherCliInstallReaderTests {
    [TempDir] public required TempDir Tmp { get; init; }

    readonly EntryIdentity _identity = new(new ProcessRunner(TimeProvider.System));

    string Exe(string relative) {
        Tmp.CreateExecutable(relative, "#!/bin/sh\n");
        return Tmp.GetResolvedPath(relative);
    }

    string Npm(string prefix, bool nested) {
        var launcher = $"{prefix}/lib/node_modules/@kurrent/kcap";
        Tmp.CreateFile($"{launcher}/bin/kcap.js");
        Tmp.CreateFile($"{launcher}/package.json", """{"name":"@kurrent/kcap","version":"1.2.3"}""");
        var platform = nested ? $"{launcher}/node_modules/@kurrent/kcap-darwin-arm64" : $"{prefix}/lib/node_modules/@kurrent/kcap-darwin-arm64";
        Exe($"{platform}/bin/kcap-daemon");
        return Exe($"{platform}/bin/kcap");
    }

    Task<OtherCliInstall?> Read(string binary) => OtherCliInstallReader.FromBinaryAsync(binary, _identity, CancellationToken.None);

    [Test, RunOn(OS.MacOs)]
    public async Task An_npm_platform_binary_reads_as_an_npm_install_with_both_package_folders() {
        var binary = Npm("prefix", nested: false);

        var install = await Read(binary);

        var scope = Tmp.GetResolvedPath("prefix/lib/node_modules/@kurrent");
        await Assert.That(install).IsNotNull();
        await Assert.That(install!.Kind).IsEqualTo(OtherCliInstallKind.Npm);
        await Assert.That(install.Root).IsEqualTo($"{scope}/kcap");
        await Assert.That(install.Version).IsEqualTo("1.2.3");
        await Assert.That(install.CliBinary).IsEqualTo(binary);
        await Assert.That(install.DaemonBinary).IsEqualTo($"{scope}/kcap-darwin-arm64/bin/kcap-daemon");
        await Assert.That(install.OwnedEntries.SequenceEqual([$"{scope}/kcap", $"{scope}/kcap-darwin-arm64"])).IsTrue();
        await Assert.That(install.Identity).IsNotEmpty();
    }

    [Test, RunOn(OS.MacOs)]
    public async Task A_nested_npm_layout_owns_only_the_launcher_folder() {
        var install = await Read(Npm("prefix", nested: true));

        await Assert.That(install).IsNotNull();
        await Assert.That(install!.OwnedEntries.Count).IsEqualTo(1);
        await Assert.That(install.OwnedEntries[0]).IsEqualTo(install.Root);
    }

    [Test, RunOn(OS.MacOs)]
    public async Task The_launcher_script_reads_as_the_npm_install_beside_its_platform_binary() {
        Npm("prefix", nested: false);

        var install = await Read(Tmp.GetResolvedPath("prefix/lib/node_modules/@kurrent/kcap/bin/kcap.js"));

        await Assert.That(install?.Kind).IsEqualTo(OtherCliInstallKind.Npm);
    }

    [Test, RunOn(OS.MacOs)]
    public async Task A_script_current_link_reads_the_version_it_points_at() {
        Tmp.CreateFile("kcap-home/install.json", "{}");
        Exe("kcap-home/versions/1.0.0/bin/kcap");
        Exe("kcap-home/versions/1.0.0/bin/kcap-daemon");
        Exe("kcap-home/versions/1.1.0/bin/kcap");
        Exe("kcap-home/versions/1.1.0/bin/kcap-daemon");
        var root = Tmp.GetResolvedPath("kcap-home");
        File.CreateSymbolicLink($"{root}/current", $"{root}/versions/1.1.0");

        var install = await Read($"{root}/current/bin/kcap");

        await Assert.That(install).IsNotNull();
        await Assert.That(install!.Kind).IsEqualTo(OtherCliInstallKind.Script);
        await Assert.That(install.Root).IsEqualTo(root);
        await Assert.That(install.Version).IsEqualTo("1.1.0");
        await Assert.That(install.DaemonBinary).IsEqualTo($"{root}/versions/1.1.0/bin/kcap-daemon");
        await Assert.That(install.OwnedEntries.SequenceEqual([$"{root}/versions", $"{root}/current", $"{root}/install.json"])).IsTrue();
    }

    [Test, RunOn(OS.MacOs)]
    public async Task A_homebrew_or_dev_binary_reads_as_nothing() {
        await Assert.That(await Read(Exe("opt/homebrew/Cellar/kcap/1.0.0/bin/kcap"))).IsNull();
        await Assert.That(await Read(Exe("dev/src/Capacitor.Cli/bin/Debug/net10.0/kcap"))).IsNull();
    }

    [Test, RunOn(OS.MacOs)]
    public async Task An_install_under_a_symlinked_nvm_folder_is_read_through_its_real_path() {
        Npm("real/node/v20", nested: false);
        File.CreateSymbolicLink(Tmp.PathTo(".nvm"), Tmp.GetResolvedPath("real"));

        var install = await Read(Tmp.GetResolvedPath(".nvm/node/v20/lib/node_modules/@kurrent/kcap-darwin-arm64/bin/kcap"));

        await Assert.That(install?.Root).IsEqualTo(Tmp.GetResolvedPath("real/node/v20/lib/node_modules/@kurrent/kcap"));
    }

    [Test, RunOn(OS.MacOs)]
    public async Task A_reinstall_at_the_same_path_has_a_different_identity() {
        var binary = Npm("prefix", nested: false);
        var before = await Read(binary);

        var daemon = before!.DaemonBinary;
        var next = Tmp.CreateFile("next-daemon", "new");
        File.Move(next, daemon, overwrite: true);

        await Assert.That((await Read(binary))!.Identity).IsNotEqualTo(before.Identity);
    }

    [Test, RunOn(OS.MacOs)]
    public async Task An_install_without_a_daemon_binary_reads_as_nothing() {
        var binary = Npm("prefix", nested: false);
        File.Delete(Path.Combine(Path.GetDirectoryName(binary)!, "kcap-daemon"));

        await Assert.That(await Read(binary)).IsNull();
    }
}
