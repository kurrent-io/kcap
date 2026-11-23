using Capacitor.App.Platform.Windows;

namespace Capacitor.App.Tests.Unit.Platform.Windows;

public class WindowsUninstallCommandTests {
    const string Root = @"C:\Users\me\AppData\Local\kcap";
    const string App  = @"C:\Users\me\AppData\Local\kcap\current\kcap-desktop.exe";
    const string Own  = "\"" + App + "\" --uninstall --confirm";

    [Test]
    public async Task The_installers_command_is_replaced_by_the_apps_own_uninstall() {
        var command = WindowsUninstallCommand.Replacement(Root, $"\"{Root}\\Update.exe\" --uninstall", Root, App);

        await Assert.That(command).IsEqualTo(Own);
    }

    [Test]
    public async Task A_command_that_already_runs_the_app_is_left_alone() {
        await Assert.That(WindowsUninstallCommand.Replacement(Root + @"\", Own, Root, App)).IsNull();
    }

    /// The entry's key is shared by whichever folder installed kcap last, so another install's entry is not this app's to edit.
    [Test]
    [Arguments(@"C:\Users\me\AppData\Local\KurrentCapacitor")]
    [Arguments(null)]
    public async Task Another_installs_entry_is_left_alone(string? installLocation) {
        await Assert.That(WindowsUninstallCommand.Replacement(installLocation, "\"x\\Update.exe\" --uninstall", Root, App)).IsNull();
    }
}
