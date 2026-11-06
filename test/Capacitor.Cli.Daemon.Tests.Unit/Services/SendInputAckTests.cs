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

    static int Count<T>(List<T> list) {
        lock (list) return list.Count;
    }

    static async Task Until(Func<bool> condition) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
    }

    /// <summary>Taking the input is what is acknowledged, not its write: the server never resends to another
    /// daemon process, so holding the acknowledgement for a write behind a long turn would recover nothing.</summary>
    [Test]
    public async Task An_input_queued_behind_a_turn_is_acknowledged_once_taken() {
        var server = new CaptureServerConnection();
        await using var orch = Build(server);
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "ack-queued", new FakeAcpRuntime { DispatchedWrite = new() });
        var cmd   = new SendInputCommand(agent.Id, "queued", null, Guid.NewGuid());

        await orch.HandleSendInputForTest(cmd).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(server.InputAcks).IsEquivalentTo([(cmd.DispatchId!.Value, agent.Id)]);
    }

    [Test]
    public async Task A_resend_while_the_first_copy_is_queued_is_acknowledged_again_but_not_typed() {
        var server  = new CaptureServerConnection();
        var runtime = new FakeAcpRuntime { DispatchedWrite = new() };
        await using var orch = Build(server);
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "ack-queued-resend", runtime);
        var cmd   = new SendInputCommand(agent.Id, "queued once", null, Guid.NewGuid());

        await orch.HandleSendInputForTest(cmd);
        await orch.HandleSendInputForTest(cmd);

        await Assert.That(Count(runtime.SentInputs)).IsEqualTo(1);
        await Assert.That(server.InputAcks).IsEquivalentTo([(cmd.DispatchId!.Value, agent.Id), (cmd.DispatchId!.Value, agent.Id)]);
    }

    /// <summary>A queued input the runtime later loses is reported dropped after its acknowledgement, and a
    /// resend is refused rather than typed into the runtime that lost it.</summary>
    [Test]
    public async Task A_queued_input_whose_write_fails_is_reported_dropped_after_its_ack_and_a_resend_is_refused() {
        var server  = new CaptureServerConnection();
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeAcpRuntime { DispatchedWrite = written };
        await using var orch = Build(server);
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "ack-queued-fails", runtime);
        var cmd   = new SendInputCommand(agent.Id, "never written", null, Guid.NewGuid());

        await orch.HandleSendInputForTest(cmd);
        written.SetException(new InvalidOperationException("session ended while this input was pending delivery"));
        await Until(() => Count(server.InputRejections) > 0);
        await orch.HandleSendInputForTest(cmd);

        await Assert.That(server.InputAcks).IsEquivalentTo([(cmd.DispatchId!.Value, agent.Id)]);
        await Assert.That(server.InputRejections).IsEquivalentTo([
            (cmd.DispatchId!.Value, agent.Id, AgentOrchestrator.SendInputDropReason.DeliveryFailed),
            (cmd.DispatchId!.Value, agent.Id, AgentOrchestrator.SendInputDropReason.DeliveryFailed)
        ]);
        await Assert.That(Count(runtime.SentInputs)).IsEqualTo(1);
    }

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
