using System.Collections.Concurrent;
using System.Diagnostics;
using Capacitor.Cli.Core.Harness.Headless;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Headless;

/// <summary>The child is a POSIX script that records its pid, so each kill can be proved by the process's identity
/// leaving the table rather than by a directory that POSIX lets go while its process lives.</summary>
public class HeadlessProcessTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string PidFile => Path.Combine(Tmp.Path, "child.pid");

    ProcessStartInfo Script(string body) =>
        new(Tmp.CreateExecutable($"bin-{Guid.NewGuid():N}/child", $"#!/bin/sh\necho $$ > '{PidFile}'\n{body}\n"));

    static Task<HeadlessProcessExit?> Run(ProcessStartInfo psi, string? stdin = null, Action<string>? onStdout = null,
        Func<string, bool>? onStderr = null, TimeSpan? timeout = null, CancellationToken ct = default) =>
        HeadlessProcess.RunAsync(psi, stdin, onStdout ?? (_ => { }), onStderr, timeout ?? TimeSpan.FromSeconds(20),
            TimeProvider.System, _ => { }, ct);

    async Task<(int Pid, string Identity)> ChildAsync() {
        var start = Environment.TickCount64;

        while (Environment.TickCount64 - start < 10_000) {
            if (File.Exists(PidFile) && int.TryParse((await File.ReadAllTextAsync(PidFile)).Trim(), out var pid))
                return (pid, PidIdentity.Capture(pid));

            await Task.Delay(20);
        }

        throw new TimeoutException($"the child never wrote {PidFile}");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Stdout_lines_stdin_and_the_exit_code_come_through() {
        var lines = new ConcurrentQueue<string>();

        var exit = await Run(Script("cat\necho done >&2\nexit 3"), "one\ntwö\n", lines.Enqueue);

        await Assert.That(exit).IsEqualTo(new HeadlessProcessExit(3, false, false, "done"));
        await Assert.That(lines).IsEquivalentTo(["one", "twö"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Without_input_stdin_is_closed_at_once() {
        var exit = await Run(Script("cat > /dev/null\nexit 0"), timeout: TimeSpan.FromSeconds(5));

        await Assert.That(exit!.ExitCode).IsEqualTo(0);
    }

    /// <summary>The input is larger than a pipe buffer, so the write fails once the child has gone.</summary>
    [Test, ExcludeOn(OS.Windows)]
    public async Task A_child_that_exits_without_reading_its_input_still_reports_how_it_ended() {
        var exit = await Run(Script("echo 'No models available' >&2\nexit 1"), new string('x', 1 << 20));

        await Assert.That(exit).IsEqualTo(new HeadlessProcessExit(1, false, false, "No models available"));
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_stderr_line_can_kill_the_child_early() {
        var go = Path.Combine(Tmp.Path, "go");
        var run = Run(Script($"while [ ! -f '{go}' ]; do sleep 0.05; done\necho 'stream error: usage limit' >&2\nexec sleep 30"),
            onStderr: l => l.Contains("usage limit"));
        var (pid, identity) = await ChildAsync();
        var started = Environment.TickCount64;
        await File.WriteAllTextAsync(go, "");
        var exit = await run;

        await Assert.That(exit).IsEqualTo(new HeadlessProcessExit(null, false, true, "stream error: usage limit"));
        await Assert.That(Environment.TickCount64 - started).IsLessThan(10_000);
        await Assert.That(PidIdentity.IsGone(pid, identity)).IsTrue();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task The_deadline_kills_the_child_before_returning() {
        var run  = Run(Script("exec sleep 30"), timeout: TimeSpan.FromSeconds(1));
        var (pid, identity) = await ChildAsync();
        var exit = await run;

        await Assert.That(exit!.TimedOut).IsTrue();
        await Assert.That(exit.ExitCode).IsNull();
        await Assert.That(PidIdentity.IsGone(pid, identity)).IsTrue();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Cancellation_kills_the_child_before_propagating() {
        using var cts = new CancellationTokenSource();
        var run = Run(Script("exec sleep 30"), ct: cts.Token);
        var (pid, identity) = await ChildAsync();

        await cts.CancelAsync();

        await Assert.That(async () => await run).Throws<OperationCanceledException>();
        await Assert.That(PidIdentity.IsGone(pid, identity)).IsTrue();
    }

    [Test]
    public async Task A_child_that_cannot_start_is_null() =>
        await Assert.That(await Run(new ProcessStartInfo(Path.Combine(Tmp.Path, "missing")))).IsNull();

    [Test]
    public async Task ForLog_folds_lines_and_drops_control_characters() =>
        await Assert.That(HeadlessProcess.ForLog("a\r\nb\u0007c")).IsEqualTo("a\\n\\nbc");
}
