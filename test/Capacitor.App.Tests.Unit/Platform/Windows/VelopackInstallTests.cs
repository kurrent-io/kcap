using Capacitor.App.Platform.Windows;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit.Platform.Windows;

/// Only the install the running app came from may reach Velopack's uninstaller, which deletes that whole root.
public class VelopackInstallTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string Root(bool app = true, bool updater = true) {
        var root = Tmp.CreateDir("kcap").Path;
        if (app) Tmp.CreateFile(Path.Combine("kcap", "current", VelopackRoot.AppExecutable));
        if (updater) Tmp.CreateFile(Path.Combine("kcap", VelopackRoot.UpdateExe));
        return root;
    }

    string Bin => Tmp.PathTo("kcap", "current", "bin");

    [Test]
    public async Task An_app_running_from_its_install_names_the_root_the_updater_and_the_cli() {
        var root = Root();

        var install = VelopackInstall.Detect(root, Path.Combine(root, "current", VelopackRoot.AppExecutable), Bin);

        await Assert.That(install).IsEqualTo(new VelopackInstall(
            root, Path.Combine(root, VelopackRoot.UpdateExe), Path.Combine(Bin, CliResolver.BundledCliFileName)));
    }

    [Test]
    public async Task A_run_from_outside_the_install_or_an_incomplete_one_has_none() {
        var root = Root();
        var inside = Path.Combine(root, "current", VelopackRoot.AppExecutable);

        await Assert.That(VelopackInstall.Detect(root, Tmp.PathTo("bin", "Debug", VelopackRoot.AppExecutable), Bin)).IsNull();
        await Assert.That(VelopackInstall.Detect(root, null, Bin)).IsNull();
        await Assert.That(VelopackInstall.Detect(null, inside, Bin)).IsNull();
        await Assert.That(VelopackInstall.Detect(Tmp.PathTo("elsewhere"), inside, Bin)).IsNull();
    }

    [Test]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task A_root_without_the_app_or_its_updater_has_none(bool app, bool updater) {
        var root = Root(app, updater);

        await Assert.That(VelopackInstall.Detect(root, Path.Combine(root, "current", VelopackRoot.AppExecutable), Bin)).IsNull();
    }
}
