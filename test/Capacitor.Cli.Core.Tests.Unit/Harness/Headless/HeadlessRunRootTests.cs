using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Capacitor.Cli.Core.Harness.Headless;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Headless;

public class HeadlessRunRootTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    const UnixFileMode Everyone  = (UnixFileMode)0b111_111_111;

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task Create_makes_the_root_and_each_run_directory_owner_only_and_empty() {
        var state = Tmp.CreateDir("state").Path;

        using var run   = new HeadlessRunRoot(state, "epoch1").Create("pi");
        using var child = run.Child("config");

        await Assert.That(File.GetUnixFileMode(Path.Combine(state, "headless-runs"))).IsEqualTo(OwnerOnly);
        await Assert.That(Path.GetDirectoryName(run.Path)).IsEqualTo(Path.Combine(state, "headless-runs"));
        await Assert.That(Path.GetFileName(run.Path)).StartsWith("pi-epoch1-");
        await Assert.That(File.GetUnixFileMode(run.Path)).IsEqualTo(OwnerOnly);
        await Assert.That(File.GetUnixFileMode(child.Path)).IsEqualTo(OwnerOnly);
        await Assert.That(Directory.EnumerateFileSystemEntries(child.Path)).IsEmpty();
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task A_root_wider_than_owner_only_is_refused_and_nothing_is_created_in_it() {
        var state = Tmp.CreateDir("state").Path;
        var root  = Tmp.CreateDir("state", "headless-runs").Path;
        File.SetUnixFileMode(root, Everyone);

        await Assert.That(() => new HeadlessRunRoot(state, "epoch1").Create("pi")).Throws<HeadlessRunRefusedException>();
        await Assert.That(Directory.EnumerateFileSystemEntries(root)).IsEmpty();
    }

    /// <summary>The root itself comes out <c>0700</c>, so only the ancestor check can refuse it.</summary>
    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task A_state_dir_others_can_write_refuses_the_run() {
        var state = Tmp.CreateDir("state").Path;
        File.SetUnixFileMode(state, Everyone);

        await Assert.That(() => new HeadlessRunRoot(state, "epoch1").Create("pi")).Throws<HeadlessRunRefusedException>();
        await Assert.That(File.GetUnixFileMode(Path.Combine(state, "headless-runs"))).IsEqualTo(OwnerOnly);
        await Assert.That(Directory.EnumerateFileSystemEntries(Path.Combine(state, "headless-runs"))).IsEmpty();
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task A_sticky_world_writable_state_dir_is_allowed() {
        var state = Tmp.CreateDir("state").Path;
        File.SetUnixFileMode(state, Everyone | UnixFileMode.StickyBit);

        using var run = new HeadlessRunRoot(state, "epoch1").Create("pi");

        await Assert.That(Directory.Exists(run.Path)).IsTrue();
    }

    [Test]
    public async Task Dispose_removes_the_tree_and_leaves_a_links_target_untouched() {
        var outside = Tmp.CreateFile(["outside", "keep.txt"], "keep");
        var run     = new HeadlessRunRoot(Tmp.CreateDir("state").Path, "epoch1").Create("opencode");
        var child   = run.Child("data");
        File.WriteAllText(Path.Combine(child.Path, "session.db"), "x");
        DirectoryLink.Create(Path.Combine(child.Path, "escape"), Tmp.PathTo("outside"));

        run.Dispose();

        await Assert.That(Directory.Exists(run.Path)).IsFalse();
        await Assert.That(File.Exists(outside)).IsTrue();
    }

    [Test]
    public async Task SweepStale_removes_another_epochs_directories_and_keeps_the_current_ones() {
        var state = Tmp.CreateDir("state").Path;
        var stale = new HeadlessRunRoot(state, "old").Create("pi");
        File.WriteAllText(Path.Combine(stale.Path, "prompt.md"), "secret");
        var root  = new HeadlessRunRoot(state, "new");
        var live  = root.Create("pi");

        root.SweepStale();

        await Assert.That(Directory.Exists(stale.Path)).IsFalse();
        await Assert.That(Directory.Exists(live.Path)).IsTrue();
    }

    /// <summary>Most daemons never run Pi or OpenCode, so a root not made yet is nothing to report at start.</summary>
    [Test]
    public async Task SweepStale_with_no_root_yet_has_nothing_to_report() {
        var state = Tmp.CreateDir("state").Path;

        await Assert.That(new HeadlessRunRoot(state, "new").SweepStale()).IsNull();
        await Assert.That(Directory.Exists(Path.Combine(state, "headless-runs"))).IsFalse();
    }

    /// <summary>A NUL in the path makes every filesystem call throw <see cref="ArgumentException"/>, outside the IO
    /// kinds: the sweep the daemon runs at start still reports rather than throws, and a run is refused.</summary>
    [Test]
    public async Task A_check_that_throws_anything_is_a_refusal_never_an_exception() {
        var root = new HeadlessRunRoot(Tmp.PathTo("state") + "\0x", "new");

        await Assert.That(root.SweepStale()).IsNotNull();
        await Assert.That(() => root.Create("pi")).Throws<HeadlessRunRefusedException>();
    }

    [Test]
    public async Task SweepStale_leaves_a_headless_runs_link_and_its_target_alone() {
        var state  = Tmp.CreateDir("state").Path;
        var victim = Tmp.CreateFile(["victim", "pi-old-0123456789abcdef0123456789abcdef", "keep.txt"], "keep");
        DirectoryLink.Create(Path.Combine(state, "headless-runs"), Tmp.PathTo("victim"));

        var refusal = new HeadlessRunRoot(state, "new").SweepStale();

        await Assert.That(refusal).IsNotNull();
        await Assert.That(File.Exists(victim)).IsTrue();
    }

    /// <summary>A link named like a stale run is unlinked, never descended into; a name <see cref="HeadlessRunRoot.Create"/>
    /// would not make is not touched.</summary>
    [Test]
    public async Task SweepStale_unlinks_a_link_inside_the_root_and_keeps_names_it_did_not_make() {
        var state  = Tmp.CreateDir("state").Path;
        using var live = new HeadlessRunRoot(state, "new").Create("pi");
        var root   = Path.Combine(state, "headless-runs");
        var victim = Tmp.CreateFile(["victim", "keep.txt"], "keep");
        var link   = Path.Combine(root, "pi-old-0123456789abcdef0123456789abcdef");
        DirectoryLink.Create(link, Tmp.PathTo("victim"));
        var foreign = Directory.CreateDirectory(Path.Combine(root, "not-a-run")).FullName;

        var refusal = new HeadlessRunRoot(state, "new").SweepStale();

        await Assert.That(refusal).IsNull();
        await Assert.That(Path.Exists(link)).IsFalse();
        await Assert.That(File.Exists(victim)).IsTrue();
        await Assert.That(Directory.Exists(foreign)).IsTrue();
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task SweepStale_leaves_a_root_wider_than_owner_only_alone() {
        var state = Tmp.CreateDir("state").Path;
        var stale = new HeadlessRunRoot(state, "old").Create("pi");
        File.SetUnixFileMode(Path.Combine(state, "headless-runs"), Everyone);

        var refusal = new HeadlessRunRoot(state, "new").SweepStale();

        await Assert.That(refusal).IsNotNull();
        await Assert.That(Directory.Exists(stale.Path)).IsTrue();
    }

    [Test, RunOn(OS.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task Create_on_windows_leaves_an_owner_only_run_directory() {
        using var run = new HeadlessRunRoot(Tmp.CreateDir("state").Path, "epoch1").Create("pi");

        await Assert.That(OwnerOnlyDirectory.Violation(run.Path)).IsNull();
    }

    [Test, RunOn(OS.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task A_parent_granting_users_delete_child_refuses_the_run() {
        var state    = Tmp.CreateDir("state").Path;
        var info     = new DirectoryInfo(state);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.DeleteSubdirectoriesAndFiles,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);

        await Assert.That(() => new HeadlessRunRoot(state, "epoch1").Create("pi"))
            .Throws<HeadlessRunRefusedException>()
            .WithMessageContaining(WindowsAcl.Users.Value);
    }
}
