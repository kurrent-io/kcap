using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// An agent still unlinked well after spawn has its process checked: one that has exited ends its run
/// though its output never did, once its output has had a moment to end by itself, and one still alive
/// keeps running.
public class AgentLivenessCheckTests {
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task An_unlinked_agent_whose_process_is_gone_ends_its_run(bool exited) {
        var time = new FakeTimeProvider();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(), timeProvider: time);
        var alive = new ScriptedPtyProcess();
        IPtyProcess pty = exited ? new OneChunkThenBlockPtyProcess() : alive;
        var agent = orch.SeedAgentForTest("unlinked", isPrivate: true, pty: pty, vendor: "claude");
        var reading   = orch.ReadAgentOutputForTest(agent);
        var discovery = orch.RunDiscoveryForTest(agent, _ => null);
        try {
            time.Advance(TimeSpan.FromSeconds(61));

            if (exited) {
                await Task.Delay(200);
                await Assert.That(agent.ReadCts.IsCancellationRequested).IsFalse();

                time.Advance(AgentOrchestrator.ExitedOutputGrace);
                await reading.WaitAsync(WaitHarness.Bounded);
                await Assert.That(agent.Status).IsNotEqualTo("Running");
            } else {
                await Task.Delay(200);
                await Assert.That(reading.IsCompleted).IsFalse();
                await Assert.That(agent.Status).IsEqualTo("Running");
            }
        } finally {
            alive.Exit();
            await reading.WaitAsync(WaitHarness.Bounded);
            await discovery.WaitAsync(WaitHarness.Bounded);
        }
    }
}
