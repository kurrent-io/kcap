using Capacitor.Models.Transcripts.Harness.OpenCode;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.OpenCode;

public class OpenCodeTranscriptEventsTests {
    static readonly DateTimeOffset Received = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Created  = DateTimeOffset.FromUnixTimeMilliseconds(1782242800000);

    static ProjectionResult P(string line) =>
        OpenCodeTranscriptEvents.Instance.Project(line, 1, Received, OpenCodeTranscriptEvents.Instance.CreateContext("ses_1", null));

    static IReadOnlyList<CanonicalEvent> E(string line) => P(line).Events;

    static Struct? Slug(object payload) => SchemaExtensions.Slug(payload, OpenCodeExtension.Slug);

    [Test]
    [Arguments("")]
    [Arguments("not json at all")]
    [Arguments("[]")]
    public async Task A_line_that_is_not_a_json_object_is_rejected(string line) {
        var result = P(line);
        await Assert.That(result.Rejected).IsNotNull();
        await Assert.That(result.Events.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("""{"type":"session_title","title":"Fix the build","time":1}""")]
    [Arguments("""{"info":{"id":"ses_x","title":"a session blob with no role"}}""")]
    [Arguments("""{"info":{"role":"system"},"parts":[{"type":"text","text":"sys","id":"prt_s"}]}""")]
    [Arguments("""{"info":{"role":"assistant","id":"msg_x"},"parts":[{"type":"step-start","id":"prt_a"},{"type":"step-finish","id":"prt_b"}]}""")]
    [Arguments("""{"info":{"role":"assistant","id":"msg_x"},"parts":[{"type":"text","text":"no part id"}]}""")]
    [Arguments("""{"info":{"role":"user","id":"msg_x"},"parts":[{"type":"file","id":"prt_f","url":"file:///a.png"}]}""")]
    public async Task A_record_with_no_conversation_emits_nothing(string line) {
        var result = P(line);
        await Assert.That(result.Rejected).IsNull();
        await Assert.That(result.Events.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_user_message_is_one_event_on_its_message_id_at_its_creation_time() {
        var events = E("""{"info":{"role":"user","time":{"created":1782242800000},"id":"msg_u1"},"parts":[{"type":"text","text":"Read note.txt","id":"prt_u1"}]}""");

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(((UserMessageReceived)events[0].Payload).Content).IsEqualTo("Read note.txt");
        await Assert.That(events[0].EventId).IsEqualTo(TranscriptIds.OpenCode("msg_u1"));
        await Assert.That(events[0].Timestamp).IsEqualTo(Created);
        await Assert.That(events[0].RecordTimestamp).IsEqualTo(Created.ToString("O"));
        await Assert.That(events[0].Usage).IsNull();
    }

    [Test]
    public async Task A_user_message_without_an_id_takes_its_id_from_its_text() {
        var events = E("""{"info":{"role":"user","time":{"created":1782242800000}},"parts":[{"type":"text","text":"hello","id":"prt_u1"}]}""");
        await Assert.That(events[0].EventId).IsEqualTo(TranscriptIds.OpenCode("hello"));
    }

    [Test]
    public async Task Synthetic_and_ignored_text_is_left_out_and_the_rest_is_joined_by_newlines() {
        var events = E("""
            {"info":{"role":"user","id":"msg_u","time":{"created":1782242800000}},"parts":[
              {"type":"text","text":"first","id":"p1"},
              {"type":"text","text":"injected","synthetic":true,"id":"p2"},
              {"type":"text","text":"elided","ignored":true,"id":"p3"},
              {"type":"text","text":"","id":"p4"},
              {"type":"text","text":"second","synthetic":false,"id":"p5"}]}
            """);

        await Assert.That(((UserMessageReceived)events[0].Payload).Content).IsEqualTo("first\nsecond");
    }

    [Test]
    public async Task A_user_message_of_only_hidden_text_emits_nothing() {
        await Assert.That(E("""{"info":{"role":"user","id":"msg_u"},"parts":[{"type":"text","text":"x","synthetic":true,"id":"p"}]}""").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Assistant_parts_emit_in_order_a_tick_apart_with_usage_on_the_first_only() {
        var events = E("""
            {"info":{"role":"assistant","id":"msg_a","modelID":"north-mini-code-free","tokens":{"input":10,"output":2,"reasoning":5,"cache":{"read":0,"write":0}},"time":{"created":1782242800000}},"parts":[
              {"type":"step-start","id":"prt_s"},
              {"type":"reasoning","text":"Thinking about it","id":"prt_r"},
              {"type":"reasoning","text":"hidden","synthetic":true,"id":"prt_h"},
              {"type":"text","text":"Answer","id":"prt_t"},
              {"type":"step-finish","id":"prt_f"}]}
            """);

        await Assert.That(events.Select(e => e.EventType)).IsEquivalentTo(["AssistantThinkingGenerated", "AssistantTextGenerated"]);
        await Assert.That(events.Select(e => e.EventId)).IsEquivalentTo([TranscriptIds.OpenCode("prt_r"), TranscriptIds.OpenCode("prt_t")]);
        await Assert.That(events[0].Timestamp).IsEqualTo(Created);
        await Assert.That(events[1].Timestamp).IsEqualTo(Created.AddTicks(1));
        await Assert.That(((AssistantTextGenerated)events[1].Payload).Timestamp.ToDateTimeOffset()).IsEqualTo(Created.AddTicks(1));
        await Assert.That(events[1].RecordTimestamp).IsEqualTo(Created.ToString("O"));
        await Assert.That(events[0].Usage).IsNotNull();
        await Assert.That(events[1].Usage).IsNull();
        await Assert.That(((AssistantThinkingGenerated)events[0].Payload).HasEncrypted).IsFalse();
    }

    [Test]
    public async Task Usage_totals_the_four_additive_counts_and_carries_the_model_id_verbatim() {
        var events = E("""{"info":{"role":"assistant","id":"msg_a","modelID":"command-a-03-2025","providerID":"cohere","tokens":{"total":9999,"input":1200,"output":34,"reasoning":50,"cache":{"read":100,"write":20}},"time":{"created":1782242800000}},"parts":[{"type":"text","text":"Done.","id":"prt_t"}]}""");

        var usage = events[0].Usage!;
        await Assert.That(usage.InputTokens).IsEqualTo(1200L);
        await Assert.That(usage.OutputTokens).IsEqualTo(34L);
        await Assert.That(usage.CachedInputTokens).IsEqualTo(100L);
        await Assert.That(usage.ReasoningTokens).IsEqualTo(50L);
        await Assert.That(usage.TotalTokens).IsEqualTo(1354L);
        await Assert.That(usage.Model).IsEqualTo("command-a-03-2025");
        await Assert.That(events[0].CacheCreationTokens).IsEqualTo(20L);
    }

    [Test]
    [Arguments("""{"modelID":"m"}""", false)]
    [Arguments("""{"tokens":{"reasoning":5}}""", false)]
    [Arguments("""{"tokens":{"cache":{"read":3}}}""", false)]
    [Arguments("""{"modelID":"m","tokens":{"reasoning":5}}""", true)]
    [Arguments("""{"tokens":{"output":1}}""", true)]
    public async Task Usage_is_stamped_only_when_the_message_reports_counts_the_server_would_write(string fields, bool stamped) {
        var info   = """{"role":"assistant","id":"msg_a","time":{"created":1782242800000},""" + fields[1..];
        var events = E($$"""{"info":{{info}},"parts":[{"type":"text","text":"x","id":"prt_t"}]}""");

        await Assert.That(events[0].Usage is not null).IsEqualTo(stamped);
    }

    [Test]
    public async Task A_model_with_only_a_reasoning_count_has_no_total() {
        var usage = E("""{"info":{"role":"assistant","id":"msg_a","modelID":"m","tokens":{"reasoning":5}},"parts":[{"type":"text","text":"x","id":"prt_t"}]}""")[0].Usage!;
        await Assert.That(usage.HasTotalTokens).IsFalse();
        await Assert.That(usage.ReasoningTokens).IsEqualTo(5L);
    }

    [Test]
    public async Task A_completed_tool_part_emits_its_call_and_its_result_on_the_part_id() {
        var events = E("""{"info":{"role":"assistant","id":"msg_a","time":{"created":1782242800000}},"parts":[{"type":"tool","callID":"call_1","tool":"read","state":{"status":"completed","input":{"filePath":"note.txt"},"output":"file body","title":"note.txt"},"id":"prt_tool1"}]}""");

        await Assert.That(events.Count).IsEqualTo(2);
        var call = ((AssistantToolCallsGenerated)events[0].Payload).ToolCalls.Single();
        await Assert.That(call.CallId).IsEqualTo("call_1");
        await Assert.That(call.ToolName).IsEqualTo("read");
        await Assert.That(call.ToolKind).IsEqualTo(AcpToolKind.Read);
        await Assert.That(call.Arguments.Fields["filePath"].StringValue).IsEqualTo("note.txt");
        await Assert.That(events[0].EventId).IsEqualTo(TranscriptIds.OpenCodeToolCall("prt_tool1"));

        var result = (ToolResultReceived)events[1].Payload;
        await Assert.That(result.CallId).IsEqualTo("call_1");
        await Assert.That(result.Result).IsEqualTo("file body");
        await Assert.That(events[1].EventId).IsEqualTo(TranscriptIds.OpenCodeToolResult("prt_tool1"));
        await Assert.That(events[1].Timestamp).IsEqualTo(Created.AddTicks(1));
        await Assert.That(SchemaExtensions.Flag(Slug(result), OpenCodeExtension.Success)).IsTrue();
        await Assert.That(SchemaExtensions.Text(Slug(result), OpenCodeExtension.Status)).IsEqualTo("completed");
        await Assert.That(SchemaExtensions.Text(Slug(result), OpenCodeExtension.Title)).IsEqualTo("note.txt");
    }

    [Test]
    public async Task A_failed_tool_part_carries_its_error_as_the_result_and_a_null_title_when_it_has_none() {
        var events = E("""{"info":{"role":"assistant","id":"msg_a","time":{"created":1782242800000}},"parts":[{"type":"tool","callID":"call_2","tool":"read","state":{"status":"error","input":{"filePath":"missing.txt"},"error":"ENOENT: missing.txt","output":"ignored"},"id":"prt_tool2"}]}""");

        var result = (ToolResultReceived)events.Single(e => e.EventType == "ToolResultReceived").Payload;
        var slug   = Slug(result)!;
        await Assert.That(result.Result).IsEqualTo("ENOENT: missing.txt");
        await Assert.That(slug.Fields[OpenCodeExtension.Success].BoolValue).IsFalse();
        await Assert.That(SchemaExtensions.Text(slug, OpenCodeExtension.Status)).IsEqualTo("error");
        await Assert.That(slug.Fields[OpenCodeExtension.Title].KindCase).IsEqualTo(Value.KindOneofCase.NullValue);
    }

    [Test]
    public async Task A_tool_part_without_input_or_output_leaves_the_arguments_and_the_result_unset() {
        var events = E("""{"info":{"role":"assistant","id":"msg_a"},"parts":[{"type":"tool","callID":"c","tool":"bash","state":{"status":"completed"},"id":"prt_y"}]}""");

        await Assert.That(((AssistantToolCallsGenerated)events[0].Payload).ToolCalls[0].Arguments).IsNull();
        await Assert.That(((ToolResultReceived)events[1].Payload).Result).IsEqualTo("");
    }

    [Test]
    [Arguments("pending")]
    [Arguments("running")]
    [Arguments(null)]
    public async Task A_tool_part_that_has_not_settled_emits_nothing(string? status) {
        var state = status is null ? "{}" : $$$"""{"status":"{{{status}}}","input":{}}""";
        await Assert.That(E($$"""{"info":{"role":"assistant","id":"msg_r"},"parts":[{"type":"tool","id":"prt_run","tool":"read","callID":"c1","state":{{state}}}]}""").Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_task_call_names_the_child_session_it_spawned() {
        var calls = (AssistantToolCallsGenerated)E("""{"info":{"role":"assistant","id":"msg_p1"},"parts":[{"type":"tool","id":"prt_task1","tool":"task","callID":"call_task_1","state":{"status":"completed","input":{"description":"Summarize files"},"output":"3 files.","metadata":{"parentSessionId":"ses_parent","sessionId":"ses_child_abc"}}}]}""")[0].Payload;

        await Assert.That(SchemaExtensions.Text(Slug(calls), OpenCodeExtension.AgentId)).IsEqualTo("ses_child_abc");
        await Assert.That(calls.ToolCalls[0].ToolKind).IsEqualTo(AcpToolKind.Other);
    }

    [Test]
    public async Task A_guid_child_session_id_is_written_in_its_canonical_form() {
        var calls = (AssistantToolCallsGenerated)E("""{"info":{"role":"assistant","id":"m"},"parts":[{"type":"tool","id":"p","tool":"task","callID":"c","state":{"status":"completed","metadata":{"sessionId":"0F1E2D3C-4B5A-6978-8796-A5B4C3D2E1F0"}}}]}""")[0].Payload;
        await Assert.That(SchemaExtensions.Text(Slug(calls), OpenCodeExtension.AgentId)).IsEqualTo("0f1e2d3c4b5a69788796a5b4c3d2e1f0");
    }

    [Test]
    [Arguments("task", """{"status":"completed","input":{"description":"not really a subagent"},"output":"done"}""")]
    [Arguments("task", """{"status":"completed","metadata":{"sessionId":""}}""")]
    [Arguments("read", """{"status":"completed","metadata":{"sessionId":"ses_child"}}""")]
    public async Task Only_a_task_call_with_a_child_session_carries_the_slug(string tool, string state) {
        var calls = (AssistantToolCallsGenerated)E($$"""{"info":{"role":"assistant","id":"m"},"parts":[{"type":"tool","id":"p","tool":"{{tool}}","callID":"c","state":{{state}}}]}""")[0].Payload;
        await Assert.That(Slug(calls)).IsNull();
    }

    [Test]
    public async Task The_same_line_projects_to_the_same_ids() {
        const string line = """{"info":{"role":"assistant","id":"msg_a5","time":{"created":1782242800000}},"parts":[{"type":"text","text":"hi","id":"prt_x"},{"type":"tool","callID":"c","tool":"read","state":{"status":"completed","output":"o"},"id":"prt_y"}]}""";
        await Assert.That(E(line).Select(e => e.EventId)).IsEquivalentTo(E(line).Select(e => e.EventId));
    }

    [Test]
    public async Task A_message_without_a_creation_time_takes_the_receive_time_and_no_record_timestamp() {
        var events = E("""{"info":{"role":"assistant","id":"msg_a"},"parts":[{"type":"text","text":"a","id":"p1"},{"type":"text","text":"b","id":"p2"}]}""");

        await Assert.That(events[0].Timestamp).IsEqualTo(Received);
        await Assert.That(events[1].Timestamp).IsEqualTo(Received.AddTicks(1));
        await Assert.That(events.All(e => e.RecordTimestamp is null)).IsTrue();
    }

    [Test]
    public async Task A_creation_time_out_of_range_is_rejected() {
        await Assert.That(P("""{"info":{"role":"user","id":"m","time":{"created":999999999999999999}},"parts":[{"type":"text","text":"x","id":"p"}]}""").Rejected).IsNotNull();
    }

    [Test]
    [Arguments("read", AcpToolKind.Read)]
    [Arguments("edit", AcpToolKind.Edit)]
    [Arguments("multiedit", AcpToolKind.Edit)]
    [Arguments("write", AcpToolKind.Edit)]
    [Arguments("patch", AcpToolKind.Edit)]
    [Arguments("apply_patch", AcpToolKind.Edit)]
    [Arguments("bash", AcpToolKind.Execute)]
    [Arguments("grep", AcpToolKind.Search)]
    [Arguments("glob", AcpToolKind.Search)]
    [Arguments("list", AcpToolKind.Search)]
    [Arguments("webfetch", AcpToolKind.Fetch)]
    [Arguments("websearch", AcpToolKind.Fetch)]
    [Arguments("codesearch", AcpToolKind.Fetch)]
    [Arguments("todowrite", AcpToolKind.Other)]
    [Arguments("skill", AcpToolKind.Other)]
    [Arguments("github_create_issue", AcpToolKind.Other)]
    public async Task Each_built_in_tool_maps_to_its_kind(string tool, string kind) =>
        await Assert.That(OpenCodeToolKinds.Of(tool)).IsEqualTo(kind);
}
