namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>What ends a terminal session's recording in the daemon: its Claude process gone, told apart from a later
/// process given the same pid.</summary>
public class ProcessIncarnationTests {
    [Test]
    public async Task A_running_process_has_not_exited() {
        using var process = DummyProcess.StartSleep(30);

        var incarnation = ProcessIncarnation.Of(process.Pid);

        await Assert.That(incarnation).IsNotNull();
        await Assert.That(incarnation!.HasExited()).IsFalse();
    }

    [Test]
    public async Task A_process_that_ended_has_exited() {
        using var process = DummyProcess.StartSleep(30);
        var incarnation = ProcessIncarnation.Of(process.Pid)!;

        process.Kill();
        process.WaitForExit(TimeSpan.FromSeconds(10));

        await Assert.That(incarnation.HasExited()).IsTrue();
    }

    /// <summary>The pid is alive, but its start token is another process's: the one this names has gone.</summary>
    [Test]
    public async Task A_pid_given_to_another_process_has_exited() {
        using var original      = DummyProcess.StartSleep(30);
        var       originalToken = ProcessIncarnation.Of(original.Pid)!.StartToken;
        // Linux reads a start time in clock ticks, so a process started within the same tick carries the same token.
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        using var reused = DummyProcess.StartSleep(30);
        await Assert.That(ProcessIncarnation.Of(reused.Pid)!.StartToken).IsNotEqualTo(originalToken);

        var incarnation = new ProcessIncarnation(reused.Pid, originalToken);

        await Assert.That(incarnation.HasExited()).IsTrue();
    }

    /// <summary>A hook that resolved no agent falls back to these, and following init would never end a source.</summary>
    [Test]
    public async Task There_is_none_for_init_or_no_pid() {
        await Assert.That(ProcessIncarnation.Of(1)).IsNull();
        await Assert.That(ProcessIncarnation.Of(0)).IsNull();
    }
}
