using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>Pins the exact bytes posted to <c>/hooks/subagent-start</c> and <c>/hooks/subagent-stop</c>:
/// key order, the empty <c>cwd</c>, and where the optional <c>spawn_call_id</c> and <c>strict</c> land.</summary>
public class SubagentHookPayloadsTests {
    const string Start =
        """{"hook_event_name":"subagent_start","session_id":"parent","agent_id":"child","agent_type":"general","transcript_path":"/t/child.jsonl","cwd":""}""";

    const string Stop =
        """{"hook_event_name":"subagent_stop","session_id":"parent","agent_id":"child","agent_type":"general","transcript_path":"/t/child.jsonl","cwd":"","stop_hook_active":false,"agent_transcript_path":"/t/child.jsonl","last_assistant_message":""}""";

    public static IEnumerable<Func<(string Name, JsonObject Payload, string Expected)>> Payloads() {
        yield return () => ("start", SubagentHookPayloads.Start("parent", "child", "general", "/t/child.jsonl"), Start);
        yield return () => ("stop", SubagentHookPayloads.Stop("parent", "child", "general", "/t/child.jsonl"), Stop);
        yield return () => (
            "start with spawn call",
            SubagentHookPayloads.Start("parent", "child", "general", "/t/child.jsonl", "call_1"),
            Start[..^1] + ""","spawn_call_id":"call_1"}""");
        yield return () => (
            "strict start",
            SubagentHookPayloads.Start("parent", "child", "general", "/t/child.jsonl", strict: true),
            Start[..^1] + ""","strict":true}""");
        yield return () => (
            "strict stop",
            SubagentHookPayloads.Stop("parent", "child", "general", "/t/child.jsonl", strict: true),
            Stop[..^1] + ""","strict":true}""");
    }

    [Test]
    [MethodDataSource(nameof(Payloads))]
    public async Task Payload_serializes_to_the_pinned_bytes((string Name, JsonObject Payload, string Expected) c) =>
        await Assert.That(c.Payload.ToJsonString()).IsEqualTo(c.Expected);
}
