using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Core.Harness.OpenCode;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>Pins the exact bytes each vendor posts to <c>/hooks/subagent-start</c> and <c>/hooks/subagent-stop</c>:
/// key order, the empty <c>cwd</c>, and where the optional <c>spawn_call_id</c> and <c>strict</c> land.</summary>
public class SubagentHookPayloadsTests {
    const string Start =
        """{"hook_event_name":"subagent_start","session_id":"parent","agent_id":"child","agent_type":"general","transcript_path":"/t/child.jsonl","cwd":""}""";

    const string Stop =
        """{"hook_event_name":"subagent_stop","session_id":"parent","agent_id":"child","agent_type":"general","transcript_path":"/t/child.jsonl","cwd":"","stop_hook_active":false,"agent_transcript_path":"/t/child.jsonl","last_assistant_message":""}""";

    public static IEnumerable<Func<(string Name, JsonObject Payload, string Expected)>> Payloads() {
        yield return () => ("gemini start", GeminiSubagentDiscovery.BuildStartPayload("parent", "child", "general", "/t/child.jsonl"), Start);
        yield return () => ("gemini stop", GeminiSubagentDiscovery.BuildStopPayload("parent", "child", "general", "/t/child.jsonl"), Stop);
        yield return () => ("opencode start", OpenCodeSubagentDiscovery.BuildStartPayload("parent", "child", "general", "/t/child.jsonl"), Start);
        yield return () => ("opencode stop", OpenCodeSubagentDiscovery.BuildStopPayload("parent", "child", "general", "/t/child.jsonl"), Stop);
        yield return () => ("muse start", MuseSubagentDiscovery.BuildStartPayload("parent", "child", "general", "/t/child.jsonl"), Start);
        yield return () => ("muse stop", MuseSubagentDiscovery.BuildStopPayload("parent", "child", "general", "/t/child.jsonl"), Stop);
        yield return () => ("codex start", CodexSubagentDiscovery.BuildStartPayload("parent", "child", "general", "/t/child.jsonl"), Start);
        yield return () => ("codex stop", CodexSubagentDiscovery.BuildStopPayload("parent", "child", "general", "/t/child.jsonl"), Stop);
        yield return () => (
            "codex start with spawn call",
            CodexSubagentDiscovery.BuildStartPayload("parent", "child", "general", "/t/child.jsonl", "call_1"),
            Start[..^1] + ""","spawn_call_id":"call_1"}""");
    }

    [Test]
    [MethodDataSource(nameof(Payloads))]
    public async Task Payload_serializes_to_the_pinned_bytes((string Name, JsonObject Payload, string Expected) c) =>
        await Assert.That(c.Payload.ToJsonString()).IsEqualTo(c.Expected);
}
