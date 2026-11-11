using System.Globalization;
using Capacitor.Cli.Daemon.Pty.Unix;
using Capacitor.Cli.Daemon.Tests.Unit.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Unix;

/// <summary>L1-managed(b): the REAL production entry point (UnixPtyProcessFactory.Spawn) end
/// to end — resolves PATH in the parent, builds a plan via pty_preflight, spawns via the
/// dedicated spawner thread, and surfaces the natively-captured StartIdentity.</summary>
public class UnixPtyProcessSpawnTests {
    [Test]
    public async Task Spawn_produces_a_running_process_with_a_captured_identity() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        // The spawner thread is a non-background (IsBackground = false) OS thread — the test
        // must own it and Dispose() it itself, or the thread parks forever on the queue and the
        // whole test-host process never exits (confirmed empirically: an earlier version of this
        // test that let UnixPtyProcessFactory own an undisposed static singleton hung indefinitely).
        using var spawner = new UnixSpawnerThread();
        var       factory = new UnixPtyProcessFactory(spawner, TimeProvider.System);
        var       proc    = factory.Spawn("sleep", ["5"], AppContext.BaseDirectory);
        try {
            await Assert.That(proc.Pid).IsGreaterThan(0);
            await Assert.That(proc.StartIdentity).IsNotNull();
            await Assert.That(proc.StartIdentity).IsNotEmpty();
            await Assert.That(proc.StartIdentity).StartsWith(OperatingSystem.IsLinux() ? "lx:" : "mac:");

            // Cross-check against the independent, existing ProcessStartToken machinery: on
            // Linux both the shim's capture_lx_identity (pty_shim.c) and ProcessStartToken.ForPid
            // read the SAME two kernel facts (starttime field 22 of /proc/{pid}/stat, and
            // /proc/sys/kernel/random/boot_id) in the SAME "lx:{boot}:{starttime}" format via two
            // independently-implemented code paths, so they must produce byte-identical tokens
            // for a healthy spawn.
            //
            // On macOS, as of M1-A(a) [Task 7], ProcessStartToken.ForPid ALSO vendors the SAME
            // proc_pidinfo(PROC_PIDUNIQIDENTIFIERINFO=17) + kern.bootsessionuuid capture pty_shim.c's
            // pty_capture_mac_identity uses natively (same fixed-offset prefix read, same 256-byte
            // buffer size) — so the two independently-implemented code paths must ALSO produce
            // byte-identical "mac:{bootsessionuuid}:{p_uniqueid}" tokens for a healthy spawn. This
            // is the regression guard against the two vendored copies of the private struct
            // drifting apart (before Task 7 landed, Core's non-Linux branch returned the OLDER
            // "tk:{Process.StartTime.Ticks}" scheme instead, so this cross-check did not apply on
            // macOS and was gated out below).
            var liveToken = Capacitor.Cli.Core.ProcessStartToken.ForPid(proc.Pid);
            await Assert.That(liveToken).IsEqualTo(proc.StartIdentity);
        } finally {
            await proc.DisposeAsync();
        }
    }

    /// <summary>Terminate must take down the leader's whole process group, not just the leader —
    /// the shape a codex reviewer takes with its code-mode-host helper (kcap-cli#469). The shell
    /// leader backgrounds a long sleep (same group, since the leader is a forkpty session leader),
    /// reports its pid over the PTY, and waits. A leader-only kill leaves the sleep orphaned and
    /// running, which is exactly the mutation this test exists to fail on.</summary>
    [Test]
    public async Task Terminate_kills_the_leaders_whole_process_group() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var spawner = new UnixSpawnerThread();
        var       factory = new UnixPtyProcessFactory(spawner, TimeProvider.System);
        // The helper IGNORES SIGHUP (trap '' HUP survives the exec): a plain background sleep dies
        // to the controlling terminal's leader-exit SIGHUP even under a leader-only kill, which let
        // exactly that mutation pass this test — a helper that shrugs off HUP is also the shape
        // that leaks in production. Only a signal to the GROUP reaches it.
        var proc = factory.Spawn(
            "/bin/sh", ["-c", "(trap '' HUP; exec sleep 300) & echo \"CHILD:$!:DONE\"; wait"],
            AppContext.BaseDirectory);
        try {
            var childPid = await ReadReportedChildPidAsync(proc);
            await Assert.That(childPid).IsGreaterThan(0);
            // Capture while it is provably alive: this is init's child once the leader dies, not
            // ours, so the pid alone would read as live again the moment the OS reassigns it.
            var childIdentity = PidIdentity.Capture(childPid);

            await proc.TerminateAsync(TimeSpan.FromSeconds(5));
            await Assert.That(proc.HasExited).IsTrue();

            // Reparented to init, which reaps it once it dies — poll for the identity leaving the
            // process table rather than racing the reap.
            await PidIdentity.WaitUntilGoneAsync(childPid, childIdentity, TimeSpan.FromSeconds(5));
        } finally {
            await proc.DisposeAsync();
        }
    }

    /// <summary>A leader that ignores SIGTERM is only stopped by the SIGKILL fallback, and Terminate
    /// must return with it reaped: a disposed runtime never checks again, so a leader still unreaped
    /// at that point stays a zombie for the daemon's lifetime.</summary>
    [Test]
    public async Task Terminate_reaps_a_leader_that_only_the_kill_fallback_stops() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var spawner = new UnixSpawnerThread();
        var       factory = new UnixPtyProcessFactory(spawner, TimeProvider.System);
        var proc = factory.Spawn(
            "/bin/sh", ["-c", "trap '' TERM; echo \"CHILD:$$:DONE\"; while :; do sleep 1; done"],
            AppContext.BaseDirectory);
        try {
            await Assert.That(await ReadReportedChildPidAsync(proc)).IsEqualTo(proc.Pid);

            await proc.TerminateAsync(TimeSpan.FromMilliseconds(300));

            // Waiting on the pid here could steal a recycled pid from another test's child.
            await Assert.That(proc.HasExited).IsTrue();
        } finally {
            await proc.DisposeAsync();
        }
    }

    /// <summary>A helper that inherited the terminal and ignores SIGHUP keeps it open after the leader
    /// exits, so the terminal never hangs up: the output must still end once the leader has gone, with
    /// the leader's own exit code, and the helper goes down with the group when the process is disposed.</summary>
    [Test]
    public async Task Output_ends_when_the_leader_exits_though_a_helper_holds_the_terminal() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var spawner = new UnixSpawnerThread();
        var       factory = new UnixPtyProcessFactory(spawner, TimeProvider.System);
        var proc = factory.Spawn(
            "/bin/sh", ["-c", "(trap '' HUP; exec sleep 300) & echo \"CHILD:$!:DONE\"; sleep 1; exit 3"],
            AppContext.BaseDirectory);
        var helperPid      = -1;
        var helperIdentity = "";
        try {
            var (output, ended) = await ReadToEndAsync(proc, TimeSpan.FromSeconds(10));
            var match = System.Text.RegularExpressions.Regex.Match(output, @"CHILD:(\d+):DONE");
            if (match.Success) {
                helperPid      = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                helperIdentity = PidIdentity.Capture(helperPid);
            }

            await Assert.That(ended).IsTrue();
            await Assert.That(proc.HasExited).IsTrue();
            await Assert.That(proc.ExitCode).IsEqualTo(3);
            await Assert.That(helperPid).IsGreaterThan(0);

            await proc.DisposeAsync();
            await WaitHarness.SpinUntilAsync(() => PidIdentity.IsGone(helperPid, helperIdentity), TimeSpan.FromSeconds(5));
        } finally {
            await proc.DisposeAsync();
            if (helperPid > 0 && !PidIdentity.IsGone(helperPid, helperIdentity))
                UnixPtyInterop.kill(helperPid, UnixPtyInterop.SIGKILL);
        }
    }

    /// <summary>A leader a signal killed reports 128 plus the signal, so a SIGKILL reads as a failure
    /// rather than a clean exit.</summary>
    [Test]
    public async Task A_leader_killed_by_a_signal_exits_with_128_plus_the_signal() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var spawner = new UnixSpawnerThread();
        var       factory = new UnixPtyProcessFactory(spawner, TimeProvider.System);
        var       proc    = factory.Spawn("/bin/sh", ["-c", "kill -9 $$"], AppContext.BaseDirectory);
        try {
            await ReadToEndAsync(proc, TimeSpan.FromSeconds(10));
            await proc.WaitForExitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(proc.HasExited).IsTrue();
            await Assert.That(proc.ExitCode).IsEqualTo(137);
        } finally {
            await proc.DisposeAsync();
        }
    }

    /// <summary>A leader something else reaped after the runtime saw it exit has its group left alone at
    /// disposal: once reaped, the group id no longer proves the group is the agent's. The helper surviving
    /// the disposal is what shows no signal went out. The runtime never reaps before disposal, so the
    /// test's reap always wins.</summary>
    [Test]
    public async Task A_leader_reaped_elsewhere_after_its_exit_was_seen_has_its_group_left_alone() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var spawner = new UnixSpawnerThread();
        var       factory = new UnixPtyProcessFactory(spawner, TimeProvider.System);
        var proc = factory.Spawn(
            "/bin/sh", ["-c", "(trap '' HUP; exec sleep 300) & echo \"CHILD:$!:DONE\"; exec sleep 300"],
            AppContext.BaseDirectory);
        var helperPid      = -1;
        var helperIdentity = "";
        try {
            helperPid      = await ReadReportedChildPidAsync(proc);
            helperIdentity = PidIdentity.Capture(helperPid);
            await Assert.That(helperPid).IsGreaterThan(0);

            UnixPtyInterop.kill(proc.Pid, UnixPtyInterop.SIGKILL);
            await proc.WaitForExitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(proc.HasExited).IsTrue();
            await Assert.That(proc.ExitCode).IsEqualTo(137);

            await Assert.That(UnixPtyInterop.waitpid(proc.Pid, out _, 0)).IsEqualTo(proc.Pid);

            await proc.DisposeAsync();
            await Task.Delay(300);

            await Assert.That(PidIdentity.IsGone(helperPid, helperIdentity)).IsFalse();
        } finally {
            await proc.DisposeAsync();
            if (helperPid > 0 && !PidIdentity.IsGone(helperPid, helperIdentity))
                UnixPtyInterop.kill(helperPid, UnixPtyInterop.SIGKILL);
        }
    }

    /// <summary>Output still buffered when the leader is reaped is all read, though the reap came first: the
    /// writes are spaced so they arrive as separate reads, and reading starts only once the exit is observed.
    /// Linux only: macOS holds the leader's exit until its terminal output has been read.</summary>
    [Test]
    public async Task Output_buffered_when_the_leader_is_reaped_is_all_read() {
        if (!OperatingSystem.IsLinux()) return;

        using var spawner = new UnixSpawnerThread();
        var       factory = new UnixPtyProcessFactory(spawner, TimeProvider.System);
        var proc = factory.Spawn(
            "/bin/sh", ["-c", "printf one-; sleep 0.3; printf two-; sleep 0.3; printf three; exit 0"], AppContext.BaseDirectory);
        try {
            await proc.WaitForExitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(proc.HasExited).IsTrue();

            var (output, ended) = await ReadToEndAsync(proc, TimeSpan.FromSeconds(10));

            await Assert.That(ended).IsTrue();
            await Assert.That(output).IsEqualTo("one-two-three");
        } finally {
            await proc.DisposeAsync();
        }
    }

    /// <summary>Everything the process wrote, and whether its output ended before <paramref name="limit"/>.</summary>
    static async Task<(string Output, bool Ended)> ReadToEndAsync(Capacitor.Cli.Daemon.Pty.IPtyProcess proc, TimeSpan limit) {
        using var cts    = new CancellationTokenSource(limit);
        var       buffer = new System.Text.StringBuilder();
        try {
            await foreach (var chunk in proc.ReadOutputAsync(cts.Token)) buffer.Append(System.Text.Encoding.UTF8.GetString(chunk));
        } catch (OperationCanceledException) {
            // The limit passed with the output still open, which Ended reports.
        }

        return (buffer.ToString(), !cts.IsCancellationRequested);
    }

    /// <summary>A spawned child that nothing came to own has to go down with its whole group. The
    /// helper ignores SIGHUP, so closing the master alone leaves it running — only the group kill
    /// reaches it.</summary>
    [Test]
    public async Task Abandoning_a_spawned_child_kills_its_whole_group_and_reaps_the_leader() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var spawner = new UnixSpawnerThread();

        var rc = UnixPtyInterop.pty_preflight(
            "/bin/sh", ["sh", "-c", "(trap '' HUP; exec /bin/sleep 300) & echo \"CHILD:$!:DONE\"; wait", null],
            [null], UnixPtyInterop.pty_probe_execveat(), out var plan);
        await Assert.That(rc).IsEqualTo(0);

        var result = spawner.SpawnOn(plan, [null], AppContext.BaseDirectory, 40, 120, Environment.ProcessId, -1);
        UnixPtyInterop.pty_plan_free(ref plan);
        await Assert.That(result.FailedStep).IsEqualTo(0);

        var helperPid      = -1;
        var helperIdentity = "";

        try {
            helperPid = ReadReportedChildPid(result.MasterFd);
            await Assert.That(helperPid).IsGreaterThan(0);
            helperIdentity = PidIdentity.Capture(helperPid);

            UnixPtyProcess.Abandon(result.MasterFd, result.Pid);

            // Reaped means no longer this process's child, which is what ECHILD says.
            await Assert.That(UnixPtyInterop.waitpid(result.Pid, out _, UnixPtyInterop.WNOHANG)).IsEqualTo(-1);
            await PidIdentity.WaitUntilGoneAsync(helperPid, helperIdentity, TimeSpan.FromSeconds(5));
        } finally {
            // Only while the leader is still this process's unreaped child is its pid — and so its
            // group id — provably its own.
            if (UnixPtyInterop.waitpid(result.Pid, out _, UnixPtyInterop.WNOHANG) == 0) {
                UnixPtyInterop.kill(-result.Pid, UnixPtyInterop.SIGKILL);
                UnixPtyInterop.waitpid(result.Pid, out _, 0);
            }

            if (helperPid > 0 && !PidIdentity.IsGone(helperPid, helperIdentity))
                UnixPtyInterop.kill(helperPid, UnixPtyInterop.SIGKILL);
        }
    }

    /// <summary>The raw-fd twin of <see cref="ReadReportedChildPidAsync"/>, for a child no
    /// <see cref="UnixPtyProcess"/> owns.</summary>
    static int ReadReportedChildPid(int masterFd) {
        var buffer  = new System.Text.StringBuilder();
        var chunk   = new byte[256];
        var pfd     = new UnixPtyInterop.PollFd { fd = masterFd, events = UnixPtyInterop.POLLIN };
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        while (System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10)) {
            if (UnixPtyInterop.poll(ref pfd, 1, 200) <= 0 || (pfd.revents & UnixPtyInterop.POLLIN) == 0) continue;

            var read = (int)UnixPtyInterop.read(masterFd, chunk, chunk.Length);
            if (read <= 0) break;

            buffer.Append(System.Text.Encoding.UTF8.GetString(chunk, 0, read));
            var match = System.Text.RegularExpressions.Regex.Match(buffer.ToString(), @"CHILD:(\d+):DONE");
            if (match.Success) return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        return -1;
    }

    /// <summary>Reads PTY output until the leader reports its backgrounded child as
    /// <c>CHILD:{pid}:DONE</c>. The trailing marker matters: without it a chunk boundary could
    /// split the digits and a prefix of the pid would parse as a (wrong, possibly live) pid.</summary>
    static async Task<int> ReadReportedChildPidAsync(Capacitor.Cli.Daemon.Pty.IPtyProcess proc) {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new System.Text.StringBuilder();

        await foreach (var chunk in proc.ReadOutputAsync(cts.Token)) {
            buffer.Append(System.Text.Encoding.UTF8.GetString(chunk));
            var match = System.Text.RegularExpressions.Regex.Match(buffer.ToString(), @"CHILD:(\d+):DONE");
            if (match.Success) return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        return -1;
    }
}
