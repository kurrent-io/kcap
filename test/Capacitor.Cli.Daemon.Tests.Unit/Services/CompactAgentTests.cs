using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// A server's <c>CompactAgent</c>: a terminal agent gets <c>/compact</c> typed, refused while a prompt is open that it
/// could reach, and a second request while the first is queued or running is dropped, so two viewers compact once.
/// </summary>
public class CompactAgentTests {
    static AgentOrchestrator Build(CaptureServerConnection server) =>
        AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

    static int Typed(RecordingPtyProcess pty) => pty.Writes.Count(w => w.Contains("/compact", StringComparison.Ordinal));

    static async Task Until(Func<bool> condition) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
    }

    [Test]
    public async Task A_claude_agent_has_the_command_typed_and_the_request_acknowledged() {
        var server = new CaptureServerConnection();
        var pty    = new RecordingPtyProcess();
        await using var orch = Build(server);
        var agent = orch.SeedAgentForTest("compact-1", pty: pty, vendor: "claude");
        var cmd   = new CompactAgentCommand(agent.Id, Guid.NewGuid());

        await orch.HandleCompactAgentForTest(cmd);
        await Until(() => Typed(pty) > 0);

        await Assert.That(Typed(pty)).IsEqualTo(1);
        await Assert.That(server.InputAcks).IsEquivalentTo([(cmd.DispatchId, agent.Id)]);
        await Assert.That(server.InputRejections).IsEmpty();
    }

    [Test]
    public async Task A_second_request_while_the_first_runs_is_dropped() {
        var server = new CaptureServerConnection();
        var pty    = new RecordingPtyProcess();
        await using var orch = Build(server);
        var agent  = orch.SeedAgentForTest("compact-twice", pty: pty, vendor: "claude");
        var first  = new CompactAgentCommand(agent.Id, Guid.NewGuid());
        var second = new CompactAgentCommand(agent.Id, Guid.NewGuid());

        await orch.HandleCompactAgentForTest(first);
        await orch.HandleCompactAgentForTest(second);
        await Until(() => Typed(pty) > 0);

        await Assert.That(Typed(pty)).IsEqualTo(1);
        await Assert.That(server.InputRejections)
            .IsEquivalentTo([(second.DispatchId, agent.Id, AgentOrchestrator.SendInputDropReason.CompactionRunning)]);
    }

    [Test]
    public async Task A_resend_of_one_request_is_acknowledged_again_without_typing_twice() {
        var server = new CaptureServerConnection();
        var pty    = new RecordingPtyProcess();
        await using var orch = Build(server);
        var agent = orch.SeedAgentForTest("compact-resend", pty: pty, vendor: "claude");
        var cmd   = new CompactAgentCommand(agent.Id, Guid.NewGuid());

        await orch.HandleCompactAgentForTest(cmd);
        await orch.HandleCompactAgentForTest(cmd);
        await Until(() => Typed(pty) > 0);

        await Assert.That(Typed(pty)).IsEqualTo(1);
        await Assert.That(server.InputAcks).IsEquivalentTo([(cmd.DispatchId, agent.Id), (cmd.DispatchId, agent.Id)]);
        await Assert.That(server.InputRejections).IsEmpty();
    }

    [Test]
    public async Task A_terminal_dialog_the_daemon_has_read_refuses_it() {
        var server = new CaptureServerConnection();
        var pty    = new RecordingPtyProcess();
        await using var orch = Build(server);
        var agent = orch.SeedAgentForTest("compact-dialog", pty: pty, vendor: "claude");
        agent.TerminalDialog = new TerminalDialogDto("Allow?", "", ["No", "Yes"], 0, "");
        var cmd = new CompactAgentCommand(agent.Id, Guid.NewGuid());

        await orch.HandleCompactAgentForTest(cmd);

        await Assert.That(Typed(pty)).IsEqualTo(0);
        await Assert.That(server.InputRejections)
            .IsEquivalentTo([(cmd.DispatchId, agent.Id, AgentOrchestrator.SendInputDropReason.PromptOpen)]);
    }

    /// <summary>The runtime decides under the gate a dialog opens under, so one it has been told of refuses the
    /// compaction even before the orchestrator's copy of the dialog is set.</summary>
    [Test]
    public async Task A_dialog_the_runtime_holds_input_for_refuses_it() {
        var server = new CaptureServerConnection();
        var pty    = new RecordingPtyProcess();
        await using var orch = Build(server);
        var agent = orch.SeedAgentForTest("compact-runtime-dialog", pty: pty, vendor: "claude");
        agent.Runtime.NoteTerminalDialog(true);
        var cmd = new CompactAgentCommand(agent.Id, Guid.NewGuid());

        await orch.HandleCompactAgentForTest(cmd);

        await Assert.That(Typed(pty)).IsEqualTo(0);
        await Assert.That(server.InputRejections)
            .IsEquivalentTo([(cmd.DispatchId, agent.Id, AgentOrchestrator.SendInputDropReason.PromptOpen)]);
    }

    [Test]
    public async Task A_pending_permission_prompt_refuses_it() {
        var server = new CaptureServerConnection();
        var pty    = new RecordingPtyProcess();
        await using var orch = Build(server);
        var agent = orch.SeedAgentForTest("compact-permission", pty: pty, vendor: "claude");
        _ = orch.PermissionBrokerForTest.Register(new PermissionPendingDto(
            "req-1", agent.Id, "session-1", "claude", "Bash", null, null, false, false, "2026-01-01T00:00:00Z"));
        var cmd = new CompactAgentCommand(agent.Id, Guid.NewGuid());

        await orch.HandleCompactAgentForTest(cmd);

        await Assert.That(Typed(pty)).IsEqualTo(0);
        await Assert.That(server.InputRejections)
            .IsEquivalentTo([(cmd.DispatchId, agent.Id, AgentOrchestrator.SendInputDropReason.PromptOpen)]);
    }

    [Test]
    public async Task A_review_participant_is_not_compacted() {
        var server = new CaptureServerConnection();
        var pty    = new RecordingPtyProcess();
        await using var orch = Build(server);
        var agent = orch.SeedAgentForTest("compact-reviewer", kind: LaunchKind.ReviewFlow, pty: pty, vendor: "claude");
        var cmd   = new CompactAgentCommand(agent.Id, Guid.NewGuid());

        await orch.HandleCompactAgentForTest(cmd);

        await Assert.That(Typed(pty)).IsEqualTo(0);
        await Assert.That(server.InputRejections)
            .IsEquivalentTo([(cmd.DispatchId, agent.Id, AgentOrchestrator.SendInputDropReason.CompactionUnsupported)]);
    }

    [Test]
    public async Task A_terminal_vendor_without_the_command_is_not_compacted() {
        var server = new CaptureServerConnection();
        var pty    = new RecordingPtyProcess();
        await using var orch = Build(server);
        var agent = orch.SeedAgentForTest("compact-gemini", pty: pty, vendor: "gemini");
        var cmd   = new CompactAgentCommand(agent.Id, Guid.NewGuid());

        await orch.HandleCompactAgentForTest(cmd);

        await Assert.That(Typed(pty)).IsEqualTo(0);
        await Assert.That(server.InputRejections)
            .IsEquivalentTo([(cmd.DispatchId, agent.Id, AgentOrchestrator.SendInputDropReason.CompactionUnsupported)]);
    }

    [Test]
    public async Task An_unknown_agent_is_reported() {
        var server = new CaptureServerConnection();
        await using var orch = Build(server);
        var cmd = new CompactAgentCommand("nobody", Guid.NewGuid());

        await orch.HandleCompactAgentForTest(cmd);

        await Assert.That(server.InputRejections)
            .IsEquivalentTo([(cmd.DispatchId, "nobody", AgentOrchestrator.SendInputDropReason.UnknownAgent)]);
    }
}
