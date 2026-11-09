using System.Diagnostics;
using System.Reactive.Linq;
using Capacitor.App.Platform.Windows;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// The in-app uninstall on Windows: `kcap uninstall`, then the CLI's folder off the user PATH, then Velopack's own
/// uninstaller, which ends every process in the install and so is started only from the app's exit.
public class WindowsAppRemovalTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class MemoryUserPath(string? value) : IUserPathStore {
        public string? Value = value;
        public bool IgnoreWrites;
        public string? Read() => Value;
        public void Append(string directory) => Value = string.IsNullOrEmpty(Value) ? directory : $"{Value};{directory}";
        public void Replace(string path) {
            if (!IgnoreWrites) Value = path;
        }
    }

    readonly List<string> _calls = [];
    readonly List<ProcessStartInfo> _started = [];

    VelopackInstall Install(bool withUpdater = true) {
        var root = Tmp.CreateDir("kcap").Path;
        Tmp.CreateFile(Path.Combine("kcap", "current", VelopackRoot.AppExecutable));
        var bin = Tmp.CreateDir("kcap", "current", "bin").Path;
        if (withUpdater) Tmp.CreateFile(Path.Combine("kcap", VelopackRoot.UpdateExe));
        return new VelopackInstall(root, Path.Combine(root, VelopackRoot.UpdateExe), Path.Combine(bin, "kcap.exe"));
    }

    (UninstallRunViewModel Vm, WindowsAppRemoval Removal, FakeKcapCli Cli) Build(VelopackInstall install, MemoryUserPath path, int cliExit = 0) {
        var cli = new FakeKcapCli {
            UninstallBehavior = _ => {
                _calls.Add($"cli:{path.Value}");
                return Task.FromResult(new StreamingResult(cliExit, false, []));
            },
        };
        var removal = new WindowsAppRemoval(install, new WindowsUserPathInstaller(path, new FakeLoginShellProbe()), _started.Add);
        return (new UninstallRunViewModel(cli, removal, purgeLocal: true, () => _calls.Add("quit"), action => action()), removal, cli);
    }

    [Test]
    public async Task A_finished_run_takes_the_cli_off_the_path_after_kcap_uninstall_and_starts_the_uninstaller_only_on_exit() {
        var install = Install();
        var other   = Tmp.CreateDir("tools").Path;
        var bin     = Path.GetDirectoryName(install.Cli)!;
        var path    = new MemoryUserPath($"{other};{bin}");
        var (vm, removal, cli) = Build(install, path);

        await vm.RunAsync();

        await Assert.That(vm.State).IsEqualTo(UninstallRunState.Finished);
        await Assert.That(cli.UninstallPurgeFlags).IsEquivalentTo([true]);
        await Assert.That(_calls).IsEquivalentTo([$"cli:{other};{bin}"], CollectionOrdering.Matching);
        await Assert.That(path.Value).IsEqualTo(other);
        await Assert.That(_started).IsEmpty();
        await Assert.That(vm.Description).Contains("The app is removed when you close this window.");
        await Assert.That(vm.Receipt[^1]).IsEqualTo(new UninstallReceiptItem("The kcap app is removed when you close this window", Done: false));

        await vm.CloseCommand.Execute();
        await Assert.That(_calls[^1]).IsEqualTo("quit");
        await Assert.That(_started).IsEmpty();

        removal.StartUninstallerIfSet();
        removal.StartUninstallerIfSet();

        var started = _started.Single();
        await Assert.That(started.FileName).IsEqualTo(install.UpdateExe);
        await Assert.That(started.ArgumentList).IsEquivalentTo([WindowsAppRemoval.SilentArg, WindowsAppRemoval.UninstallArg], CollectionOrdering.Matching);
        await Assert.That(started.UseShellExecute).IsFalse();
        await Assert.That(started.Environment[WindowsUninstallCleanup.CompletedEnvVar]).IsEqualTo("1");
        await Assert.That(LegacyPaths.Within(started.WorkingDirectory, install.Root)).IsFalse();
    }

    [Test]
    public async Task A_failed_kcap_uninstall_keeps_the_path_and_never_starts_the_uninstaller() {
        var install = Install();
        var bin     = Path.GetDirectoryName(install.Cli)!;
        var path    = new MemoryUserPath(bin);
        var (vm, removal, _) = Build(install, path, cliExit: 1);

        await vm.RunAsync();
        removal.StartUninstallerIfSet();

        await Assert.That(vm.State).IsEqualTo(UninstallRunState.Failed);
        await Assert.That(path.Value).IsEqualTo(bin);
        await Assert.That(_started).IsEmpty();
    }

    [Test]
    public async Task A_path_entry_that_stays_fails_the_run_before_the_uninstaller_is_set() {
        var install = Install();
        var bin     = Path.GetDirectoryName(install.Cli)!;
        var path    = new MemoryUserPath(bin) { IgnoreWrites = true };
        var (vm, removal, _) = Build(install, path);

        await vm.RunAsync();
        removal.StartUninstallerIfSet();

        await Assert.That(vm.State).IsEqualTo(UninstallRunState.Failed);
        await Assert.That(vm.Lines).Contains($"Could not remove {bin} from your user PATH");
        await Assert.That(_started).IsEmpty();
    }

    [Test]
    [NotInParallel]
    public async Task A_missing_updater_sends_the_user_to_Settings_Apps_and_starts_nothing_on_exit() {
        var install = Install(withUpdater: false);
        var (vm, removal, _) = Build(install, new MemoryUserPath(null));
        using var stderr = ConsoleOutput.StartErrorCapture();

        await vm.RunAsync();
        removal.StartUninstallerIfSet();

        await Assert.That(vm.State).IsEqualTo(UninstallRunState.TrashFailed);
        await Assert.That(vm.Heading).IsEqualTo("Remove kcap in Settings › Apps to finish");
        await Assert.That(vm.Description).IsEqualTo("The app could not be handed to its Windows uninstaller. See the details below to finish.");
        var opened = _started.Single();
        await Assert.That(opened.FileName).IsEqualTo(WindowsAppRemoval.AppsSettings);
        await Assert.That(opened.UseShellExecute).IsTrue();
    }
}
