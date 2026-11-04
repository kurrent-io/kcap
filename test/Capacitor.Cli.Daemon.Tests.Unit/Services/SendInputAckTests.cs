using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The server resends a dispatched input until this daemon acknowledges it, so every input carrying a
/// dispatch id is acknowledged once taken, and a resend of one is answered again without typing it twice.
/// </summary>
public class SendInputAckTests {
    static AgentOrchestrator Build(CaptureServerConnection server) =>
        AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

    static int Typed(RecordingPtyProcess pty, string text) => pty.Writes.Count(w => w.Contains(text, StringComparison.Ordinal));

    [Test]
    public async Task A_delivered_input_is_acknowledged_and_a_resend_is_acknowledged_again_without_typing_it_twice() {
        var server = new CaptureServerConnection();
        var pty    = new RecordingPtyProcess();
        await using var orch = Build(server);
        var agent = orch.SeedAgentForTest("ack-1", pty: pty);
        var cmd   = new SendInputCommand(agent.Id, "review round 2", null, Guid.NewGuid());

        await orch.HandleSendInputForTest(cmd);
        await orch.HandleSendInputForTest(cmd);

        await Assert.That(Typed(pty, "review round 2")).IsEqualTo(1);
        await Assert.That(server.InputAcks).IsEquivalentTo([(cmd.DispatchId!.Value, agent.Id), (cmd.DispatchId!.Value, agent.Id)]);
    }

    [Test]
    public async Task A_resend_arriving_while_the_first_copy_is_being_delivered_is_ignored() {
        var server  = new CaptureServerConnection();
        var pty     = new RecordingPtyProcess();
        await using var orch = Build(server);
        var agent   = orch.SeedAgentForTest("ack-2", pty: pty);
        var cmd     = new SendInputCommand(agent.Id, "review round 3", null, Guid.NewGuid());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        orch.SendInputBeforeWriteHookForTest = () => {
            entered.TrySetResult();

            return release.Task;
        };

        var first = orch.HandleSendInputForTest(cmd);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await orch.HandleSendInputForTest(cmd).WaitAsync(TimeSpan.FromSeconds(10));
        release.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(Typed(pty, "review round 3")).IsEqualTo(1);
        await Assert.That(server.InputAcks.Count).IsEqualTo(1);
    }

    /// <summary>The refusal is final: a resend that arrives once the agent exists is refused again, not typed.</summary>
    [Test]
    public async Task A_resend_of_a_dropped_input_is_refused_again_and_never_typed() {
        var server = new CaptureServerConnection();
        var pty    = new RecordingPtyProcess();
        await using var orch = Build(server);
        var cmd = new SendInputCommand("ack-late", "hello", null, Guid.NewGuid());

        await orch.HandleSendInputForTest(cmd);
        orch.SeedAgentForTest("ack-late", pty: pty);
        await orch.HandleSendInputForTest(cmd);

        await Assert.That(server.InputRejections).IsEquivalentTo([
            (cmd.DispatchId!.Value, "ack-late", AgentOrchestrator.SendInputDropReason.UnknownAgent),
            (cmd.DispatchId!.Value, "ack-late", AgentOrchestrator.SendInputDropReason.UnknownAgent)
        ]);
        await Assert.That(Typed(pty, "hello")).IsEqualTo(0);
        await Assert.That(server.InputAcks).IsEmpty();
    }

    /// <summary>A server that sends no dispatch id never resends, so it is answered as it always was: nothing.</summary>
    [Test]
    public async Task An_input_with_no_dispatch_id_is_typed_each_time_and_never_acknowledged() {
        var server = new CaptureServerConnection();
        var pty    = new RecordingPtyProcess();
        await using var orch = Build(server);
        var agent = orch.SeedAgentForTest("ack-3", pty: pty);

        await orch.HandleSendInputForTest(new SendInputCommand(agent.Id, "again", null));
        await orch.HandleSendInputForTest(new SendInputCommand(agent.Id, "again", null));

        await Assert.That(Typed(pty, "again")).IsEqualTo(2);
        await Assert.That(server.InputAcks).IsEmpty();
    }
}
