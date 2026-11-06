using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Recording.Tests.Unit;

public class CodexToolTrackingSourceTests {
    [Test]
    [Arguments("codex",  true)]
    [Arguments("claude", false)]
    [Arguments("cursor", false)]
    [Arguments("gemini", false)]
    public async Task TracksCodexToolCalls_only_for_codex(string vendor, bool expected) =>
        await Assert.That(ToolCallTracking.TracksCodexToolCalls(vendor)).IsEqualTo(expected);

    const string FunctionCall =
        """{"type":"response_item","payload":{"type":"function_call","call_id":"call_big","name":"shell","arguments":"{}"}}""";

    static string OversizedFunctionCallOutput() =>
        "{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\",\"call_id\":\"call_big\",\"output\":\""
      + new string('x', SecretRedactor.MaxRecordBytes + 1024)
      + "\"}}";

    // A stranded call_id pins toolInFlight true, and for Codex the idle timeout is the only
    // per-conversation session-end path — so the session stays Active forever.
    [Test]
    public async Task RedactedLines_StrandTheCallId_ButRawLinesClearIt() {
        var viaRedacted = new HashSet<string>(StringComparer.Ordinal);
        ToolCallTracking.UpdateCodexPendingToolCalls(viaRedacted, SecretRedactor.RedactLine(FunctionCall));
        ToolCallTracking.UpdateCodexPendingToolCalls(viaRedacted, SecretRedactor.RedactLine(OversizedFunctionCallOutput()));

        await Assert.That(viaRedacted.Contains("call_big")).IsTrue();

        var viaRaw = new HashSet<string>(StringComparer.Ordinal);
        ToolCallTracking.UpdateCodexPendingToolCalls(viaRaw, FunctionCall);
        ToolCallTracking.UpdateCodexPendingToolCalls(viaRaw, OversizedFunctionCallOutput());

        await Assert.That(viaRaw.Count).IsEqualTo(0);
    }

    // Other direction on the same placeholder: an unrecognised response_item leaves a re-engaged
    // child looking finished, so it is reported stopped while still working.
    [Test]
    public async Task RedactedResponseItem_FailsToReopenTheTurn_ButTheRawOneDoes() {
        var viaRedacted = new CodexSubagentTurnTracker();
        viaRedacted.Observe("""{"type":"event_msg","payload":{"type":"task_complete"}}""");
        viaRedacted.Observe(SecretRedactor.RedactLine(OversizedFunctionCallOutput()));

        await Assert.That(viaRedacted.TurnCompleted).IsTrue();

        var viaRaw = new CodexSubagentTurnTracker();
        viaRaw.Observe("""{"type":"event_msg","payload":{"type":"task_complete"}}""");
        viaRaw.Observe(OversizedFunctionCallOutput());

        await Assert.That(viaRaw.TurnCompleted).IsFalse();
    }
}
