using System.Diagnostics;
using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary><see cref="ManagedChildProcess"/> against real short-lived children; only a real process
/// table can show whether a kill landed.</summary>
[ParallelLimiter<SubprocessLimit>]
public class ManagedChildProcessTests {
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [TempDir] public required TempDir Tmp { get; init; }

    static Process Start(string fileName, params string[] args) {
        var psi = new ProcessStartInfo(fileName) {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        foreach (var arg in args) psi.ArgumentList.Add(arg);

        return Process.Start(psi)!;
    }

    static ManagedChildProcess Manage(Process process, Action<string>? onStderrLine = null) =>
        new(process, TimeProvider.System, onStderrLine ?? (_ => { }));

    static async Task<string> FirstStdoutLine(ManagedChildProcess child) {
        using var cts = new CancellationTokenSource(Deadline);

        await foreach (var line in child.ReadStdoutLinesAsync(cts.Token)) return line;

        throw new InvalidOperationException("stdout closed before the first line");
    }

    /// <summary>The kill reaches every descendant, not just the direct child; a survivor would leak a
    /// vendor's MCP children for the life of the daemon.</summary>
    [Test]
    public async Task Terminate_kills_the_whole_tree() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Spawns the tree through /bin/sh.");

        var pidFile = Tmp.PathTo("pids");
        var script = Tmp.CreateExecutable("tree.sh", """
            #!/bin/sh
            sleep 300 &
            echo $! > "$1"
            sh -c 'sleep 300 & echo $! >> "$1"; wait' inner "$1" &
            echo $! >> "$1"
            while [ "$(wc -l < "$1")" -lt 3 ]; do sleep 0.05; done
            echo ready
            wait
            """);

        await using var child = Manage(Start(script, pidFile));

        await Assert.That(await FirstStdoutLine(child)).IsEqualTo("ready");

        var descendants = (await File.ReadAllLinesAsync(pidFile))
            .Select(int.Parse)
            .Select(pid => (Pid: pid, Identity: PidIdentity.Capture(pid)))
            .ToList();

        await Assert.That(descendants.Count).IsEqualTo(3);

        try {
            await child.TerminateAsync(Deadline);

            await Assert.That(child.HasExited).IsTrue();

            foreach (var (pid, identity) in descendants)
                await PidIdentity.WaitUntilGoneAsync(pid, identity, Deadline);
        } finally {
            foreach (var (pid, identity) in descendants) ProcessTree.Kill(pid, identity);
        }
    }

    [Test]
    public async Task Dispose_kills_a_running_child_and_is_idempotent() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Uses /bin/sleep as the child.");

        var process = Start("/bin/sleep", "300");
        using var observer = Process.GetProcessById(process.Id);
        var child = Manage(process);

        await Assert.That(child.HasExited).IsFalse();

        await child.DisposeAsync();
        await child.DisposeAsync();

        observer.WaitForExit(milliseconds: (int)Deadline.TotalMilliseconds);

        await Assert.That(observer.HasExited).IsTrue();
        await Assert.That(child.IsDisposed).IsTrue();
    }

    [Test]
    public async Task Terminate_after_dispose_does_not_throw() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Uses /bin/sleep as the child.");

        var child = Manage(Start("/bin/sleep", "300"));
        var pid   = child.Pid;

        await child.DisposeAsync();
        await child.TerminateAsync(TimeSpan.FromSeconds(1));
        await child.WaitForExitAsync(TimeSpan.FromSeconds(1));

        await Assert.That(pid).IsGreaterThan(0);
        await Assert.That(child.Pid).IsEqualTo(pid);
        await Assert.That(child.HasExited).IsTrue();
        await Assert.That(child.ExitCode).IsNull();
    }

    [Test]
    public async Task WaitForExit_returns_silently_on_timeout() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Uses /bin/sleep as the child.");

        await using var child = Manage(Start("/bin/sleep", "300"));

        await child.WaitForExitAsync(TimeSpan.FromMilliseconds(100));

        await Assert.That(child.HasExited).IsFalse();
        await Assert.That(child.ExitCode).IsNull();
    }

    [Test]
    public async Task Exit_code_is_reported_once_the_child_exits() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Uses /bin/sh as the child.");

        await using var child = Manage(Start("/bin/sh", "-c", "exit 7"));

        await child.WaitForExitAsync(Deadline);

        await Assert.That(child.ExitCode).IsEqualTo(7);
    }

    /// <summary>A child writing far more stderr than a pipe buffer holds still runs to completion,
    /// and every non-empty line reaches the callback in order.</summary>
    [Test]
    public async Task Stderr_is_drained_and_empty_lines_are_skipped() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Uses /bin/sh as the child.");

        var script = Tmp.CreateExecutable("noisy.sh", """
            #!/bin/sh
            i=0
            while [ $i -lt 2000 ]; do
                printf 'line-%04d-%0100d\n\n' "$i" 0 >&2
                i=$((i + 1))
            done
            echo done
            """);

        var lines = new List<string>();
        await using var child = Manage(Start(script), line => { lock (lines) lines.Add(line); });

        await Assert.That(await FirstStdoutLine(child)).IsEqualTo("done");

        await child.WaitForExitAsync(Deadline);
        await Assert.That(child.ExitCode).IsEqualTo(0);

        var waited = Stopwatch.StartNew();
        List<string> seen;

        while (true) {
            lock (lines) seen = [.. lines];
            if (seen.Count >= 2000 || waited.Elapsed > Deadline) break;
            await Task.Delay(20);
        }

        await Assert.That(seen.Count).IsEqualTo(2000);

        await Assert.That(seen.All(l => l.Length > 0)).IsTrue();
        await Assert.That(seen[0]).StartsWith("line-0000-");
        await Assert.That(seen[^1]).StartsWith("line-1999-");
    }

    [Test]
    public async Task Written_lines_reach_stdin_and_come_back_on_stdout() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Uses /bin/cat as the child.");

        await using var child = Manage(Start("/bin/cat"));

        await child.WriteLineAsync("first", CancellationToken.None);
        await child.WriteLineAsync("second", CancellationToken.None);
        await child.CloseInputAsync(Deadline);

        using var cts = new CancellationTokenSource(Deadline);
        var lines = new List<string>();

        await foreach (var line in child.ReadStdoutLinesAsync(cts.Token)) lines.Add(line);

        await Assert.That(lines).IsEquivalentTo(["first", "second"]);

        await child.WaitForExitAsync(Deadline);
        await Assert.That(child.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task Writing_after_dispose_throws_and_closing_is_a_no_op() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Uses /bin/cat as the child.");

        var child = Manage(Start("/bin/cat"));

        await child.DisposeAsync();

        await Assert.That(() => child.WriteLineAsync("late", CancellationToken.None)).Throws<ObjectDisposedException>();
        await child.CloseInputAsync(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task Reading_stdout_after_dispose_ends_without_throwing() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Uses /bin/cat as the child.");

        var child = Manage(Start("/bin/cat"));

        await child.DisposeAsync();

        var read = new StringBuilder();
        await foreach (var line in child.ReadStdoutLinesAsync(CancellationToken.None)) read.Append(line);

        await Assert.That(read.Length).IsEqualTo(0);
    }
}
