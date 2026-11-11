using Capacitor.Cli.Harness.Copilot;

namespace Capacitor.Cli.Tests.Unit.Harness.Copilot;

public class CopilotStopRequestEndTests {
    static Task<string?> Decide(bool runtimeGone, bool endsOnShutdown) =>
        CopilotStopRequestEnd.DecideAsync(() => runtimeGone, () => endsOnShutdown, TimeProvider.System, TimeSpan.FromMilliseconds(300));

    [Test]
    [Arguments(true, false, "parent_exited")]
    [Arguments(false, true, "session_shutdown")]
    [Arguments(true, true, "parent_exited")]
    [Arguments(false, false, null)]
    public async Task A_stopped_watcher_ends_the_session_only_when_it_is_over(bool runtimeGone, bool endsOnShutdown, string? expected) =>
        await Assert.That(await Decide(runtimeGone, endsOnShutdown)).IsEqualTo(expected);

    /// <summary>Copilot writes the shutdown line a moment after it starts tearing its processes down.</summary>
    [Test]
    public async Task A_shutdown_written_while_the_watcher_waits_still_ends_the_session() {
        var landed = false;
        var decide = CopilotStopRequestEnd.DecideAsync(() => false, () => landed, TimeProvider.System, TimeSpan.FromSeconds(5));

        await Task.Delay(400);
        landed = true;

        await Assert.That(await decide).IsEqualTo("session_shutdown");
    }
}
