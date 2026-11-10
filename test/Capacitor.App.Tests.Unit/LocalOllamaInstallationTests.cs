using Capacitor.App.Services.LocalSetup;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit;

public class LocalOllamaInstallationTests {
    [Test]
    public async Task A_registered_bundle_outside_Applications_counts_without_asking_a_shell() {
        using var tmp = new TempDir();
        var bundle = tmp.CreateDir("My applications/Ollama.app");
        var probe = new FakeLoginShellProbe { TerminalPathBehavior = _ => throw new InvalidOperationException("No shell needed") };
        var discovery = new LocalOllamaInstallation(probe, () => Directory.Exists(bundle),
            _ => throw new InvalidOperationException("No PATH needed"), () => false, TimeProvider.System);

        await Assert.That(await discovery.CheckAsync(CancellationToken.None)).IsTrue();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_cli_on_the_login_shell_PATH_is_found_without_running_ollama() {
        using var tmp = new TempDir();
        tmp.CreateExecutable("ollama", "#!/bin/sh\nexit 99\n");
        var runner = new ShellReply(tmp.Path);
        var probe = new LoginShellProbe(runner, key => key == "SHELL" ? "/bin/zsh" : null);
        var discovery = new LocalOllamaInstallation(probe, () => false,
            path => BinaryProbe.Searching(path).Finds("ollama"), () => false, TimeProvider.System);

        await Assert.That(await discovery.CheckAsync(CancellationToken.None)).IsTrue();
        await Assert.That(runner.Calls).Count().IsEqualTo(1);
        await Assert.That(runner.Calls[0].File).IsEqualTo("/bin/zsh");
        await Assert.That(runner.Calls[0].Args[0]).IsEqualTo("-lic");
        await Assert.That(runner.Calls[0].Options.Timeout).IsNotNull();
    }

    [Test]
    public async Task An_unavailable_platform_lookup_still_checks_the_shell_PATH() {
        var probe = new FakeLoginShellProbe { TerminalPathBehavior = _ => Task.FromResult<string?>("/custom/bin") };
        var discovery = new LocalOllamaInstallation(probe, () => throw new DllNotFoundException(),
            path => path == "/custom/bin", () => false, TimeProvider.System);

        await Assert.That(await discovery.CheckAsync(CancellationToken.None)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_failed_shell_lookup_falls_back_to_the_fixed_locations(bool installed) {
        var probe = new FakeLoginShellProbe { TerminalPathBehavior = _ => throw new IOException("Shell unavailable") };
        var discovery = new LocalOllamaInstallation(probe, () => false, _ => false, () => installed, TimeProvider.System);

        await Assert.That(await discovery.CheckAsync(CancellationToken.None)).IsEqualTo(installed);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_slow_shell_uses_the_fixed_paths_and_cancels_its_wait_before_the_late_answer(bool installed) {
        var time = new FakeTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var path = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookupToken = CancellationToken.None;
        var probe = new FakeLoginShellProbe { TerminalPathBehavior = ct => {
            lookupToken = ct;
            started.SetResult();
            return path.Task;
        } };
        var discovery = new LocalOllamaInstallation(probe, () => false, _ => true, () => installed, time);
        var check = discovery.CheckAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(LocalOllamaInstallation.LookupTimeout);

        await Assert.That(await check.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(installed);
        await Assert.That(lookupToken.IsCancellationRequested).IsTrue();
        path.SetResult("/late/bin");
    }

    [Test]
    public async Task A_blocking_platform_lookup_does_not_block_the_caller_or_its_timeout() {
        var time = new FakeTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var discovery = new LocalOllamaInstallation(null, () => {
            started.SetResult();
            release.Wait(TimeSpan.FromSeconds(10));
            finished.SetResult();
            return true;
        }, _ => false, () => true, time);
        var check = discovery.CheckAsync(CancellationToken.None);
        try {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(check.IsCompleted).IsFalse();
            time.Advance(LocalOllamaInstallation.LookupTimeout);
            await Assert.That(await check.WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
        } finally {
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task Cancellation_does_not_turn_into_a_fixed_path_answer() {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new FakeLoginShellProbe { TerminalPathBehavior = async ct => {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        } };
        var fallbackCalls = 0;
        var discovery = new LocalOllamaInstallation(probe, () => false, _ => false,
            () => { fallbackCalls++; return true; }, TimeProvider.System);
        var check = discovery.CheckAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => check);
        await Assert.That(fallbackCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Windows_can_check_its_process_PATH_without_a_Unix_login_shell() {
        var discovery = new LocalOllamaInstallation(null, () => false, path => path is null, () => false, TimeProvider.System);
        await Assert.That(await discovery.CheckAsync(CancellationToken.None)).IsTrue();
    }

    sealed class ShellReply(string path) : IProcessRunner {
        public List<(string File, string[] Args, RunOptions Options)> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Calls.Add((fileName, args, options));
            return Task.FromResult(new ProcessResult(0, $"<<KCAP-PATH>>{path}<<KCAP-PATH>>", "", false));
        }
        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) => throw new NotSupportedException();
    }
}
