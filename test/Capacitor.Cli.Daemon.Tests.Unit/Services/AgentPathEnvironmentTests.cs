using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

[NotInParallel, RunOn(OS.MacOs | OS.Linux)]
public class AgentPathEnvironmentTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class ObservedRunner : IProcessRunner {
        readonly ProcessRunner _runner = new(TimeProvider.System);
        public Task<ProcessResult>? Completion { get; private set; }

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) =>
            Completion = _runner.RunAsync(fileName, args, options, ct);

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
                Action<StreamedLine> onLine, CancellationToken ct) => throw new NotSupportedException();
    }

    sealed class PendingRunner : IProcessRunner {
        public TaskCompletionSource<ProcessResult> Result { get; } = new();
        public int Calls { get; private set; }

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Calls++;
            return Result.Task;
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
                Action<StreamedLine> onLine, CancellationToken ct) => throw new NotSupportedException();
    }

    [Test]
    public async Task Repeated_refreshes_skip_an_unfinished_probe_and_resume_after_it_drains() {
        using var path = EnvScope.Exclusive("PATH", "/usr/bin:/bin");
        var runner = new PendingRunner();
        var time = new FakeTimeProvider();
        var environment = new AgentPathEnvironment(runner, time);

        try {
            for (var i = 0; i < 3; i++) {
                var refresh = environment.RefreshAsync(CancellationToken.None);
                time.Advance(TimeSpan.FromSeconds(12));
                await refresh.WaitAsync(TimeSpan.FromSeconds(3));
            }
            await Assert.That(runner.Calls).IsEqualTo(1);
            await Assert.That(Environment.GetEnvironmentVariable("PATH")).IsEqualTo("/usr/bin:/bin");
        } finally {
            runner.Result.SetResult(new ProcessResult(0, "<<KCAP-PATH>>/fresh-path<<KCAP-PATH>>", "", false));
        }

        await Assert.That(Environment.GetEnvironmentVariable("PATH")).IsEqualTo("/usr/bin:/bin");
        await environment.RefreshAsync(CancellationToken.None);
        await Assert.That(runner.Calls).IsEqualTo(2);
        await Assert.That(Environment.GetEnvironmentVariable("PATH")).IsEqualTo("/fresh-path");
    }

    [Test]
    public async Task An_exited_shell_with_a_child_holding_output_cannot_block_refresh_or_apply_a_late_PATH() {
        using var path = EnvScope.Exclusive("PATH", "/usr/bin:/bin");
        var pidFile = Tmp.PathTo("child.pid");
        var shell = Tmp.CreateExecutable("shell", $"#!/bin/sh\n/bin/sleep 30 &\nprintf '%s' \"$!\" > '{pidFile}'\nprintf '<<KCAP-PATH>>/late-path<<KCAP-PATH>>'\n");
        using var shellEnv = EnvScope.Exclusive("SHELL", shell);
        var runner = new ObservedRunner();
        var environment = new AgentPathEnvironment(runner);
        var refresh = environment.RefreshAsync(CancellationToken.None);

        int pid = 0;
        string? identity = null;
        try {
            using var captureDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(pidFile) || !int.TryParse(await File.ReadAllTextAsync(pidFile), out pid))
                await Task.Delay(20, captureDeadline.Token);
            identity = PidIdentity.Capture(pid);

            await refresh.WaitAsync(TimeSpan.FromSeconds(15));
            await Assert.That(Environment.GetEnvironmentVariable("PATH")).IsEqualTo("/usr/bin:/bin");
        } finally {
            if (identity is not null) ProcessTree.Kill(pid, identity);
            await runner.Completion!.WaitAsync(TimeSpan.FromSeconds(3));
            await refresh.WaitAsync(TimeSpan.FromSeconds(3));
        }

        await Assert.That(Environment.GetEnvironmentVariable("PATH")).IsEqualTo("/usr/bin:/bin");
    }

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
