using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// What releases a launch prompt held for SessionStart, and how the hold reaches a viewer.
public class AgentOrchestratorSessionStartTests {
    const string SessionId = "0123456789abcdef0123456789abcdef";

    static (PtyHostedAgentRuntime Runtime, RecordingScriptedPtyProcess Pty) HeldRuntime(TimeProvider time) {
        var pty     = new RecordingScriptedPtyProcess();
        var runtime = new PtyHostedAgentRuntime("claude", pty, time, awaitsSessionStart: true, confirmsLaunchPrompt: true);

        runtime.DeliverLaunchPrompt("build it");
        _ = Task.Run(async () => { await foreach (var _ in runtime.ReadOutputAsync()) { } });
        pty.Emit("first-run screen");

        return (runtime, pty);
    }

    static AgentOrchestrator Orchestrator() =>
        AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

    static async Task WaitForAsync(Func<bool> done) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(25);
    }

    static async Task AdvanceAsync(FakeTimeProvider time, TimeSpan span) {
        for (var advanced = TimeSpan.Zero; advanced < span; advanced += TimeSpan.FromMilliseconds(250)) {
            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task A_relayed_session_start_releases_the_agents_launch_prompt() {
        var (runtime, pty) = HeldRuntime(TimeProvider.System);
        await using var orch = Orchestrator();
        var agent = orch.SeedAgentForTest("held-1", vendor: "claude", runtime: runtime);

        orch.PermissionBridgeForTest.SessionStartHandler!(agent.Id);
        await WaitForAsync(() => pty.Writes.Count >= 2);

        await Assert.That(string.Join("|", pty.Writes)).IsEqualTo("\x1b[200~build it\x1b[201~|\r");
        pty.Exit();
    }

    [Test]
    public async Task A_session_start_for_an_agent_it_does_not_hold_is_dropped() {
        await using var orch = Orchestrator();

        orch.PermissionBridgeForTest.SessionStartHandler!("gone");

        await Assert.That(orch.SnapshotAgentsForStatus()).IsEmpty();
    }

    /// An older kcap on the agent's PATH runs SessionStart but sends no notice. Claude's transcript
    /// begins with that hook's own record, so a fresh agent's file is proof enough.
    [Test]
    public async Task A_fresh_agents_transcript_releases_its_launch_prompt() {
        var (runtime, pty) = HeldRuntime(TimeProvider.System);
        await using var orch = Orchestrator();
        var agent = orch.SeedAgentForTest("held-2", vendor: "claude", runtime: runtime, bridgedSinceStart: true);

        await orch.RunDiscoveryForTest(agent, _ => (SessionId, "/t.jsonl"));
        await WaitForAsync(() => pty.Writes.Count >= 2);

        await Assert.That(string.Join("|", pty.Writes)).IsEqualTo("\x1b[200~build it\x1b[201~|\r");
        pty.Exit();
    }

    /// A resumed session's transcript predates the spawn, so finding it says nothing about the REPL.
    [Test]
    public async Task A_resumed_agents_transcript_leaves_its_launch_prompt_held() {
        var time = new FakeTimeProvider();
        var (runtime, pty) = HeldRuntime(time);
        await using var orch = Orchestrator();
        var agent = orch.SeedAgentForTest("held-3", vendor: "claude", runtime: runtime);

        await orch.RunDiscoveryForTest(agent, _ => (SessionId, "/t.jsonl"));
        await AdvanceAsync(time, PtyHostedAgentRuntime.LaunchPromptQuietWindow * 3);

        await Assert.That(pty.Writes).IsEmpty();
        pty.Exit();
    }

    /// The app reads the hold from the status snapshot, so going overdue and clearing must each
    /// republish it, or the note would never show or never go.
    [Test]
    public async Task A_hold_going_overdue_and_its_release_are_both_republished() {
        var time = new FakeTimeProvider();
        var (runtime, pty) = HeldRuntime(time);
        await using var orch = Orchestrator();
        var agent = orch.SeedAgentForTest("held-4", vendor: "claude", runtime: runtime);
        _ = orch.WatchLaunchPromptForTest(agent);

        var before = orch.StatusNotifierForTest.Version;
        await AdvanceAsync(time, PtyHostedAgentRuntime.LaunchPromptConfirmWait + TimeSpan.FromSeconds(1));
        await WaitForAsync(() => orch.StatusNotifierForTest.Version > before);

        await Assert.That(orch.StatusNotifierForTest.Version).IsGreaterThan(before);
        await Assert.That(runtime.LaunchPromptUnconfirmed).IsTrue();

        var overdue = orch.StatusNotifierForTest.Version;
        orch.PermissionBridgeForTest.SessionStartHandler!(agent.Id);

        await Assert.That(orch.StatusNotifierForTest.Version).IsGreaterThan(overdue);
        await Assert.That(runtime.LaunchPromptUnconfirmed).IsFalse();
        pty.Exit();
    }
}
