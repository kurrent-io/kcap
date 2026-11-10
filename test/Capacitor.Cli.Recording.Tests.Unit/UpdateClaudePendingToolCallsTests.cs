namespace Capacitor.Cli.Recording.Tests.Unit;

public class UpdateClaudePendingToolCallsTests {
    [Test]
    public async Task ToolUse_AddsId() {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        const string line = """{"isSidechain":true,"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_01A","name":"Bash","input":{"command":"ls"}}]}}""";

        ToolCallTracking.UpdateClaudePendingToolCalls(pending, line);

        await Assert.That(pending.Contains("toolu_01A")).IsTrue();
    }

    [Test]
    public async Task ToolResult_RemovesId() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "toolu_01A" };
        const string line = """{"isSidechain":true,"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_01A","type":"tool_result","content":"ok"}]}}""";

        ToolCallTracking.UpdateClaudePendingToolCalls(pending, line);

        await Assert.That(pending.Contains("toolu_01A")).IsFalse();
    }

    // One assistant turn can start several tools before any result arrives.
    [Test]
    public async Task MultipleToolUses_InOneMessage_AllTracked() {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        const string line = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"working"},{"type":"tool_use","id":"toolu_01A","name":"Bash","input":{}},{"type":"tool_use","id":"toolu_01B","name":"Read","input":{}}]}}""";

        ToolCallTracking.UpdateClaudePendingToolCalls(pending, line);

        await Assert.That(pending.Count).IsEqualTo(2);
        await Assert.That(pending.Contains("toolu_01B")).IsTrue();
    }

    [Test]
    public async Task AssistantTextOnly_LeavesSetUnchanged() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "existing" };
        const string line = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"just thinking"}]}}""";

        ToolCallTracking.UpdateClaudePendingToolCalls(pending, line);

        await Assert.That(pending.Count).IsEqualTo(1);
        await Assert.That(pending.Contains("existing")).IsTrue();
    }

    // Claude Code writes a string content for plain user prompts, not an array.
    [Test]
    public async Task StringContent_LeavesSetUnchanged() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "existing" };
        const string line = """{"type":"user","message":{"role":"user","content":"go on then"}}""";

        ToolCallTracking.UpdateClaudePendingToolCalls(pending, line);

        await Assert.That(pending.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CodexLine_LeavesSetUnchanged() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "existing" };
        const string line = """{"type":"response_item","payload":{"type":"function_call","call_id":"call_1","name":"shell","arguments":"{}"}}""";

        ToolCallTracking.UpdateClaudePendingToolCalls(pending, line);

        await Assert.That(pending.Count).IsEqualTo(1);
        await Assert.That(pending.Contains("existing")).IsTrue();
    }

    [Test]
    public async Task MalformedJson_LeavesSetUnchanged_NoThrow() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "existing" };

        ToolCallTracking.UpdateClaudePendingToolCalls(pending, "not json at all {{}}");

        await Assert.That(pending.Count).IsEqualTo(1);
        await Assert.That(pending.Contains("existing")).IsTrue();
    }
}
