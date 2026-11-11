using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Unix;

/// Whether the runtime can reap a terminal's leader, which decides whether an exited leader's group may be signalled.
public class ChildReapersTests {
    /// Only a default SIGCHLD disposition in a process other than pid 1 rules the runtime out: ignored makes it reap
    /// every child, and a handler installed before the read hides the original.
    [Test]
    [Arguments(42, 0, false)]
    [Arguments(42, 1, true)]
    [Arguments(42, 0x1234, true)]
    [Arguments(42, -1, true)]
    [Arguments(1, 0, true)]
    public async Task The_runtime_is_ruled_out_only_by_a_default_disposition_outside_pid_1(int pid, long handler, bool mayReap) =>
        await Assert.That(ChildReapers.Decide(pid, (nint)handler)).IsEqualTo(mayReap);

    /// The read happens as the daemon's code loads, ahead of the runtime's own SIGCHLD handler, so a host started
    /// with the default disposition reads as one only the daemon reaps in.
    [Test]
    public async Task The_disposition_is_read_before_the_runtime_replaces_it() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        await Assert.That(ChildReapers.RuntimeMayReap).IsEqualTo(Environment.ProcessId == 1);
    }
}
