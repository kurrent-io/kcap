using Capacitor.App.Platform.Mac;
using Capacitor.App.Platform.Windows;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

public class WindowsOtherKcapInstallsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    /// Each `kcap` a new terminal reaches outside the install is named once, npm's with the command that removes it;
    /// the install's own CLI folder is not another install.
    [Test]
    public async Task Names_each_kcap_outside_the_install_once() {
        var npm = Tmp.CreateDir("npm").Path;
        Tmp.CreateFile(Path.Combine("npm", "kcap.cmd"));
        Tmp.CreateDir("npm", "node_modules", "@kurrent", "kcap");
        var tools = Tmp.CreateDir("tools").Path;
        Tmp.CreateFile(Path.Combine("tools", "kcap.exe"));
        var empty = Tmp.CreateDir("empty").Path;
        var root  = Tmp.CreateDir("kcap").Path;
        Tmp.CreateFile(Path.Combine("kcap", "current", "bin", "kcap.exe"));

        var found = WindowsOtherKcapInstalls.Find(
            string.Join(';', npm, $"\"{tools}\"", empty, Path.Combine(root, "current", "bin"), npm, "relative"), root);

        await Assert.That(found).IsEquivalentTo([
            new OtherKcapInstall(Path.Combine(npm, "kcap.cmd"), "npm uninstall -g @kurrent/kcap"),
            new OtherKcapInstall(Path.Combine(tools, "kcap.exe"), null),
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task No_terminal_path_finds_none() {
        await Assert.That(WindowsOtherKcapInstalls.Find(null, Tmp.Path)).IsEmpty();
    }
}
