using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// A repoint moves a job off another install's daemon. A stopped job stays stopped, since every bootstrap of a
/// kcap unit starts it, and the original plist is held in the daemon store from before its daemon is stopped
/// until the job runs again.
/// </summary>
public class LaunchdServiceManagerRepointTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    const string Old    = "/npm/lib/node_modules/@kurrent/kcap-darwin-arm64/bin/kcap-daemon";
    const string Bundle = "/Applications/kcap.app/Contents/MacOS/kcap-daemon";

    static string Stabilize(string binary) => binary == Old ? Bundle : binary;

    static readonly Func<TimeSpan> Plenty = static () => LaunchdServiceManager.ReloadBudget + TimeSpan.FromMinutes(1);

    static string Plist(string binary) => LaunchdUnit.Plist(new ServiceSpec(
        "test", binary, "/home/u/.config/kcap/daemon-test.log", new Dictionary<string, string> { ["KCAP_PROFILE"] = "work" }, ["--max-agents", "4"]));

    string PlistPath => LaunchdUnit.PlistPath(Home, "test");
    string Preimage  => Daemons.Store.RepointPreimagePath("test");

    void Seed(string binary) {
        Directory.CreateDirectory(LaunchdUnit.AgentsDir(Home));
        File.WriteAllText(PlistPath, Plist(binary));
    }

    UnitRefresh Repoint(FakeLaunchctl launchctl, Func<bool> stopForReload, out string? error) =>
        launchctl.Manager(Daemons.Store).RefreshUnit(
            "test", stopForReload, Plenty, out error, Stabilize, leaveStoppedUnloaded: true, preimagePath: Preimage);

    [Test]
    public async Task A_loaded_stopped_job_is_booted_out_and_left_unloaded_without_a_bootstrap() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed(Old);
        var launchctl = new FakeLaunchctl(Home);
        launchctl.Load("test", Old, running: false);
        var asked = false;

        var outcome = Repoint(launchctl, () => asked = true, out var error);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.NotLoaded);
        await Assert.That(error).IsNull();
        await Assert.That(asked).IsFalse();
        await Assert.That(launchctl.IsLoaded("test")).IsFalse();
        await Assert.That(launchctl.Verbs).IsEquivalentTo(["print", "bootout"]);
        await Assert.That(File.ReadAllText(PlistPath)).IsEqualTo(Plist(Bundle));
        await Assert.That(File.Exists(Preimage)).IsFalse();
    }

    [Test]
    public async Task An_unloaded_unit_is_rewritten_and_left_unloaded() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed(Old);
        var launchctl = new FakeLaunchctl(Home);

        var outcome = Repoint(launchctl, () => true, out _);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.NotLoaded);
        await Assert.That(launchctl.Verbs).IsEquivalentTo(["print"]);
        await Assert.That(File.ReadAllText(PlistPath)).IsEqualTo(Plist(Bundle));
        await Assert.That(File.Exists(Preimage)).IsFalse();
    }

    [Test]
    public async Task A_failed_bootstrap_restores_the_original_plist_and_its_running_state() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed(Old);
        var launchctl = new FakeLaunchctl(Home) { FailingBootstraps = 1 };
        launchctl.Load("test", Old);

        var outcome = Repoint(launchctl, () => { launchctl.Exit("test"); return true; }, out var error);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Failed);
        await Assert.That(error).Contains("previous unit was restored and loaded");
        await Assert.That(File.ReadAllText(PlistPath)).IsEqualTo(Plist(Old));
        await Assert.That(launchctl.Program("test")).IsEqualTo(Old);
        await Assert.That(launchctl.IsRunning("test")).IsTrue();
        await Assert.That(File.Exists(Preimage)).IsFalse();
    }

    /// <summary>The daemon has already exited for the reload, and launchd keeps the job with its old program.</summary>
    [Test]
    public async Task A_bootout_that_leaves_the_job_loaded_puts_the_plist_back_and_starts_the_daemon_again() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed(Old);
        var launchctl = new FakeLaunchctl(Home) { BootoutFails = true };
        launchctl.Load("test", Old);

        var outcome = Repoint(launchctl, () => { launchctl.Exit("test"); return true; }, out var error);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Failed);
        await Assert.That(error).Contains("previous unit was put back");
        await Assert.That(File.ReadAllText(PlistPath)).IsEqualTo(Plist(Old));
        await Assert.That(launchctl.Verbs).Contains("kickstart");
        await Assert.That(launchctl.IsRunning("test")).IsTrue();
        await Assert.That(File.Exists(Preimage)).IsFalse();
    }

    [Test]
    public async Task The_preimage_exists_before_the_stop_and_is_gone_after_success() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed(Old);
        var launchctl = new FakeLaunchctl(Home);
        launchctl.Load("test", Old);
        (string Text, UnixFileMode Mode)? atStop = null;

        var outcome = Repoint(launchctl, () => {
            if (!OperatingSystem.IsWindows() && File.Exists(Preimage)) atStop = (File.ReadAllText(Preimage), File.GetUnixFileMode(Preimage));
            launchctl.Exit("test");
            return true;
        }, out var error);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Reloaded);
        await Assert.That(error).IsNull();
        await Assert.That(atStop?.Text).IsEqualTo(Plist(Old));
        await Assert.That(atStop?.Mode).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.That(File.Exists(Preimage)).IsFalse();
        await Assert.That(launchctl.Program("test")).IsEqualTo(Bundle);
        await Assert.That(File.ReadAllText(PlistPath)).IsEqualTo(Plist(Bundle));
    }

    [Test]
    public async Task Uninstall_removes_the_unit_s_preimage() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed(Bundle);
        File.WriteAllText(Preimage, Plist(Old));
        var launchctl = new FakeLaunchctl(Home);
        launchctl.Load("test", Bundle);

        var removed = launchctl.Manager(Daemons.Store).Uninstall("test", out var error);

        await Assert.That(removed).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(File.Exists(PlistPath)).IsFalse();
        await Assert.That(File.Exists(Preimage)).IsFalse();
    }
}
