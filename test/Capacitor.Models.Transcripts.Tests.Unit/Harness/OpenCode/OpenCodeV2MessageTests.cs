using Capacitor.Models.Transcripts.Harness.OpenCode;
using Kurrent.Agent.Schema.Events;
using TUnit.Assertions.Enums;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.OpenCode;

/// <summary>OpenCode v2 messages through the OpenCode projection, beside the recorded transcripts its golden tests
/// replay: the cases those transcripts do not reach.</summary>
public class OpenCodeV2MessageTests {
    static readonly DateTimeOffset Received = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    static ProjectionResult P(string line) =>
        OpenCodeTranscriptEvents.Instance.Project(line, 1, Received, OpenCodeTranscriptEvents.Instance.CreateContext("ses_1", null));

    static IReadOnlyList<CanonicalEvent> E(string line) => P(line).Events;

    static string Assistant(string content) =>
        $$"""{"id":"msg_a","type":"assistant","time":{"created":1795148345027,"completed":1795148347313},"model":{"id":"big-pickle"},"content":[{{content}}]}""";

    /// <summary>A text or reasoning part is keyed by its position among all the message's parts, a tool's included,
    /// and a tool by the message and its call id.</summary>
    [Test]
    public async Task Each_part_is_keyed_by_its_message_and_its_position_or_call_id() {
        var events = E(Assistant("""
            {"type":"text","text":""},
            {"type":"reasoning","text":"think"},
            {"type":"text","text":""},
            {"type":"text","text":"done"},
            {"type":"tool","id":"call_1","name":"read","state":{"status":"completed","input":{"path":"a"},"content":[{"type":"text","text":"x"}]}}
            """));

        await Assert.That(events.Select(e => e.EventId)).IsEquivalentTo([
            TranscriptIds.OpenCodeContent("msg_a", 1),
            TranscriptIds.OpenCodeContent("msg_a", 3),
            TranscriptIds.OpenCodeCall("msg_a", "call_1"),
            TranscriptIds.OpenCodeCallResult("msg_a", "call_1"),
        ], CollectionOrdering.Matching);
        await Assert.That(events.Select(e => e.Timestamp.Ticks - events[0].Timestamp.Ticks)).IsEquivalentTo([0L, 1L, 2L, 3L], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("""{"status":"streaming","input":"{\"pa"}""")]
    [Arguments("""{"status":"running","input":{"path":"a"},"metadata":{}}""")]
    [Arguments(null)]
    public async Task A_tool_that_has_not_settled_emits_nothing(string? state) {
        var tool = state is null ? """{"type":"tool","id":"call_1","name":"read"}""" : $$"""{"type":"tool","id":"call_1","name":"read","state":{{state}}}""";

        await Assert.That(E(Assistant(tool))).IsEmpty();
    }

    /// <summary>A failed tool's result is its error's message, and a subagent call names the child session it runs in.</summary>
    [Test]
    [Arguments("""{"type":"tool","id":"c","name":"read","state":{"status":"error","input":{},"error":{"type":"tool.execution","message":"File not found"}}}""",
        "File not found", false, null)]
    [Arguments("""{"type":"tool","id":"c","name":"subagent","state":{"status":"completed","input":{},"content":[{"type":"text","text":"started"}],"metadata":{"sessionID":"ses_child"}}}""",
        "started", true, "ses_child")]
    [Arguments("""{"type":"tool","id":"c","name":"read","state":{"status":"completed","input":{},"content":[{"type":"text","text":"x"}],"metadata":{"sessionID":"ses_child"}}}""",
        "x", true, null)]
    public async Task A_settled_tool_carries_its_outcome(string tool, string result, bool success, string? child) {
        var events = E(Assistant(tool));

        var received = (ToolResultReceived)events[1].Payload;
        await Assert.That(received.Result).IsEqualTo(result);
        await Assert.That(SchemaExtensions.Slug(received, OpenCodeExtension.Slug)!.Fields[OpenCodeExtension.Success].BoolValue).IsEqualTo(success);
        var slug = SchemaExtensions.Slug(events[0].Payload, OpenCodeExtension.Slug);
        await Assert.That(slug?.Fields[OpenCodeExtension.AgentId].StringValue).IsEqualTo(child);
    }

    /// <summary>A command the user ran is a shell call and its result once it ends, an error unless it exited 0.</summary>
    [Test]
    [Arguments("exited", "0", "completed")]
    [Arguments("exited", "3", "error")]
    [Arguments("timeout", "null", "error")]
    [Arguments("killed", "null", "error")]
    public async Task An_ended_shell_message_is_a_shell_call_and_its_result(string status, string exit, string outcome) {
        var events = E($$$"""{"id":"msg_s","type":"shell","time":{"created":1795148275800,"completed":1795148275820},"shellID":"sh_1","command":"make","status":"{{{status}}}","exit":{{{exit}}},"output":{"output":"out","cursor":3,"size":3,"truncated":false}}""");

        var call = ((AssistantToolCallsGenerated)events[0].Payload).ToolCalls.Single();
        await Assert.That(call.ToolName).IsEqualTo("shell");
        await Assert.That(call.ToolKind).IsEqualTo(AcpToolKind.Execute);
        await Assert.That(call.Arguments.Fields["command"].StringValue).IsEqualTo("make");
        await Assert.That(events.Select(e => e.EventId)).IsEquivalentTo([TranscriptIds.OpenCodeCall("msg_s", "sh_1"), TranscriptIds.OpenCodeCallResult("msg_s", "sh_1")], CollectionOrdering.Matching);
        var result = (ToolResultReceived)events[1].Payload;
        await Assert.That(result.Result).IsEqualTo("out");
        await Assert.That(SchemaExtensions.Slug(result, OpenCodeExtension.Slug)!.Fields[OpenCodeExtension.Status].StringValue).IsEqualTo(outcome);
    }

    [Test]
    [Arguments("""{"id":"msg_s","type":"shell","time":{"created":1},"shellID":"sh_1","command":"make","status":"running"}""")]
    [Arguments("""{"id":"msg_i","type":"idle","time":{"created":1},"outcome":"succeeded"}""")]
    [Arguments("""{"id":"msg_y","type":"synthetic","time":{"created":1},"text":"<subagent>done</subagent>"}""")]
    [Arguments("""{"id":"msg_c","type":"compaction","time":{"created":1},"status":"completed","reason":"auto","summary":"s"}""")]
    [Arguments("""{"id":"msg_m","type":"model-switched","time":{"created":1},"model":{"id":"m"}}""")]
    [Arguments("""{"type":"user","time":{"created":1},"text":"no id"}""")]
    [Arguments("""{"id":"msg_u","type":"user","time":{"created":1},"text":""}""")]
    public async Task A_message_with_no_conversation_emits_nothing(string line) {
        var result = P(line);

        await Assert.That(result.Rejected).IsNull();
        await Assert.That(result.Events).IsEmpty();
    }

    [Test]
    public async Task A_user_message_is_one_event_on_its_message_id() {
        var events = E("""{"id":"msg_u","type":"user","time":{"created":1795148345014},"text":"hello","files":[]}""");

        await Assert.That(((UserMessageReceived)events.Single().Payload).Content).IsEqualTo("hello");
        await Assert.That(events.Single().EventId).IsEqualTo(TranscriptIds.OpenCode("msg_u"));
        await Assert.That(events.Single().Timestamp).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1795148345014));
    }

    [Test]
    public async Task The_messages_usage_rides_on_its_first_event() {
        var events = E("""{"id":"msg_a","type":"assistant","time":{"created":1},"model":{"id":"big-pickle","providerID":"opencode"},"tokens":{"input":10,"output":2,"reasoning":1,"cache":{"read":4,"write":3}},"content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]}""");

        await Assert.That(events[0].Usage!.TotalTokens).IsEqualTo(19);
        await Assert.That(events[0].Usage!.Model).IsEqualTo("big-pickle");
        await Assert.That(events[0].CacheCreationTokens).IsEqualTo(3);
        await Assert.That(events[1].Usage).IsNull();
    }

    [Test]
    public async Task A_creation_time_out_of_range_is_rejected() {
        await Assert.That(P("""{"id":"msg_u","type":"user","time":{"created":999999999999999},"text":"hi"}""").Rejected).IsNotNull();
    }
}
