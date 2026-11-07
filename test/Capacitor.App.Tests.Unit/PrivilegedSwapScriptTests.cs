using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using TUnit.Core.Enums;
using Capacitor.App.Services;
using Capacitor.App.Services.CliAdoption;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// <summary>Runs the rendered helper script as the current user against fake installs, driving its control pipe the way
/// <see cref="PrivilegedSwap"/> does.</summary>
[RunOn(OS.MacOs), UnsupportedOSPlatform("windows"), ParallelLimiter<SubprocessLimit>]
public class PrivilegedSwapScriptTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Go_swaps_then_deletes_each_entry_after_its_marker_check() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        await using var run = Run.Start(Tmp, spec);

        await run.WaitForAsync("swapped");
        foreach (var link in install.Links) await Assert.That(new FileInfo(link.Path).LinkTarget).IsEqualTo(link.Replacement);

        run.Send("go");
        var result = await run.ExitAsync();

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(run.Words()).IsEquivalentTo(["swapped", "deleted"]);
        foreach (var (entry, _, _, _, _, _) in spec.Entries) {
            await Assert.That(Path.Exists(entry.Original)).IsFalse();
            await Assert.That(Path.Exists(entry.Aside)).IsFalse();
            await Assert.That(Path.Exists(AsideRetirement.CheckedAside(entry.Aside))).IsFalse();
        }
        await Assert.That(Directory.Exists(install.Scope)).IsTrue();
        await Assert.That(File.Exists(install.Sibling)).IsTrue();
        foreach (var link in install.Links) await Assert.That(new FileInfo(link.Path).LinkTarget).IsEqualTo(link.Replacement);
    }

    [Test]
    public async Task Put_back_restores_every_link_and_keeps_the_files() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        await using var run = Run.Start(Tmp, spec);

        await run.WaitForAsync("swapped");
        run.Send("put-back");
        var result = await run.ExitAsync();

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(run.Words()).IsEquivalentTo(["swapped", "restored"]);
        await install.AssertUntouchedAsync(spec);
    }

    [Test]
    public async Task Closing_the_control_pipe_before_go_restores_the_links() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        await using var run = Run.Start(Tmp, spec);

        await run.WaitForAsync("swapped");
        run.Control.Dispose();
        await run.ExitAsync();

        await Assert.That(run.Words()).IsEquivalentTo(["swapped", "restored"]);
        await install.AssertUntouchedAsync(spec);
    }

    [Test]
    public async Task No_word_within_the_wait_restores_the_links() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp, goWait: TimeSpan.FromSeconds(1));
        await using var run = Run.Start(Tmp, spec);

        await run.WaitForAsync("swapped");
        await run.ExitAsync();

        await Assert.That(run.Words()).IsEquivalentTo(["swapped", "restored"]);
        await install.AssertUntouchedAsync(spec);
    }

    [Test]
    public async Task A_link_changed_since_the_plan_is_left_and_the_rest_restored() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        var changed = install.Links[1].Path;
        var someone = Tmp.CreateFile("someone else/kcap");
        File.Delete(changed);
        File.CreateSymbolicLink(changed, someone);
        await using var run = Run.Start(Tmp, spec);

        var result = await run.ExitAsync();

        await Assert.That(result.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Words()).IsEquivalentTo(["restored"]);
        await Assert.That(new FileInfo(install.Links[0].Path).LinkTarget).IsEqualTo(install.Links[0].Original);
        await Assert.That(new FileInfo(changed).LinkTarget).IsEqualTo(someone);
        foreach (var (entry, _, _, _, _, _) in spec.Entries) await Assert.That(File.Exists(Path.Combine(entry.Original, "package.json"))).IsTrue();
    }

    /// <summary>Another account that can write a link's folder can plant a link to a directory at the staging name, which
    /// root must not follow into that directory. The second link's is planted, so the first, already swapped, comes
    /// back.</summary>
    [Test]
    public async Task A_link_planted_at_the_staging_name_is_not_followed() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        var trap    = Tmp.CreateDir("trap");
        var planted = install.Links[1].Path + ".kcap-swap-" + spec.Staging;
        Directory.CreateSymbolicLink(planted, trap);
        await using var run = Run.Start(Tmp, spec);

        var result = await run.ExitAsync();

        await Assert.That(result.ExitCode).IsEqualTo(4);
        await Assert.That(run.Words()).IsEquivalentTo(["restored"]);
        await Assert.That(Directory.EnumerateFileSystemEntries(trap)).IsEmpty();
        await Assert.That(new FileInfo(planted).LinkTarget).IsEqualTo(trap);
        await install.AssertUntouchedAsync(spec);
    }

    /// <summary>The staging name is the app's random part: one anyone could guess could be planted before the prompt.</summary>
    [Test]
    [Arguments("")]
    [Arguments("0123456789abcdef")]
    [Arguments("0123456789ABCDEF0123456789ABCDEF")]
    [Arguments("../../../../../../../../tmp/x/aa")]
    public async Task A_staging_name_that_is_not_random_hex_is_refused(string staging) {
        var refused = PrivilegedNpmInstall.Create(Tmp).Spec(Tmp) with { Staging = staging };

        await Assert.That(() => PrivilegedSwapScript.Render(refused)).Throws<ArgumentException>();
        await Assert.That(() => PrivilegedSwapScript.Arguments(refused, PrivilegedSwap.CurrentUid())).Throws<ArgumentException>();
    }

    /// <summary>The second package fails its own check after the first passed and took the checked name, so both come
    /// back.</summary>
    [Test]
    [Arguments("marker gone")]
    [Arguments("another version")]
    [Arguments("another package")]
    public async Task An_aside_tree_without_its_marker_is_moved_back(string change) {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        await using var run = Run.Start(Tmp, spec);

        await run.WaitForAsync("swapped");
        var marker = Path.Combine(spec.Entries[^1].Entry.Original, "package.json");
        switch (change) {
            case "marker gone":     File.Delete(marker); break;
            case "another version": File.WriteAllText(marker, """{"name":"@kurrent/kcap-darwin-arm64","version":"1.5.0"}"""); break;
            default:                File.WriteAllText(marker, $$"""{"name":"@kurrent/kcap","version":"{{PrivilegedNpmInstall.Version}}"}"""); break;
        }
        run.Send("go");
        var result = await run.ExitAsync();

        await Assert.That(result.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Words()).IsEquivalentTo(["swapped", "restored"]);
        foreach (var (entry, _, _, _, _, _) in spec.Entries) {
            await Assert.That(Directory.Exists(entry.Original)).IsTrue();
            await Assert.That(Path.Exists(entry.Aside)).IsFalse();
            await Assert.That(Path.Exists(AsideRetirement.CheckedAside(entry.Aside))).IsFalse();
        }
        await Assert.That(File.Exists(Path.Combine(spec.Entries[0].Entry.Original, "package.json"))).IsTrue();
        foreach (var link in install.Links) await Assert.That(new FileInfo(link.Path).LinkTarget).IsEqualTo(link.Original);
    }

    [Test]
    public async Task A_script_install_is_set_aside_checked_by_kind_and_deleted() {
        var (spec, root, link) = ScriptInstall();
        await using var run = Run.Start(Tmp, spec);

        await run.WaitForAsync("swapped");
        run.Send("go");
        var result = await run.ExitAsync();

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(run.Words()).IsEquivalentTo(["swapped", "deleted"]);
        foreach (var (entry, _, _, _, _, _) in spec.Entries) {
            await Assert.That(Path.Exists(entry.Original) || new FileInfo(entry.Original).LinkTarget is not null).IsFalse();
            await Assert.That(Path.Exists(entry.Aside) || new FileInfo(entry.Aside).LinkTarget is not null).IsFalse();
        }
        await Assert.That(File.Exists(Path.Combine(root, "data", "keep.json"))).IsTrue();
        await Assert.That(new FileInfo(link.Path).LinkTarget).IsEqualTo(link.Replacement);
    }

    /// <summary>An update moves current on to its own version while every entry keeps its kind: current must still name
    /// the recorded one, or the whole install goes back.</summary>
    [Test]
    public async Task A_script_current_naming_another_version_moves_the_install_back() {
        var (spec, root, link) = ScriptInstall();
        await using var run = Run.Start(Tmp, spec);

        await run.WaitForAsync("swapped");
        Tmp.CreateFile("opt kcap/versions/1.5.0/bin/kcap-daemon");
        File.Delete(Path.Combine(root, "current"));
        File.CreateSymbolicLink(Path.Combine(root, "current"), "versions/1.5.0");
        run.Send("go");
        await run.ExitAsync();

        await Assert.That(run.Words()).IsEquivalentTo(["swapped", "restored"]);
        await Assert.That(new FileInfo(Path.Combine(root, "current")).LinkTarget).IsEqualTo("versions/1.5.0");
        await Assert.That(Directory.Exists(Path.Combine(root, "versions", "1.5.0"))).IsTrue();
        await Assert.That(new FileInfo(link.Path).LinkTarget).IsEqualTo(link.Original);
    }

    /// <summary>A reinstall of the same version passes the marker check: only the daemon's identity tells it apart, so
    /// a package whose daemon is not the one preflight read moves the whole install back.</summary>
    [Test]
    public async Task A_package_whose_daemon_is_not_the_one_read_moves_the_install_back() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = WithDaemon(install.Spec(Tmp), "1:2:3:4");
        await using var run = Run.Start(Tmp, spec);

        await run.WaitForAsync("swapped");
        run.Send("go");
        await run.ExitAsync();

        await Assert.That(run.Words()).IsEquivalentTo(["swapped", "restored"]);
        await install.AssertUntouchedAsync(spec);
    }

    [Test]
    public async Task A_package_holding_the_daemon_preflight_read_is_deleted() {
        var install  = PrivilegedNpmInstall.Create(Tmp);
        var identity = await new EntryIdentity(new ProcessRunner(TimeProvider.System))
            .OfAsync(Path.Combine(install.Packages[1], "bin", "kcap"), CancellationToken.None);
        var spec = WithDaemon(install.Spec(Tmp), identity!);
        await using var run = Run.Start(Tmp, spec);

        await run.WaitForAsync("swapped");
        run.Send("go");
        var result = await run.ExitAsync();

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(run.Words()).IsEquivalentTo(["swapped", "deleted"]);
    }

    // The platform package holds the daemon; its kcap binary stands for it.
    static PrivilegedSpec WithDaemon(PrivilegedSpec spec, string identity) =>
        spec with { Entries = [spec.Entries[0], spec.Entries[1] with { Daemon = ("bin/kcap", identity) }] };

    [Test]
    public async Task A_kind_checked_entry_that_changed_kind_moves_the_install_back() {
        var (spec, root, link) = ScriptInstall();
        await using var run = Run.Start(Tmp, spec);

        await run.WaitForAsync("swapped");
        var marker = Path.Combine(root, "install.json");
        File.Delete(marker);
        Directory.CreateDirectory(marker);
        run.Send("go");
        await run.ExitAsync();

        await Assert.That(run.Words()).IsEquivalentTo(["swapped", "restored"]);
        await Assert.That(Directory.Exists(Path.Combine(root, "versions", "1.4.1"))).IsTrue();
        await Assert.That(new FileInfo(Path.Combine(root, "current")).LinkTarget).IsEqualTo("versions/1.4.1");
        await Assert.That(Directory.Exists(marker)).IsTrue();
        foreach (var (entry, _, _, _, _, _) in spec.Entries) await Assert.That(Path.Exists(entry.Aside)).IsFalse();
        await Assert.That(new FileInfo(link.Path).LinkTarget).IsEqualTo(link.Original);
    }

    [Test]
    public async Task Paths_with_spaces_and_quotes_are_passed_as_arguments() {
        const string Hostile = "it's \"a\" `touch $PWNED` $(touch $PWNED) dir";
        var install = PrivilegedNpmInstall.Create(Tmp, prefix: Hostile);
        var spec    = install.Spec(Tmp, helper: "helper " + Hostile);
        var pwned   = Tmp.PathTo("pwned");
        await using var run = Run.Start(Tmp, spec, new Dictionary<string, string> { ["PWNED"] = pwned });

        await run.WaitForAsync("swapped");
        run.Send("go");
        var result = await run.ExitAsync();

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(run.Words()).IsEquivalentTo(["swapped", "deleted"]);
        foreach (var (entry, _, _, _, _, _) in spec.Entries) await Assert.That(Path.Exists(entry.Original)).IsFalse();
        foreach (var link in install.Links) await Assert.That(new FileInfo(link.Path).LinkTarget).IsEqualTo(link.Replacement);
        await Assert.That(File.Exists(pwned)).IsFalse();
    }

    [Test]
    public async Task Every_file_stays_reachable_until_go() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        await using var run = Run.Start(Tmp, spec);

        await run.WaitForAsync("swapped");
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        foreach (var (entry, _, _, _, _, _) in spec.Entries) {
            await Assert.That(File.Exists(Path.Combine(entry.Original, "package.json"))).IsTrue();
            await Assert.That(Path.Exists(entry.Aside)).IsFalse();
        }
        foreach (var link in install.Links) {
            await Assert.That(File.Exists(link.Path)).IsTrue();
            await Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(link.Path)!, link.Original))).IsTrue();
        }

        run.Send("go");
        await run.ExitAsync();

        await Assert.That(run.Words()).IsEquivalentTo(["swapped", "deleted"]);
    }

    /// <summary>One an earlier run checked is deleted as it is: it passed every check before it took the checked
    /// name.</summary>
    [Test]
    public async Task A_checked_aside_an_earlier_run_left_is_deleted_on_go() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        var left    = spec.Entries[^1].Entry;
        Directory.Move(left.Original, AsideRetirement.CheckedAside(left.Aside));
        File.Delete(Path.Combine(AsideRetirement.CheckedAside(left.Aside), "package.json"));
        await using var run = Run.Start(Tmp, spec);

        await run.WaitForAsync("swapped");
        run.Send("go");
        await run.ExitAsync();

        await Assert.That(run.Words()).IsEquivalentTo(["swapped", "deleted"]);
        foreach (var (entry, _, _, _, _, _) in spec.Entries) {
            await Assert.That(Path.Exists(entry.Original)).IsFalse();
            await Assert.That(Path.Exists(entry.Aside)).IsFalse();
            await Assert.That(Path.Exists(AsideRetirement.CheckedAside(entry.Aside))).IsFalse();
        }
    }

    /// <summary>One an earlier run set aside and never checked may be a tree that replaced the install while the
    /// prompt was open: it is checked like a fresh one, and one that fails goes back to its name with the
    /// rest.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task An_unchecked_aside_an_earlier_run_left_is_checked_on_go(bool passes) {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        var left    = spec.Entries[^1].Entry;
        Directory.Move(left.Original, left.Aside);
        if (!passes) File.WriteAllText(Path.Combine(left.Aside, "package.json"), """{"name":"@kurrent/kcap-darwin-arm64","version":"1.5.0"}""");
        await using var run = Run.Start(Tmp, spec);

        await run.WaitForAsync("swapped");
        run.Send("go");
        await run.ExitAsync();

        string[] words = passes ? ["swapped", "deleted"] : ["swapped", "restored"];
        await Assert.That(run.Words()).IsEquivalentTo(words);
        foreach (var (entry, _, _, _, _, _) in spec.Entries) {
            await Assert.That(Path.Exists(entry.Original)).IsEqualTo(!passes);
            await Assert.That(Path.Exists(entry.Aside)).IsFalse();
            await Assert.That(Path.Exists(AsideRetirement.CheckedAside(entry.Aside))).IsFalse();
        }
        if (!passes) await Assert.That(File.ReadAllText(Path.Combine(left.Original, "package.json"))).Contains("1.5.0");
    }

    /// <summary>A process of any account mapping a file of the install keeps it, however it was started: this one runs
    /// through a link outside the install, the path a process listing would show.</summary>
    [Test]
    public async Task A_process_running_from_the_install_keeps_it_on_go() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        var daemon  = Tmp.CopyExecutable("/bin/sleep", Path.GetRelativePath(Tmp.Path, Path.Combine(install.Packages[1], "bin", "kcap-daemon")));
        var signed  = await new ProcessRunner(TimeProvider.System).RunAsync(
            "/usr/bin/codesign", ["--force", "--sign", "-", daemon], new RunOptions(null, TimeSpan.FromSeconds(30)), CancellationToken.None);
        await Assert.That(signed.ExitCode).IsEqualTo(0);
        var started = Tmp.CreateDir("service").PathTo("kcap-daemon");
        File.CreateSymbolicLink(started, daemon);
        using var running = Process.Start(new ProcessStartInfo(started, "60") { UseShellExecute = false })!;
        try {
            await using var run = Run.Start(Tmp, spec);

            await run.WaitForAsync("swapped");
            run.Send("go");
            var result = await run.ExitAsync();

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(run.Words()).IsEquivalentTo(["swapped", "in-use"]);
            await Assert.That(result.Stderr).Contains($"process {running.Id} runs");
            await install.AssertUntouchedAsync(spec);
        } finally {
            running.Kill();
        }
    }

    /// <summary>Another account's daemon service names the install in its launch agent, which only root can read.</summary>
    [Test]
    public async Task A_launch_agent_in_any_home_naming_the_install_keeps_it_on_go() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        var plist   = Path.Combine(spec.Homes, "other", "Library", "LaunchAgents", "io.kurrent.kcap.daemon.default.plist");
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, plist), $"<string>{Path.Combine(install.Packages[1], "bin", "kcap")}</string>");
        await using var run = Run.Start(Tmp, spec);

        await run.WaitForAsync("swapped");
        run.Send("go");
        var result = await run.ExitAsync();

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(run.Words()).IsEquivalentTo(["swapped", "in-use"]);
        await Assert.That(result.Stderr).Contains(plist);
        await install.AssertUntouchedAsync(spec);
    }

    /// <summary>A launch agent that is not a kcap daemon's names nothing kcap runs.</summary>
    [Test]
    public async Task Another_launch_agent_naming_the_install_does_not_keep_it() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        Tmp.CreateFile(Path.Combine("homes", "other", "Library", "LaunchAgents", "com.example.other.plist"), install.Packages[1]);
        await using var run = Run.Start(Tmp, spec);

        await run.WaitForAsync("swapped");
        run.Send("go");
        await run.ExitAsync();

        await Assert.That(run.Words()).IsEquivalentTo(["swapped", "deleted"]);
    }

    /// <summary>Root creates the status file, so a folder that is a link, is not 0700 or is not this user's ends the run
    /// before it writes anything.</summary>
    [Test]
    [Arguments("linked folder")]
    [Arguments("mode 0755")]
    [Arguments("another owner")]
    public async Task A_status_folder_that_is_not_this_users_private_folder_is_refused(string trap) {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp, helper: "helper");
        var real    = Tmp.CreateDir("real helper");
        File.SetUnixFileMode(real, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (trap == "linked folder") Directory.CreateSymbolicLink(Tmp.PathTo("helper"), real);
        var owner = trap == "another owner" ? PrivilegedSwap.CurrentUid() + 1 : PrivilegedSwap.CurrentUid();

        await using var run = Run.Start(Tmp, spec, arrange: () => {
            if (trap == "mode 0755")
                File.SetUnixFileMode(Tmp.PathTo("helper"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }, owner: owner);
        var result = await run.ExitAsync();

        await Assert.That(result.ExitCode).IsEqualTo(2);
        await Assert.That(new FileInfo(spec.StatusFile).LinkTarget is null && !File.Exists(spec.StatusFile)).IsTrue();
        await Assert.That(File.Exists(Path.Combine(real, "status"))).IsFalse();
        await install.AssertUntouchedAsync(spec);
    }

    [Test]
    [Arguments("occupied")]
    [Arguments("dangling link")]
    [Arguments("missing folder")]
    public async Task A_status_path_the_script_cannot_create_ends_the_run_before_any_write(string trap) {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp, helper: "status folder");
        var target  = Tmp.PathTo("link target");

        await using var run = Run.Start(Tmp, spec, arrange: () => {
            switch (trap) {
                case "occupied":      File.WriteAllText(spec.StatusFile, "keep"); break;
                case "dangling link": File.CreateSymbolicLink(spec.StatusFile, target); break;
                default:
                    File.Delete(spec.ControlFifo);
                    Directory.Delete(Tmp.PathTo("status folder"));
                    break;
            }
        });
        var result = await run.ExitAsync();

        await Assert.That(result.ExitCode).IsEqualTo(2);
        switch (trap) {
            case "occupied":      await Assert.That(File.ReadAllText(spec.StatusFile)).IsEqualTo("keep"); break;
            case "dangling link": await Assert.That(Path.Exists(target)).IsFalse(); break;
            default:              await Assert.That(Directory.Exists(Tmp.PathTo("status folder"))).IsFalse(); break;
        }
        await install.AssertUntouchedAsync(spec);
    }

    /// <summary>A control path that is not the app's own pipe is never opened for writing, and no link is swapped.</summary>
    [Test]
    [Arguments("regular file")]
    [Arguments("link to a pipe")]
    public async Task A_control_path_that_is_not_the_pipe_ends_the_run_before_any_change(string trap) {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);

        await using var run = Run.Start(Tmp, spec, arrange: () => {
            if (trap == "regular file") {
                File.Delete(spec.ControlFifo);
                File.WriteAllText(spec.ControlFifo, "go\n");
            } else {
                File.Move(spec.ControlFifo, Tmp.PathTo("helper", "real control"));
                File.CreateSymbolicLink(spec.ControlFifo, Tmp.PathTo("helper", "real control"));
            }
        });
        var result = await run.ExitAsync();

        await Assert.That(result.ExitCode).IsEqualTo(3);
        await Assert.That(run.Words()).IsEquivalentTo(["restored"]);
        if (trap == "regular file") await Assert.That(File.ReadAllText(spec.ControlFifo)).IsEqualTo("go\n");
        await install.AssertUntouchedAsync(spec);
    }

    /// <summary>The kind is the app's, fixed before the prompt: an entry that was something else already when the helper
    /// started is moved back with the rest.</summary>
    [Test]
    [Arguments("script current is a directory")]
    [Arguments("npm package is a link")]
    public async Task An_entry_that_is_not_its_expected_kind_moves_the_install_back(string trap) {
        PrivilegedSpec spec;
        IReadOnlyList<LinkChange> links;
        if (trap == "script current is a directory") {
            (spec, var root, var link) = ScriptInstall();
            File.Delete(Path.Combine(root, "current"));
            Directory.CreateDirectory(Path.Combine(root, "current", "bin"));
            links = [link];
        } else {
            var install  = PrivilegedNpmInstall.Create(Tmp);
            spec         = install.Spec(Tmp);
            links        = install.Links;
            var launcher = spec.Entries[0].Entry.Original;
            var checkout = Tmp.PathTo("checkout");
            Directory.Move(launcher, checkout);
            Directory.CreateSymbolicLink(launcher, checkout);
        }
        var before = spec.Entries.Select(e => new FileInfo(e.Entry.Original).LinkTarget ?? (Directory.Exists(e.Entry.Original) ? "d" : "f")).ToList();
        await using var run = Run.Start(Tmp, spec);

        await run.WaitForAsync("swapped");
        run.Send("go");
        var result = await run.ExitAsync();

        await Assert.That(result.ExitCode).IsEqualTo(5);
        await Assert.That(run.Words()).IsEquivalentTo(["swapped", "restored"]);
        await Assert.That(spec.Entries.Select(e => new FileInfo(e.Entry.Original).LinkTarget ?? (Directory.Exists(e.Entry.Original) ? "d" : "f")).ToList())
            .IsEquivalentTo(before);
        foreach (var (entry, _, _, _, _, _) in spec.Entries) await Assert.That(Path.Exists(entry.Aside) || new FileInfo(entry.Aside).LinkTarget is not null).IsFalse();
        foreach (var link in links) await Assert.That(new FileInfo(link.Path).LinkTarget).IsEqualTo(link.Original);
    }

    /// <summary>Refused before the prompt: nothing reaches root without the check its kind needs.</summary>
    [Test]
    [Arguments("npm folder without a marker")]
    [Arguments("marker without substrings")]
    [Arguments("marker on a link")]
    [Arguments("script entry as another kind")]
    [Arguments("substrings without a marker")]
    public async Task An_entry_without_the_check_it_needs_is_refused(string trap) {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        var (aside, kind, marker, contains, _, _) = spec.Entries[0];
        var (scriptSpec, _, _) = ScriptInstall();
        PrivilegedEntry entry = trap switch {
            "npm folder without a marker"  => new(aside, kind, null, []),
            "marker without substrings"    => new(aside, kind, marker, []),
            "marker on a link"             => new(aside, PrivilegedEntryKind.Link, marker, contains),
            "script entry as another kind" => scriptSpec.Entries[1] with { Kind = PrivilegedEntryKind.Directory },
            _                              => scriptSpec.Entries[2] with { MarkerContains = ["stable"] },
        };
        var refused = spec with { Entries = [entry] };

        await Assert.That(() => PrivilegedSwapScript.Render(refused)).Throws<ArgumentException>();
        await Assert.That(() => PrivilegedSwapScript.Arguments(refused, PrivilegedSwap.CurrentUid())).Throws<ArgumentException>();
    }

    /// <summary>The folder check sees the status file's folder as written, and the control pipe must sit in it too.</summary>
    [Test]
    [Arguments("double slash")]
    [Arguments("dot segment")]
    [Arguments("control pipe elsewhere")]
    public async Task A_channel_path_the_folder_check_would_not_cover_is_refused(string trap) {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        var linked  = Tmp.PathTo("linked helper");
        Directory.CreateSymbolicLink(linked, Tmp.CreateDir("real helper"));
        var refused = trap switch {
            "double slash" => spec with { ControlFifo = linked + "//control", StatusFile = linked + "//status" },
            "dot segment"  => spec with { ControlFifo = linked + "/./control", StatusFile = linked + "/./status" },
            _              => spec with { ControlFifo = Tmp.PathTo("elsewhere", "control") },
        };

        await Assert.That(() => PrivilegedSwapScript.Render(refused)).Throws<ArgumentException>();
        await Assert.That(() => PrivilegedSwapScript.Arguments(refused, PrivilegedSwap.CurrentUid())).Throws<ArgumentException>();
    }

    static readonly (string Name, PrivilegedEntryKind Kind)[] Layout = [
        ("versions", PrivilegedEntryKind.Directory), ("current", PrivilegedEntryKind.Link), ("install.json", PrivilegedEntryKind.File),
    ];

    /// <summary>A script install's root as a root-owned install folder would hold it: <c>versions</c>, a relative
    /// <c>current</c> link and <c>install.json</c>, each checked by kind only, beside data the installer does not
    /// own; a PATH link points through <c>current</c>.</summary>
    (PrivilegedSpec Spec, string Root, LinkChange Link) ScriptInstall() {
        var root = Tmp.CreateDir("opt kcap");
        Tmp.CreateFile("opt kcap/versions/1.4.1/bin/kcap");
        Tmp.CreateFile("opt kcap/versions/1.4.1/bin/kcap-daemon");
        File.CreateSymbolicLink(Path.Combine(root, "current"), "versions/1.4.1");
        Tmp.CreateFile("opt kcap/install.json", """{"channel":"stable"}""");
        Tmp.CreateFile("opt kcap/data/keep.json", "{}");
        var bundle = Tmp.CreateFile("Applications/kcap.app/Contents/MacOS/kcap");
        var path   = Tmp.CreateDir("local bin").PathTo("kcap");
        var old    = Path.Combine(root, "current", "bin", "kcap");
        File.CreateSymbolicLink(path, old);

        var retirement = new AsideRetirement(new ConfigRoot(Tmp.CreateDir("config")), "cli-adoption-retire");
        var link       = new LinkChange(path, old, bundle);
        var spec = new PrivilegedSpec(
            [link], [.. Layout.Select(e => new PrivilegedEntry(retirement.Plan(Path.Combine(root, e.Name)), e.Kind, null, [],
                LinkTargetName: e.Kind == PrivilegedEntryKind.Link ? "1.4.1" : null))],
            Tmp.PathTo("helper", "control"), Tmp.PathTo("helper", "status"), TimeSpan.FromSeconds(30), PrivilegedNpmInstall.Staging,
            Tmp.CreateDir("homes"));
        return (spec, root, link);
    }

    /// <summary>The rendered script running as the current user, with the control pipe held open for writing.</summary>
    sealed class Run(PrivilegedSpec spec, FileStream control, Task<ProcessResult> exit) : IAsyncDisposable {
        public FileStream Control { get; } = control;

        /// <param name="arrange">Runs after the channel is made and before the script starts.</param>
        /// <param name="owner">The uid the script requires of the status folder; this user's by default.</param>
        public static Run Start(
                TempDir tmp, PrivilegedSpec spec, IReadOnlyDictionary<string, string>? env = null, Action? arrange = null, uint? owner = null) {
            PrivilegedSwap.CreateChannel(spec);
            var control = PrivilegedSwap.OpenControl(spec.ControlFifo);
            arrange?.Invoke();
            var script = tmp.CreateExecutable("privileged-" + Guid.NewGuid().ToString("N") + ".sh", PrivilegedSwapScript.Render(spec));
            var exit   = new ProcessRunner(TimeProvider.System).RunAsync(
                script, [.. PrivilegedSwapScript.Arguments(spec, owner ?? PrivilegedSwap.CurrentUid())],
                new RunOptions(env, TimeSpan.FromMinutes(1)), CancellationToken.None);
            return new Run(spec, control, exit);
        }

        public void Send(string word) {
            Control.Write(Encoding.ASCII.GetBytes(word + "\n"));
            Control.Flush();
        }

        public async Task WaitForAsync(string word) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (!Words().Contains(word)) {
                if (exit.IsCompleted) throw new InvalidOperationException($"The script exited before '{word}': {(await exit).Stderr}");
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"No '{word}' in {spec.StatusFile}.");
                await Task.Delay(20);
            }
        }

        public async Task<ProcessResult> ExitAsync() {
            var result = await exit.WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.That(result.TimedOut).IsFalse();
            return result;
        }

        /// <summary>The status file's words after the pid line, which must be the script's own.</summary>
        public string[] Words() {
            if (!File.Exists(spec.StatusFile)) return [];
            var lines = File.ReadAllLines(spec.StatusFile);
            return lines.Length > 0 && int.TryParse(lines[0], out var pid) && pid > 0 ? lines[1..] : ["<no pid>", .. lines];
        }

        public async ValueTask DisposeAsync() {
            Control.Dispose();
            await exit.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}
