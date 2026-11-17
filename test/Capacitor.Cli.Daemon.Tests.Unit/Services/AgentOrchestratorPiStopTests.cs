using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>An owner stop of an interactive Pi agent, driven through a real
/// <see cref="Capacitor.Cli.Daemon.Harness.Pi.PiRpcHostedAgentRuntime"/>.</summary>
[ParallelLimiter<SubprocessLimit>]
public class AgentOrchestratorPiStopTests {
    [TempDir] public required TempDir Tmp { get; init; }

    /// <summary>Pi exits on its own once stdin closes, so an owner stop ends it cleanly: Completed with
    /// its session, never a kill's exit code and never a Failed that the web UI renders as a launch
    /// failure.</summary>
    [Test]
    public async Task An_owner_stop_lets_pi_exit_cleanly_and_reports_completed() {
        var server = new CaptureServerConnection();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var (runtime, process) = PiRpcRuntimeFakes.NewRuntime();
        process.ExitsOnInputClose = true;
        await runtime.WaitForSessionReadyAsync(CancellationToken.None);
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "pi-stop-1", runtime, status: "Running");
        agent.SessionId = PiRpcRuntimeFakes.PiSessionId;

        await orch.HandleStopAgentForTest("pi-stop-1");
        await orch.FinalizeAgentRunForTest(agent).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(agent.Status).IsEqualTo("Completed");
        await Assert.That(server.StatusChangedWithSession)
            .Contains(("pi-stop-1", "Completed", PiRpcRuntimeFakes.PiSessionId));
        await Assert.That(server.StatusChangedWithSession.Any(c => c.Status == "Failed")).IsFalse();
        await Assert.That(runtime.ExitCode).IsEqualTo(0);
    }

    /// <summary>Pi's resume token is its session file's absolute path, a file that exists, and it names no person's
    /// disable, so the stop still ends the session on the server.</summary>
    [Test]
    public async Task A_stopped_pi_agent_ends_its_session_though_its_resume_token_is_a_file_that_exists() {
        var server = new CaptureServerConnection();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var agent = await SeedPiAgentResumableFromItsSessionFile(orch, "pi-stop-2");

        await orch.HandleStopAgentForTest("pi-stop-2");
        await orch.FinalizeAgentRunForTest(agent).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(server.EndSessionReasons).Contains(agent.PendingEndReason);
    }

    [Test]
    public async Task A_pi_agents_title_view_keeps_its_session_though_its_resume_token_is_a_file_that_exists() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var agent = await SeedPiAgentResumableFromItsSessionFile(orch, "pi-title-1");

        await Assert.That(orch.SnapshotAgentsForTitles().Single(view => view.Id == agent.Id).SessionId).IsEqualTo(PiRpcRuntimeFakes.PiSessionId);
    }

    /// <summary>A running Pi agent whose resume token is set as a launch sets it, from the runtime.</summary>
    async Task<AgentInstance> SeedPiAgentResumableFromItsSessionFile(AgentOrchestrator orch, string agentId) {
        var sessionFile = Tmp.CreateFile("session.jsonl");
        var (runtime, process) = PiRpcRuntimeFakes.NewRuntime(PiRpcRuntimeFakes.GetStateResponse(sessionFile: sessionFile));
        process.ExitsOnInputClose = true;
        await runtime.WaitForSessionReadyAsync(CancellationToken.None);

        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, agentId, runtime, status: "Running");
        agent.SessionId     = PiRpcRuntimeFakes.PiSessionId;
        agent.VendorSession = runtime.VendorResumeToken;
        await Assert.That(agent.VendorSession).IsEqualTo(sessionFile);

        return agent;
    }
}
