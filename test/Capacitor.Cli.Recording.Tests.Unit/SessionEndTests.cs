using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Resume;

namespace Capacitor.Cli.Recording.Tests.Unit;

/// <summary>When a recorder stops, the session end it would post is held while the daemon is resuming its hosted
/// agent, since the resumed process continues the same session, and goes to a process the session runs on in.</summary>
public class SessionEndTests {
    static HostedAgent Agent(string? id) => new(id, false, DaemonBridge.None);

    static Func<HostedAgent, bool> Ledger(ResumeState? state) =>
        hosted => state == ResumeState.Resuming;

    [Test]
    public async Task A_parent_exit_end_is_held_while_the_entry_is_resuming() {
        var decision = SessionEnd.DecideSessionEnd(runsOnElsewhere: false, Agent("agent-1"), Ledger(ResumeState.Resuming));

        await Assert.That(decision).IsEqualTo(SessionEndAction.HeldForResume);
    }

    [Test]
    public async Task A_parent_exit_end_posts_for_a_running_entry_or_no_entry() {
        await Assert.That(SessionEnd.DecideSessionEnd(false, Agent("agent-1"), Ledger(ResumeState.Running)))
            .IsEqualTo(SessionEndAction.Post);
        await Assert.That(SessionEnd.DecideSessionEnd(false, Agent("agent-1"), Ledger(null)))
            .IsEqualTo(SessionEndAction.Post);
        await Assert.That(SessionEnd.DecideSessionEnd(false, hosted: null, Ledger(ResumeState.Resuming)))
            .IsEqualTo(SessionEndAction.Post);
    }

    [Test]
    public async Task A_session_that_runs_on_elsewhere_is_handed_to_its_new_process_first() {
        var decision = SessionEnd.DecideSessionEnd(runsOnElsewhere: true, Agent("agent-1"), Ledger(ResumeState.Resuming));

        await Assert.That(decision).IsEqualTo(SessionEndAction.RunsOnElsewhere);
    }

    [Test]
    [Arguments("parent_exited", null, false, true)]
    [Arguments("idle_timeout", null, false, true)]
    [Arguments("parent_exited", "agent-1", false, false)]
    [Arguments(null, null, false, false)]
    [Arguments("idle_timeout", null, true, false)]
    public async Task The_watcher_ends_its_session_whatever_the_session_holds(string? endReason, string? agentId, bool cursorSuppresses, bool expected) {
        await Assert.That(SessionEnd.PostsOwnSessionEnd(endReason, agentId, cursorSuppresses)).IsEqualTo(expected);
    }

    /// <summary>A hosted watcher's own end names the agent and daemon epoch it runs under, so a spooled copy replayed
    /// after the session resumes reads as the predecessor's; an unhosted watcher's end names neither.</summary>
    [Test]
    public async Task The_recorders_own_end_names_the_hosted_agent_and_daemon_epoch_it_runs_under() {
        var at     = new DateTimeOffset(2026, 11, 2, 18, 26, 34, TimeSpan.Zero);
        var hosted = SessionEnd.ParentExitEndBody("s1", "/t/x.jsonl", "/repo", "parent_exited", at,
            new HostedAgent("agent-1", false, DaemonBridge.None) { DaemonEpoch = "epoch-1" });
        var plain  = SessionEnd.ParentExitEndBody("s1", "/t/x.jsonl", "/repo", "parent_exited", at, HostedAgent.Terminal);

        await Assert.That(hosted["agent_host_id"]?.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(hosted["daemon_epoch"]?.GetValue<string>()).IsEqualTo("epoch-1");
        await Assert.That(hosted["reason"]?.GetValue<string>()).IsEqualTo("parent_exited");
        await Assert.That(hosted["ended_at"]?.GetValue<string>()).IsEqualTo(at.ToString("O"));
        await Assert.That(plain.ContainsKey("agent_host_id")).IsFalse();
        await Assert.That(plain.ContainsKey("daemon_epoch")).IsFalse();
    }
}
