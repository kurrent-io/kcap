using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AgentOrchestratorUnstartedRoundTests {
    /// A reviewer whose round started no turn is terminated even while the rejection report hangs:
    /// alive, it could still take a pending Enter and start a turn after the verdict.
    [Test]
    public async Task A_round_that_never_started_terminates_the_reviewer_without_waiting_on_the_report() {
        var gate     = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server   = new CaptureServerConnection { InputRejectionGate = gate };
        var runtime  = new FakeHostedAgentRuntime("codex", emitsTerminalOutput: true);
        var dispatch = Guid.NewGuid();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var agent = orch.SeedAgentForTest("unstarted-1", kind: LaunchKind.ReviewFlow, runtime: runtime);

        var failing = orch.FailUnstartedCodexRoundAsync(agent, dispatch, resubmitWritten: false);
        await runtime.ExitGate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(failing.IsCompleted).IsFalse();
        await Assert.That(server.InputRejections.Single())
            .IsEqualTo((dispatch, agent.Id, AgentOrchestrator.SendInputDropReason.TurnNotStarted));
        await Assert.That(agent.StopDetail).IsEqualTo(AgentOrchestrator.CodexRoundNotStartedReason);

        gate.SetResult();
        await failing.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
