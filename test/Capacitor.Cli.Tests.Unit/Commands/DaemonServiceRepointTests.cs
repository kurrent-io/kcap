using System.Runtime.InteropServices;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Services;
using Capacitor.Cli.Tests.Unit.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>The desktop app moves a launchd daemon service off an npm or script install's daemon before it deletes
/// that install, so every unit running a daemon from inside the folder moves to this install's, and no other.</summary>
[NotInParallel]
public partial class DaemonServiceRepointTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint getuid();

    const string Bundle = "/Applications/kcap.app/Contents/MacOS/kcap-daemon";
    const string Brew   = "/opt/homebrew/bin/kcap-daemon";

    string Dir       => Tmp.PathTo("npm", "lib", "node_modules", "@kurrent");
    string OldDaemon => Tmp.PathTo("npm", "lib", "node_modules", "@kurrent", "kcap-darwin-arm64", "bin", "kcap-daemon");

    static ServiceSpec Spec(string id, string binary) => new(id, binary, $"/home/u/.config/kcap/daemon-{id}.log",
        new Dictionary<string, string> { ["KCAP_PROFILE"] = "work" }, ["--max-agents", "4"]);

    string PlistPath(string id) => LaunchdUnit.PlistPath(Home, id);
    string Preimage(string id)  => Daemons.Store.RepointPreimagePath(id);

    void Install(string id, string binary) {
        Directory.CreateDirectory(LaunchdUnit.AgentsDir(Home));
        File.WriteAllText(PlistPath(id), LaunchdUnit.Plist(Spec(id, binary)));
    }

    DaemonServiceCommands Commands(
            FakeLaunchctl launchctl, ReloadStop answer = ReloadStop.Exited, Action<string>? atStop = null, Action<string>? beforeLock = null) =>
        new(Daemons.Store, Config.Root, Resolutions.None(Config.Root), launchctl.Manager(Daemons.Store), "unused", Home, TimeProvider.System) {
            HostsCaller   = id => {
                beforeLock?.Invoke(id);
                return false;
            },
            ReloadStopper = id => {
                atStop?.Invoke(id);
                if (answer == ReloadStop.Exited) launchctl.Exit(id);
                return answer;
            },
        };

    [Test]
    public async Task A_running_unit_under_dir_is_repointed_with_only_its_program_changed() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Install("npm", OldDaemon);
        var launchctl = new FakeLaunchctl(Home);
        launchctl.Load("npm", OldDaemon);
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(launchctl).RepointFrom(Dir, Bundle);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.ReadAllText(PlistPath("npm"))).IsEqualTo(LaunchdUnit.Plist(Spec("npm", Bundle)));
        await Assert.That(launchctl.Program("npm")).IsEqualTo(Bundle);
        await Assert.That(launchctl.IsRunning("npm")).IsTrue();
        await Assert.That(console.GetCapturedOutput()).Contains($"Daemon 'npm': now runs {Bundle}");
    }

    [Test]
    public async Task A_busy_daemon_leaves_the_unit_unchanged_and_exits_3() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Install("npm", OldDaemon);
        var before    = File.ReadAllText(PlistPath("npm"));
        var launchctl = new FakeLaunchctl(Home);
        launchctl.Load("npm", OldDaemon);
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(launchctl, ReloadStop.RestartRefused).RepointFrom(Dir, Bundle);

        await Assert.That(exit).IsEqualTo(3);
        await Assert.That(File.ReadAllText(PlistPath("npm"))).IsEqualTo(before);
        await Assert.That(File.Exists(Preimage("npm"))).IsFalse();
        await Assert.That(launchctl.Verbs).DoesNotContain("bootout");
        await Assert.That(launchctl.Program("npm")).IsEqualTo(OldDaemon);
        await Assert.That(console.GetCapturedError()).Contains("Daemon 'npm' is busy");
    }

    /// <summary>The run was cut short between the bootout and the bootstrap: the plist names this install's daemon,
    /// and launchd holds nothing. The original goes back and runs before the repoint starts over.</summary>
    [Test]
    public async Task A_preimage_left_by_a_crash_is_restored_before_the_next_repoint() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Install("npm", Bundle);
        File.WriteAllText(Preimage("npm"), LaunchdUnit.Plist(Spec("npm", OldDaemon)));
        var launchctl = new FakeLaunchctl(Home);
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(launchctl).RepointFrom(Dir, Bundle);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(launchctl.Bootstrapped).IsEquivalentTo([OldDaemon, Bundle]);
        await Assert.That(File.ReadAllText(PlistPath("npm"))).IsEqualTo(LaunchdUnit.Plist(Spec("npm", Bundle)));
        await Assert.That(launchctl.Program("npm")).IsEqualTo(Bundle);
        await Assert.That(File.Exists(Preimage("npm"))).IsFalse();
    }

    /// <summary>launchd may load any file in LaunchAgents at login, and a preimage carries the unit's own Label.</summary>
    [Test]
    public async Task The_preimage_is_kept_in_the_daemon_store_and_never_in_LaunchAgents() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Install("npm", OldDaemon);
        var launchctl = new FakeLaunchctl(Home);
        launchctl.Load("npm", OldDaemon);
        (bool Exists, UnixFileMode? Mode, string[] Agents)? atStop = null;
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(launchctl, atStop: id => {
            if (OperatingSystem.IsWindows()) return;
            var exists = File.Exists(Preimage(id));
            atStop = (exists, exists ? File.GetUnixFileMode(Preimage(id)) : null,
                [.. Directory.EnumerateFileSystemEntries(LaunchdUnit.AgentsDir(Home)).Select(Path.GetFileName).OfType<string>()]);
        }).RepointFrom(Dir, Bundle);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(atStop?.Exists).IsTrue();
        await Assert.That(atStop?.Mode).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.That(atStop?.Agents).IsEquivalentTo([Path.GetFileName(PlistPath("npm"))]);
        await Assert.That(File.Exists(Preimage("npm"))).IsFalse();
    }

    /// <summary>The leftover preimage names the old daemon, and putting it back would replace the profile and
    /// environment the new unit was installed with.</summary>
    [Test]
    public async Task An_install_over_a_leftover_preimage_discards_it_and_a_repoint_keeps_the_new_unit() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        File.WriteAllText(Preimage("npm"), LaunchdUnit.Plist(Spec("npm", OldDaemon)));
        Directory.CreateDirectory(LaunchdUnit.AgentsDir(Home));
        var fresh = new ServiceSpec("npm", Bundle, "/home/u/.config/kcap/daemon-npm.log",
            new Dictionary<string, string> { ["KCAP_PROFILE"] = "fresh" }, []);
        var launchctl = new FakeLaunchctl(Home);

        var installed      = launchctl.Manager(Daemons.Store).Install(fresh, startNow: true, out var installError);
        var leftAfterwards = File.Exists(Preimage("npm"));
        using var console  = ConsoleOutput.StartFullCapture();
        var exit           = await Commands(launchctl).RepointFrom(Dir, Bundle);

        await Assert.That(installed).IsTrue();
        await Assert.That(installError).IsNull();
        await Assert.That(leftAfterwards).IsFalse();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.ReadAllText(PlistPath("npm"))).IsEqualTo(LaunchdUnit.Plist(fresh));
        await Assert.That(launchctl.Bootstrapped).IsEquivalentTo([Bundle]);
    }

    /// <summary>The caller deletes the folder after a clean run, so a unit kcap cannot read is a failure, not a pass.</summary>
    [Test]
    public async Task A_unit_that_cannot_be_read_fails_the_run() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Directory.CreateDirectory(LaunchdUnit.AgentsDir(Home));
        File.WriteAllText(PlistPath("npm"), "not a plist");
        var launchctl = new FakeLaunchctl(Home);
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(launchctl).RepointFrom(Dir, Bundle);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(File.ReadAllText(PlistPath("npm"))).IsEqualTo("not a plist");
        await Assert.That(console.GetCapturedError()).Contains("Daemon 'npm': its unit could not be read");
    }

    /// <summary>The unit is read again once the lock is held, and a read that fails then fails the run too.</summary>
    [Test]
    public async Task A_unit_that_becomes_unreadable_before_the_lock_fails_the_run() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Install("npm", OldDaemon);
        var launchctl = new FakeLaunchctl(Home);
        launchctl.Load("npm", OldDaemon);
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(launchctl, beforeLock: id => File.WriteAllText(PlistPath(id), "not a plist")).RepointFrom(Dir, Bundle);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(launchctl.Program("npm")).IsEqualTo(OldDaemon);
        await Assert.That(console.GetCapturedError()).Contains("Daemon 'npm': its unit could not be read");
    }

    /// <summary>Without the loaded program the job may run the old binary, so the rewrite takes a reload.</summary>
    [Test]
    public async Task A_job_whose_loaded_program_launchd_does_not_show_is_reloaded() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Install("npm", OldDaemon);
        var launchctl = new FakeLaunchctl(Home) { HidesProgram = true };
        launchctl.Load("npm", OldDaemon);
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(launchctl).RepointFrom(Dir, Bundle);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(launchctl.Bootstrapped).IsEquivalentTo([Bundle]);
        await Assert.That(launchctl.Program("npm")).IsEqualTo(Bundle);
        await Assert.That(console.GetCapturedOutput()).Contains($"Daemon 'npm': now runs {Bundle}; it restarted");
    }

    /// <summary>launchd loads a plist under the label inside it, so a probe by the file's name would miss the job.</summary>
    [Test]
    public async Task A_plist_declaring_another_label_fails_the_run() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Directory.CreateDirectory(LaunchdUnit.AgentsDir(Home));
        File.WriteAllText(PlistPath("npm"), LaunchdUnit.Plist(Spec("copied", OldDaemon)));
        var before    = File.ReadAllText(PlistPath("npm"));
        var launchctl = new FakeLaunchctl(Home);
        launchctl.Load("copied", OldDaemon);
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(launchctl).RepointFrom(Dir, Bundle);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(File.ReadAllText(PlistPath("npm"))).IsEqualTo(before);
        await Assert.That(console.GetCapturedError()).Contains($"its plist does not declare the label {LaunchdUnit.Label("npm")}");
    }

    /// <summary>A plist rewritten while its job kept running leaves launchd on the old program until a reload.</summary>
    [Test]
    public async Task A_job_still_running_from_dir_under_a_rewritten_plist_is_reloaded() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Install("npm", Bundle);
        var launchctl = new FakeLaunchctl(Home);
        launchctl.Load("npm", OldDaemon);
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(launchctl).RepointFrom(Dir, Bundle);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(launchctl.Bootstrapped).IsEquivalentTo([Bundle]);
        await Assert.That(launchctl.Program("npm")).IsEqualTo(Bundle);
        await Assert.That(launchctl.IsRunning("npm")).IsTrue();
    }

    [Test]
    public async Task A_job_whose_program_launchd_does_not_show_fails_the_run_when_its_plist_is_elsewhere() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Install("brew", Brew);
        var launchctl = new FakeLaunchctl(Home) { HidesProgram = true };
        launchctl.Load("brew", Brew);
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(launchctl).RepointFrom(Dir, Bundle);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(launchctl.Verbs).DoesNotContain("bootout");
        await Assert.That(console.GetCapturedError()).Contains("Daemon 'brew': launchd does not show the program its job runs");
    }

    /// <summary>Only a unit known to be gone lets its preimage go: something else at the plist path may hide the
    /// rewritten unit the preimage is there to undo.</summary>
    [Test]
    public async Task A_preimage_is_kept_while_its_unit_cannot_be_read() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Directory.CreateDirectory(PlistPath("npm"));
        File.WriteAllText(Preimage("npm"), LaunchdUnit.Plist(Spec("npm", OldDaemon)));
        var launchctl = new FakeLaunchctl(Home);
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(launchctl).RepointFrom(Dir, Bundle);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(File.Exists(Preimage("npm"))).IsTrue();
        await Assert.That(console.GetCapturedError()).Contains("the unit could not be read");
    }

    /// <summary>A preimage outlives its unit when the plist is removed some other way than `service uninstall`.</summary>
    [Test]
    public async Task An_orphan_preimage_with_no_unit_is_dropped() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        File.WriteAllText(Preimage("gone"), LaunchdUnit.Plist(Spec("gone", OldDaemon)));
        var launchctl = new FakeLaunchctl(Home);
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(launchctl).RepointFrom(Dir, Bundle);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.Exists(Preimage("gone"))).IsFalse();
        await Assert.That(File.Exists(PlistPath("gone"))).IsFalse();
        await Assert.That(launchctl.Verbs).IsEquivalentTo(["print"]);
    }

    /// <summary>launchd runs the program it loaded whatever happens to the plist, and that may be the old install's.</summary>
    [Test]
    public async Task A_preimage_whose_plist_is_gone_is_kept_while_launchd_holds_the_job() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        File.WriteAllText(Preimage("gone"), LaunchdUnit.Plist(Spec("gone", OldDaemon)));
        var launchctl = new FakeLaunchctl(Home);
        launchctl.Load("gone", OldDaemon);
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(launchctl).RepointFrom(Dir, Bundle);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(File.Exists(Preimage("gone"))).IsTrue();
        await Assert.That(console.GetCapturedError()).Contains("launchd may still run its job");
    }

    [Test]
    public async Task Units_outside_dir_and_another_user_s_labels_are_untouched() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Install("npm", OldDaemon);
        Install("brew", Brew);
        var foreign = Path.Combine(LaunchdUnit.AgentsDir(Home), "com.example.agent.plist");
        File.WriteAllText(foreign, LaunchdUnit.Plist(Spec("npm", OldDaemon)).Replace(LaunchdUnit.Label("npm"), "com.example.agent"));
        var (brewBefore, foreignBefore) = (File.ReadAllText(PlistPath("brew")), File.ReadAllText(foreign));
        var launchctl = new FakeLaunchctl(Home);
        launchctl.Load("npm", OldDaemon);
        launchctl.Load("brew", Brew);
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(launchctl).RepointFrom(Dir, Bundle);

        var label = $"gui/{getuid()}/{LaunchdUnit.Label("npm")}";
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.ReadAllText(PlistPath("brew"))).IsEqualTo(brewBefore);
        await Assert.That(File.ReadAllText(foreign)).IsEqualTo(foreignBefore);
        await Assert.That(launchctl.Program("brew")).IsEqualTo(Brew);
        await Assert.That(launchctl.Calls.Select(c => string.Join(' ', c)).ToArray()).IsEquivalentTo([
            $"print gui/{getuid()}/{LaunchdUnit.Label("brew")}",
            $"print {label}", $"bootout {label}", $"bootstrap gui/{getuid()} {PlistPath("npm")}", $"print {label}",
        ]);
    }

    [Test]
    public async Task A_dir_reached_through_a_symlinked_parent_still_matches() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Tmp.CreateDir("real", "npm");
        Directory.CreateSymbolicLink(Tmp.PathTo("link"), Tmp.PathTo("real"));
        var daemon = Tmp.PathTo("real", "npm", "kcap-darwin-arm64", "bin", "kcap-daemon");
        Install("npm", daemon);
        var launchctl = new FakeLaunchctl(Home);
        launchctl.Load("npm", daemon);
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(launchctl).RepointFrom(Tmp.PathTo("link", "npm"), Bundle);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(launchctl.Program("npm")).IsEqualTo(Bundle);
        await Assert.That(LaunchdPrint.Binary(File.ReadAllText(PlistPath("npm")))).IsEqualTo(Bundle);
    }
}
