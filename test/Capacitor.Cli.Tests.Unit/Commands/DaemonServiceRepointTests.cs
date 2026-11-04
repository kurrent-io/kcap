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

    DaemonServiceCommands Commands(FakeLaunchctl launchctl, ReloadStop answer = ReloadStop.Exited, Action<string>? atStop = null) =>
        new(Daemons.Store, Config.Root, Resolutions.None(Config.Root), launchctl.Manager(Daemons.Store), "unused", Home, TimeProvider.System) {
            HostsCaller   = _ => false,
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
        await Assert.That(launchctl.Calls).IsEmpty();
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
