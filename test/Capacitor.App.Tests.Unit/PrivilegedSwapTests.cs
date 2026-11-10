using System.Runtime.Versioning;
using TUnit.Core.Enums;
using Capacitor.App.Services;
using Capacitor.App.Services.CliAdoption;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

[RunOn(OS.MacOs), UnsupportedOSPlatform("windows"), ParallelLimiter<SubprocessLimit>]
public class PrivilegedSwapTests {
    const string Prompt = "kcap wants to remove the npm install of kcap that every account on this Mac shares.";

    [TempDir] public required TempDir Tmp { get; init; }

    const string Privileges = " with prompt (item 1 of argv) with administrator privileges";

    /// <summary>Stands in for the elevated osascript, as the current user: runs the script it was handed with
    /// <c>/bin/bash</c>, or answers <paramref name="answer"/> without running anything. With
    /// <paramref name="throughAppleScript"/> it runs osascript itself on the AppleScript stripped of its privileges,
    /// with <c>PWNED</c> naming a file only an evaluated path would create. <paramref name="script"/> stands in for the
    /// script it was handed.</summary>
    sealed class Elevated(ProcessResult? answer = null, bool throughAppleScript = false, string? pwned = null, string? script = null)
        : IProcessRunner {
        public List<(string File, string[] Args, RunOptions Options)> Ran { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Ran.Add((fileName, args, options));
            if (answer is not null) return Task.FromResult(answer);

            var runner = new ProcessRunner(TimeProvider.System);
            if (throughAppleScript) {
                var plain = args[1].Replace(Privileges, "", StringComparison.Ordinal);
                if (plain.Contains("administrator", StringComparison.Ordinal)) throw new InvalidOperationException("The AppleScript would still prompt.");
                var env = new Dictionary<string, string> { ["PWNED"] = pwned! };
                return runner.RunAsync(fileName, [args[0], plain, .. args[2..]], options with { EnvOverlay = env }, ct);
            }

            var rest = args[(Array.IndexOf(args, "--") + 1)..];
            return runner.RunAsync("/bin/bash", ["-c", script ?? rest[1], "kcap-privileged", .. rest[2..]], options, ct);
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options, Action<StreamedLine> onLine,
            CancellationToken ct) => throw new NotSupportedException();
    }

    AdoptionRecordStore Store() => new(new ConfigRoot(Tmp.CreateDir("config")));

    [Test]
    public async Task A_verified_swap_records_the_helper_and_go_before_deleting() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        var store   = Store();
        PrivilegedOperation? duringVerify = null;
        var linksDuringVerify = new List<string?>();

        var result = await new PrivilegedSwap(new Elevated(), store).RunAsync(install.Packages[0], spec, Prompt, _ => {
            duringVerify = store.Load().Privileged;
            linksDuringVerify.AddRange(install.Links.Select(l => new FileInfo(l.Path).LinkTarget));
            return Task.FromResult(true);
        }, CancellationToken.None);

