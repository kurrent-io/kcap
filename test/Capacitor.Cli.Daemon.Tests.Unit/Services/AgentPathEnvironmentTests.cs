using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Services;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

[NotInParallel, RunOn(OS.MacOs | OS.Linux)]
public class AgentPathEnvironmentTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Restricted_setup_PATH_and_a_later_install_both_discover_and_launch() {
        using var path = EnvScope.Exclusive("PATH", "/usr/bin:/bin");
        var pathFile = Tmp.CreateFile("terminal-path", "/usr/bin:/bin");
        var shell = Tmp.CreateExecutable("shell", $"#!/bin/sh\nprintf '<<KCAP-PATH>>%s<<KCAP-PATH>>' \"$(/bin/cat '{pathFile}')\"\n");
        using var shellEnv = EnvScope.Exclusive("SHELL", shell);
        var runner = new ProcessRunner(TimeProvider.System);
        var environment = new AgentPathEnvironment(runner);
        var binaries = BinaryProbe.Tracking(() => Environment.GetEnvironmentVariable("PATH"));
        await environment.RefreshAsync(CancellationToken.None);
        await Assert.That(binaries.Resolve("late-agent")).IsNull();

        var agent = Tmp.CreateExecutable("late-agent", "#!/usr/bin/env late-agent-runtime\n");
        Tmp.CreateExecutable("late-agent-runtime", "#!/bin/sh\nprintf 'launched-with-current-path'\n");
        await File.WriteAllTextAsync(pathFile, Tmp.Path + ":/usr/bin:/bin");
        await environment.RefreshAsync(CancellationToken.None);

        await Assert.That(binaries.Resolve("late-agent")).IsEqualTo(agent);
        var launched = await runner.RunAsync(agent, [], new RunOptions(Timeout: TimeSpan.FromSeconds(5)), CancellationToken.None);
        await Assert.That(launched.ExitCode).IsEqualTo(0);
        await Assert.That(launched.Stdout).IsEqualTo("launched-with-current-path");
    }

    [Test, RunOn(OS.MacOs)]
    public async Task Real_login_shell_reloads_startup_files_without_retaining_removed_directories() {
        using var path = EnvScope.Exclusive("PATH", "/usr/bin:/bin");
        using var shell = EnvScope.Exclusive("SHELL", "/bin/zsh");
        using var dotdir = EnvScope.Exclusive("ZDOTDIR", Tmp.Path);
        var oldDirectory = Tmp.PathTo("old-bin");
        var newDirectory = Tmp.PathTo("new-bin");
        var rc = Tmp.CreateFile(".zshrc", $"export PATH=\"{oldDirectory}:$PATH\"\n");
        var environment = new AgentPathEnvironment(new ProcessRunner(TimeProvider.System));

        await environment.RefreshAsync(CancellationToken.None);
        await Assert.That(Environment.GetEnvironmentVariable("PATH")).Contains(oldDirectory);

        await File.WriteAllTextAsync(rc, $"export PATH=\"{newDirectory}:$PATH\"\n");
        await environment.RefreshAsync(CancellationToken.None);
        await Assert.That(Environment.GetEnvironmentVariable("PATH")).Contains(newDirectory);
        await Assert.That(Environment.GetEnvironmentVariable("PATH")).DoesNotContain(oldDirectory);
    }

    [Test]
    public async Task Failed_or_empty_probes_preserve_the_last_usable_PATH() {
        using var path = EnvScope.Exclusive("PATH", "/usr/bin:/bin");
        var shell = Tmp.CreateExecutable("shell", "#!/bin/sh\nexit 1\n");
        using var shellEnv = EnvScope.Exclusive("SHELL", shell);
        var environment = new AgentPathEnvironment(new ProcessRunner(TimeProvider.System));

        await environment.RefreshAsync(CancellationToken.None);
        await Assert.That(Environment.GetEnvironmentVariable("PATH")).IsEqualTo("/usr/bin:/bin");

        await File.WriteAllTextAsync(shell, "#!/bin/sh\nprintf '<<KCAP-PATH>><<KCAP-PATH>>'\n");
        await environment.RefreshAsync(CancellationToken.None);
        await Assert.That(Environment.GetEnvironmentVariable("PATH")).IsEqualTo("/usr/bin:/bin");
    }
}
