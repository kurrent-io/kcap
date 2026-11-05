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
        foreach (var (entry, _, _) in spec.Entries) {
            await Assert.That(Path.Exists(entry.Original)).IsFalse();
            await Assert.That(Path.Exists(entry.Aside)).IsFalse();
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
        foreach (var (entry, _, _) in spec.Entries) await Assert.That(File.Exists(Path.Combine(entry.Original, "package.json"))).IsTrue();
    }

    /// <summary>The second package fails its own check after the first passed, so both come back.</summary>
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
        foreach (var (entry, _, _) in spec.Entries) {
            await Assert.That(Directory.Exists(entry.Original)).IsTrue();
            await Assert.That(Path.Exists(entry.Aside)).IsFalse();
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
        foreach (var (entry, _, _) in spec.Entries) {
            await Assert.That(Path.Exists(entry.Original) || new FileInfo(entry.Original).LinkTarget is not null).IsFalse();
            await Assert.That(Path.Exists(entry.Aside) || new FileInfo(entry.Aside).LinkTarget is not null).IsFalse();
        }
        await Assert.That(File.Exists(Path.Combine(root, "data", "keep.json"))).IsTrue();
        await Assert.That(new FileInfo(link.Path).LinkTarget).IsEqualTo(link.Replacement);
    }

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
        foreach (var (entry, _, _) in spec.Entries) await Assert.That(Path.Exists(entry.Aside)).IsFalse();
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
        foreach (var (entry, _, _) in spec.Entries) await Assert.That(Path.Exists(entry.Original)).IsFalse();
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

        foreach (var (entry, _, _) in spec.Entries) {
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

    [Test]
    public async Task An_aside_an_earlier_run_left_is_deleted_on_go() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        var left    = spec.Entries[^1].Entry;
        Directory.Move(left.Original, left.Aside);
        File.Delete(Path.Combine(left.Aside, "package.json"));
        await using var run = Run.Start(Tmp, spec);

        await run.WaitForAsync("swapped");
        run.Send("go");
        await run.ExitAsync();

        await Assert.That(run.Words()).IsEquivalentTo(["swapped", "deleted"]);
        foreach (var (entry, _, _) in spec.Entries) {
            await Assert.That(Path.Exists(entry.Original)).IsFalse();
            await Assert.That(Path.Exists(entry.Aside)).IsFalse();
        }
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
        var spec    = install.Spec(Tmp) with { StatusFile = Tmp.PathTo("status folder", "status") };
        var target  = Tmp.PathTo("link target");

        await using var run = Run.Start(Tmp, spec, arrange: () => {
            switch (trap) {
                case "occupied":      File.WriteAllText(spec.StatusFile, "keep"); break;
                case "dangling link": File.CreateSymbolicLink(spec.StatusFile, target); break;
                default:              Directory.Delete(Tmp.PathTo("status folder")); break;
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
            [link], [.. new[] { "versions", "current", "install.json" }.Select(n => new PrivilegedEntry(retirement.Plan(Path.Combine(root, n)), null, []))],
            Tmp.PathTo("helper", "control"), Tmp.PathTo("helper", "status"), TimeSpan.FromSeconds(30));
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
