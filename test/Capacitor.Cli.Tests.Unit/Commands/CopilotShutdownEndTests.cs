using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A watcher ends a Copilot session itself once <c>session.shutdown</c> stays the transcript's last
/// line: the sessionEnd hook does not always fire, and a resume appends to the same file.</summary>
public class CopilotShutdownEndTests {
    const string Shutdown = """{"type":"session.shutdown","data":{},"id":"e1","timestamp":"2026-11-05T03:41:37.335Z"}""";
    const string Resume   = """{"type":"session.resume","data":{},"id":"e2","timestamp":"2026-11-05T03:41:38.226Z"}""";

    [TempDir] public required TempDir Tmp { get; init; }

    /// <summary>A transcript that can't be read must not cancel an end already decided.</summary>
    [Test]
    [Arguments(Shutdown, false)]
    [Arguments(Resume, true)]
    [Arguments(null, false)]
    public async Task Only_a_readable_later_line_moves_past_the_shutdown(string? last, bool moved) {
        var path = last is null ? Tmp.PathTo("missing.jsonl") : Tmp.CreateFile("events.jsonl", Shutdown + "\n" + last + "\n");

        await Assert.That(CopilotFinalizeDrainCommand.MovedPastShutdown(path)).IsEqualTo(moved);
    }

    [Test]
    public async Task A_settled_shutdown_ends_the_session_and_the_process_exiting_outranks_it() {
        await Assert.That(WatchCommand.DecideEndReason(false, false, false, false, copilotShutdown: true)).IsEqualTo("session_shutdown");
        await Assert.That(WatchCommand.DecideEndReason(true, false, false, false, copilotShutdown: true)).IsEqualTo("parent_exited");
    }

    /// <summary>A Copilot parent is found by name, so finding it dead means the runtime has stopped. Other vendors
    /// can be following a hook shell here, and wait for a re-resolve instead.</summary>
    [Test]
    [Arguments("copilot", null, true)]
    [Arguments("copilot", "agent-1", false)]
    [Arguments("kiro", null, false)]
    public async Task A_dead_Copilot_parent_ends_the_session_without_waiting(string vendor, string? agentId, bool expected) =>
        await Assert.That(WatchCommand.EndsWhenParentFoundDead(vendor, agentId)).IsEqualTo(expected);
}