        await Assert.That(result).IsEqualTo(PrivilegedResult.Deleted);
        await Assert.That(duringVerify).IsNotNull();
        await Assert.That(duringVerify!.InstallRoot).IsEqualTo(install.Packages[0]);
        await Assert.That(duringVerify.Pid).IsNotNull();
        await Assert.That(duringVerify.GoSent).IsFalse();
        await Assert.That(linksDuringVerify).IsEquivalentTo(install.Links.Select(l => (string?)l.Replacement));
        await Assert.That(store.Load().Privileged).IsEqualTo(duringVerify with { GoSent = true });
        foreach (var (entry, _, _, _, _, _) in spec.Entries) await Assert.That(Path.Exists(entry.Original)).IsFalse();
        await Assert.That(Path.Exists(spec.ControlFifo)).IsFalse();
        await Assert.That(Path.Exists(spec.StatusFile)).IsFalse();
    }

    [Test]
    public async Task A_failed_verify_puts_the_links_back() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        var store   = Store();

        var result = await new PrivilegedSwap(new Elevated(), store).RunAsync(
            install.Packages[0], spec, Prompt, _ => Task.FromResult(false), CancellationToken.None);

        await Assert.That(result).IsEqualTo(PrivilegedResult.PutBack);
        await Assert.That(store.Load().Privileged!.GoSent).IsFalse();
        await install.AssertUntouchedAsync(spec);
    }

    /// <summary>The helper's own report that another account uses the install, after go, with the links put back.</summary>
    [Test]
    public async Task An_install_another_account_uses_is_reported_in_use() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        Tmp.CreateFile(Path.Combine("homes", "other", "Library", "LaunchAgents", "io.kurrent.kcap.daemon.default.plist"), install.Packages[1]);

        var result = await new PrivilegedSwap(new Elevated(), Store()).RunAsync(
            install.Packages[0], spec, Prompt, _ => Task.FromResult(true), CancellationToken.None);

        await Assert.That(result).IsEqualTo(PrivilegedResult.InUse);
        await install.AssertUntouchedAsync(spec);
    }

    [Test]
    public async Task A_cancelled_prompt_changes_nothing_and_every_path_travels_as_an_argument() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        var store   = Store();
        var runner  = new Elevated(new ProcessResult(1, "", "execution error: User canceled. (-128)", false));
        var verified = false;

        var result = await new PrivilegedSwap(runner, store).RunAsync(install.Packages[0], spec, Prompt, _ => {
            verified = true;
            return Task.FromResult(true);
        }, CancellationToken.None);

        await Assert.That(result).IsEqualTo(PrivilegedResult.Cancelled);
        await Assert.That(verified).IsFalse();
        await Assert.That(store.Load().Privileged).IsNull();
        await install.AssertUntouchedAsync(spec);

        var (file, args, options) = runner.Ran.Single();
        await Assert.That(file).IsEqualTo("/usr/bin/osascript");
        await Assert.That(args[0]).IsEqualTo("-e");
        await Assert.That(args[1]).Contains(Privileges);
        await Assert.That(args[2]).IsEqualTo("--");
        await Assert.That(args[3..]).IsEquivalentTo([Prompt, PrivilegedSwapScript.Render(spec), .. PrivilegedSwapScript.Arguments(spec, PrivilegedSwap.CurrentUid())]);
        foreach (var path in spec.Links.Select(l => l.Path).Concat(spec.Entries.Select(e => e.Entry.Original)).Append(spec.StatusFile)) {
            await Assert.That(args[1]).DoesNotContain(path);
            await Assert.That(PrivilegedSwapScript.Render(spec)).DoesNotContain(path);
        }
        await Assert.That(options.CancelMode).IsEqualTo(CancelMode.KillTree);
    }

    [Test]
    public async Task An_osascript_failure_before_the_helper_reported_its_pid_is_cancelled() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        var store   = Store();
        var runner  = new Elevated(new ProcessResult(1, "", "execution error: The administrator user name or password was incorrect. (-60007)", false));

        var result = await new PrivilegedSwap(runner, store).RunAsync(
            install.Packages[0], spec, Prompt, _ => Task.FromResult(true), CancellationToken.None);

        await Assert.That(result).IsEqualTo(PrivilegedResult.Cancelled);
        await Assert.That(store.Load().Privileged).IsNull();
        await install.AssertUntouchedAsync(spec);
    }

    [Test]
    public async Task A_helper_that_ran_without_reporting_an_outcome_is_cut_short() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        var store   = Store();
        var runner  = new Elevated(script: "echo $$ > \"$1\"; exit 9");

        var result = await new PrivilegedSwap(runner, store).RunAsync(
            install.Packages[0], spec, Prompt, _ => Task.FromResult(true), CancellationToken.None);

        await Assert.That(result).IsEqualTo(PrivilegedResult.CutShort);
        await Assert.That(store.Load().Privileged!.GoSent).IsFalse();
    }

    [Test]
    public async Task The_AppleScript_hands_bash_every_argument_as_one_word() {
        const string Hostile = "it's \"a\" `touch $PWNED` $(touch $PWNED) dir";
        var install = PrivilegedNpmInstall.Create(Tmp, prefix: Hostile);
        var spec    = install.Spec(Tmp, helper: "helper " + Hostile);
        var pwned   = Tmp.PathTo("pwned");

        var result = await new PrivilegedSwap(new Elevated(throughAppleScript: true, pwned: pwned), Store()).RunAsync(
            install.Packages[0], spec, Prompt, _ => Task.FromResult(true), CancellationToken.None);

        await Assert.That(result).IsEqualTo(PrivilegedResult.Deleted);
        foreach (var (entry, _, _, _, _, _) in spec.Entries) await Assert.That(Path.Exists(entry.Original)).IsFalse();
        foreach (var link in install.Links) await Assert.That(new FileInfo(link.Path).LinkTarget).IsEqualTo(link.Replacement);
        await Assert.That(File.Exists(pwned)).IsFalse();
    }

    /// <summary>A check that throws has not passed: the helper puts everything back and is awaited for its answer.</summary>
    [Test]
    public async Task A_check_that_throws_puts_everything_back() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);

        var result = await new PrivilegedSwap(new Elevated(), Store()).RunAsync(
            install.Packages[0], spec, Prompt, _ => throw new IOException("the login shell did not answer"), CancellationToken.None);

        await Assert.That(result).IsEqualTo(PrivilegedResult.PutBack);
        await install.AssertUntouchedAsync(spec);
    }

    [Test]
    public async Task A_deletion_that_fails_after_go_is_cut_short() {
        var install = PrivilegedNpmInstall.Create(Tmp);
        var spec    = install.Spec(Tmp);
        var store   = Store();
        var locked  = Directory.CreateDirectory(Path.Combine(install.Packages[^1], "locked"));
        File.WriteAllText(Path.Combine(locked.FullName, "file"), "");
        File.SetUnixFileMode(locked.FullName, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try {
            var result = await new PrivilegedSwap(new Elevated(), store).RunAsync(
                install.Packages[0], spec, Prompt, _ => Task.FromResult(true), CancellationToken.None);

            await Assert.That(result).IsEqualTo(PrivilegedResult.CutShort);
            await Assert.That(store.Load().Privileged!.GoSent).IsTrue();
        } finally {
            var aside = spec.Entries[^1].Entry.Aside;
            string[] places = [locked.FullName, Path.Combine(aside, "locked"), Path.Combine(AsideRetirement.CheckedAside(aside), "locked")];
            foreach (var dir in places.Where(Directory.Exists))
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
