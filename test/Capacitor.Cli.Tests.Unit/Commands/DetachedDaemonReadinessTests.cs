using System.Diagnostics;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A detached start counts a daemon as started only once its own pid answers on the control
/// socket; a process that is merely alive when the bound runs out is reported as unconfirmed.</summary>
[ParallelLimiter<SubprocessLimit>]
public class DetachedDaemonReadinessTests {
    static readonly HelloProbeResult Unreachable = new(false, null, null, null);

    static Process Spawn(string file, params string[] args) {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);

        return Process.Start(psi)!;
    }

    static void Reap(Process p) {
        try { p.Kill(); } catch (InvalidOperationException) { }

        p.WaitForExit();
        p.Dispose();
    }

    [Test]
    public async Task A_live_process_whose_socket_never_answers_is_not_yet_serving() {
        if (OperatingSystem.IsWindows()) return;
        var daemon = Spawn("sleep", "30");
        try {
            var readiness = await DetachedDaemonReadiness.AwaitAsync(
                daemon, _ => Task.FromResult(Unreachable), TimeProvider.System, TimeSpan.FromMilliseconds(400));

            await Assert.That(readiness).IsEqualTo(DaemonReadiness.NotYetServing);
            await Assert.That(daemon.HasExited).IsFalse();
        } finally {
            Reap(daemon);
        }
    }

    [Test]
    public async Task Its_own_pid_answering_is_serving_without_waiting_out_the_bound() {
        if (OperatingSystem.IsWindows()) return;
        var daemon = Spawn("sleep", "30");
        try {
            var probes = 0;
            Task<HelloProbeResult> Probe(TimeSpan _) =>
                Task.FromResult(++probes < 3 ? Unreachable : new HelloProbeResult(true, 1, "v", "n") { Reachable = true, Pid = daemon.Id });

            var clock     = Stopwatch.StartNew();
            var readiness = await DetachedDaemonReadiness.AwaitAsync(daemon, Probe, TimeProvider.System, TimeSpan.FromSeconds(20));

            await Assert.That(readiness).IsEqualTo(DaemonReadiness.Serving);
            await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
        } finally {
            Reap(daemon);
        }
    }

    /// <summary>Another daemon holding the name answers on the same socket; that is not this start.</summary>
    [Test]
    public async Task Another_pid_answering_is_not_serving() {
        if (OperatingSystem.IsWindows()) return;
        var daemon = Spawn("sleep", "30");
        try {
            var foreign = new HelloProbeResult(true, 1, "v", "n") { Reachable = true, Pid = daemon.Id + 1 };

            var readiness = await DetachedDaemonReadiness.AwaitAsync(
                daemon, _ => Task.FromResult(foreign), TimeProvider.System, TimeSpan.FromMilliseconds(400));

            await Assert.That(readiness).IsEqualTo(DaemonReadiness.NotYetServing);
        } finally {
            Reap(daemon);
        }
    }

    [Test]
    public async Task An_exit_before_serving_is_exited() {
        if (OperatingSystem.IsWindows()) return;
        var daemon = Spawn("sh", "-c", "exit 2");
        try {
            var readiness = await DetachedDaemonReadiness.AwaitAsync(
                daemon, _ => Task.FromResult(Unreachable), TimeProvider.System, TimeSpan.FromSeconds(20));

            await Assert.That(readiness).IsEqualTo(DaemonReadiness.Exited);
            await Assert.That(daemon.ExitCode).IsEqualTo(2);
        } finally {
            Reap(daemon);
        }
    }

    [Test]
    public async Task An_unconfirmed_start_says_so_and_a_serving_one_does_not() {
        await Assert.That(DaemonCommands.DescribeDetachedStart("laptop", 42, DaemonReadiness.Serving))
            .IsEqualTo("Daemon 'laptop' started (PID 42)");
        await Assert.That(DaemonCommands.DescribeDetachedStart("laptop", 42, DaemonReadiness.NotYetServing))
            .Contains("not yet serving");
    }
}
