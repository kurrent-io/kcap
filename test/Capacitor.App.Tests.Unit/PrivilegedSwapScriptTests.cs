using System.Runtime.Versioning;
using System.Text;
using TUnit.Core.Enums;
using Capacitor.App.Services.CliAdoption;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// <summary>Runs the rendered helper script as the current user against a fake npm prefix, driving its control pipe the
/// way <see cref="PrivilegedSwap"/> does.</summary>
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
        foreach (var entry in spec.Entries) {
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
        foreach (var entry in spec.Entries) await Assert.That(File.Exists(Path.Combine(entry.Original, "package.json"))).IsTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task An_aside_tree_without_its_marker_is_moved_back(bool markerGone) {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        await using var run = Run.Start(Tmp, spec);

        await run.WaitForAsync("swapped");
        var marker = Path.Combine(spec.Entries[^1].Original, "package.json");
        if (markerGone) File.Delete(marker);
        else File.WriteAllText(marker, """{"name":"@kurrent/kcap-darwin-arm64","version":"1.5.0"}""");
        run.Send("go");
        var result = await run.ExitAsync();

        await Assert.That(result.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Words()).IsEquivalentTo(["swapped", "restored"]);
        foreach (var entry in spec.Entries) {
            await Assert.That(Directory.Exists(entry.Original)).IsTrue();
            await Assert.That(Path.Exists(entry.Aside)).IsFalse();
        }
        await Assert.That(File.Exists(Path.Combine(spec.Entries[0].Original, "package.json"))).IsTrue();
        foreach (var link in install.Links) await Assert.That(new FileInfo(link.Path).LinkTarget).IsEqualTo(link.Original);
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
        foreach (var entry in spec.Entries) await Assert.That(Path.Exists(entry.Original)).IsFalse();
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

        foreach (var entry in spec.Entries) {
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
        Directory.Move(spec.Entries[^1].Original, spec.Entries[^1].Aside);
        File.Delete(Path.Combine(spec.Entries[^1].Aside, "package.json"));
        await using var run = Run.Start(Tmp, spec);

        await run.WaitForAsync("swapped");
        run.Send("go");
        await run.ExitAsync();

        await Assert.That(run.Words()).IsEquivalentTo(["swapped", "deleted"]);
        foreach (var entry in spec.Entries) {
            await Assert.That(Path.Exists(entry.Original)).IsFalse();
            await Assert.That(Path.Exists(entry.Aside)).IsFalse();
        }
    }

    /// <summary>The rendered script running as the current user, with the control pipe held open for writing.</summary>
    sealed class Run(PrivilegedSpec spec, FileStream control, Task<ProcessResult> exit) : IAsyncDisposable {
        public FileStream Control { get; } = control;

        public static Run Start(TempDir tmp, PrivilegedSpec spec, IReadOnlyDictionary<string, string>? env = null) {
            PrivilegedSwap.CreateChannel(spec);
            var control = PrivilegedSwap.OpenControl(spec.ControlFifo);
            var script  = tmp.CreateExecutable("privileged-" + Guid.NewGuid().ToString("N") + ".sh", PrivilegedSwapScript.Render(spec));
            var exit    = new ProcessRunner(TimeProvider.System).RunAsync(
                script, [.. PrivilegedSwapScript.Arguments(spec)], new RunOptions(env, TimeSpan.FromMinutes(1)), CancellationToken.None);
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
