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

    /// <summary>The round is over once its rejection is decided, so a resend is refused the same way, never
    /// acknowledged: the first report may not have reached the server.</summary>
    [Test]
    public async Task A_resend_after_the_unstarted_round_rejection_is_refused_again() {
        var server   = new CaptureServerConnection();
        var runtime  = new FakeHostedAgentRuntime("codex", emitsTerminalOutput: true);
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var agent = orch.SeedAgentForTest("unstarted-2", runtime: runtime);
        var cmd   = new SendInputCommand(agent.Id, "round 2", null, Guid.NewGuid());

        await orch.HandleSendInputForTest(cmd);
        await orch.FailUnstartedCodexRoundAsync(agent, cmd.DispatchId, resubmitWritten: false);
        await orch.HandleSendInputForTest(cmd);

        await Assert.That(server.InputAcks.Count).IsEqualTo(1);
        await Assert.That(server.InputRejections).IsEquivalentTo([
            (cmd.DispatchId!.Value, agent.Id, AgentOrchestrator.SendInputDropReason.TurnNotStarted),
            (cmd.DispatchId!.Value, agent.Id, AgentOrchestrator.SendInputDropReason.TurnNotStarted)
        ]);
    }
}
