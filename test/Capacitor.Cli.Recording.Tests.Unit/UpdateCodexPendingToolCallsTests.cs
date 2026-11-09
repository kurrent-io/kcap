namespace Capacitor.Cli.Recording.Tests.Unit;

public class UpdateCodexPendingToolCallsTests {
    [Test]
    public async Task FunctionCall_AddsCallId() {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        const string line = """{"type":"response_item","payload":{"type":"function_call","call_id":"call_1","name":"shell","arguments":"{}"}}""";

        ToolCallTracking.UpdateCodexPendingToolCalls(pending, line);

        await Assert.That(pending.Contains("call_1")).IsTrue();
    }

    [Test]
    public async Task CustomToolCall_AddsCallId() {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        const string line = """{"type":"response_item","payload":{"type":"custom_tool_call","call_id":"call_2","name":"my_tool","arguments":"{}"}}""";

        ToolCallTracking.UpdateCodexPendingToolCalls(pending, line);

        await Assert.That(pending.Contains("call_2")).IsTrue();
    }

    [Test]
    public async Task FunctionCallOutput_RemovesCallId() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "call_1" };
        const string line = """{"type":"response_item","payload":{"type":"function_call_output","call_id":"call_1","output":"done"}}""";

        ToolCallTracking.UpdateCodexPendingToolCalls(pending, line);

        await Assert.That(pending.Contains("call_1")).IsFalse();
    }

    [Test]
    public async Task CustomToolCallOutput_RemovesCallId() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "call_2" };
        const string line = """{"type":"response_item","payload":{"type":"custom_tool_call_output","call_id":"call_2","output":"done"}}""";

        ToolCallTracking.UpdateCodexPendingToolCalls(pending, line);

        await Assert.That(pending.Contains("call_2")).IsFalse();
    }

    [Test]
    public async Task MessageResponseItem_LeavesSetUnchanged() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "existing" };
        const string line = """{"type":"response_item","payload":{"type":"message","role":"assistant","content":[]}}""";

        ToolCallTracking.UpdateCodexPendingToolCalls(pending, line);

        // Set unchanged — still contains "existing", nothing added
        await Assert.That(pending.Count).IsEqualTo(1);
        await Assert.That(pending.Contains("existing")).IsTrue();
    }

    [Test]
    public async Task MalformedJson_LeavesSetUnchanged_NoThrow() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "existing" };

        // Must not throw; set must remain unchanged
        ToolCallTracking.UpdateCodexPendingToolCalls(pending, "not json at all {{}}");

        await Assert.That(pending.Count).IsEqualTo(1);
        await Assert.That(pending.Contains("existing")).IsTrue();
    }
}
