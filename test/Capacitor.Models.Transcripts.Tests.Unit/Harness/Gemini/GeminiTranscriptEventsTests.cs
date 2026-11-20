using System.Globalization;
using Capacitor.Models.Transcripts.Harness.Gemini;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;
using TUnit.Assertions.Enums;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Gemini;

public class GeminiTranscriptEventsTests {
    static readonly DateTimeOffset Received = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    static TranscriptContext NewContext() => GeminiTranscriptEvents.Instance.CreateContext("sess", null);

    static ProjectionResult P(string line, TranscriptContext? context = null) =>
        GeminiTranscriptEvents.Instance.Project(line, 1, Received, context ?? NewContext());

    static IReadOnlyList<CanonicalEvent> E(string line, TranscriptContext? context = null) => P(line, context).Events;

    static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    const string Snapshot = """{"id":"a3b19e5f-275a-407f-891f-9b3661d7e79b","timestamp":"2026-06-17T14:10:12.305Z","type":"gemini","content":"","thoughts":[{"subject":"Listing Files","description":"Confirming the files exist.","timestamp":"2026-06-17T14:10:11.987Z"}],"tokens":{"input":9042,"output":78,"cached":0,"thoughts":132,"tool":0,"total":9252},"model":"gemini-3-flash-preview"}""";

    const string WholeTurn = """{"id":"a3b19e5f-275a-407f-891f-9b3661d7e79b","timestamp":"2026-06-17T14:10:12.305Z","type":"gemini","content":"","thoughts":[{"subject":"Listing Files","description":"Confirming the files exist.","timestamp":"2026-06-17T14:10:11.987Z"}],"tokens":{"input":9042,"output":78,"cached":0,"thoughts":132,"tool":0,"total":9252},"model":"gemini-3-flash-preview","toolCalls":[{"id":"list_directory__1","name":"list_directory","args":{"dir_path":"."},"result":[{"functionResponse":{"id":"list_directory__1","name":"list_directory","response":{"output":"Directory listing for /tmp/proj:\napp.py"}}}],"status":"success","displayName":"ReadFolder","description":"."}]}""";

    static readonly Guid TurnRecord = Guid.Parse("a3b19e5f-275a-407f-891f-9b3661d7e79b");

    [Test]
    public async Task The_registry_resolves_gemini_ignoring_case() {
        await Assert.That(TranscriptProjection.For("gemini")).IsSameReferenceAs(GeminiTranscriptEvents.Instance);
        await Assert.That(TranscriptProjection.For("Gemini")).IsSameReferenceAs(GeminiTranscriptEvents.Instance);
    }

    [Test]
    [Arguments("""{"sessionId":"11111111-1111-1111-1111-111111111111","projectHash":"3831443565b4","startTime":"2026-06-17T14:10:03.447Z","lastUpdated":"2026-06-17T14:10:03.447Z","kind":"main"}""")]
    [Arguments("""{"$set":{"lastUpdated":"2026-06-17T14:10:04.178Z"}}""")]
    [Arguments("""{"$set":{"messages":[{"id":"x","timestamp":"t","type":"user","content":[{"text":"hello"}]}],"lastUpdated":"t"}}""")]
    [Arguments("""{"$set":null,"id":"y","type":"user","content":"a $set of any kind marks a patch"}""")]
    [Arguments("""{"id":"n1","timestamp":"2026-06-17T14:10:00Z","type":"some_future_record","content":"anything"}""")]
    [Arguments("""{"id":"n2","type":7,"content":"a type that is not a string"}""")]
    public async Task Headers_set_patches_and_unknown_records_emit_nothing_and_are_not_rejected(string line) {
        var result = P(line);
        await Assert.That(result.Events).IsEmpty();
        await Assert.That(result.Rejected).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("{not json")]
    [Arguments("[1,2]")]
    [Arguments("\"a string\"")]
    public async Task A_line_that_is_not_a_json_object_is_rejected(string line) {
        await Assert.That(P(line).Rejected).IsNotNull();
    }

    [Test]
    public async Task A_prompt_is_a_user_message_on_the_records_own_id() {
        var e = E("""{"id":"f098a737-cee1-4415-83d8-d0da04fcda97","timestamp":"2026-06-17T14:10:04.178Z","type":"user","content":[{"text":"In one short sentence, what does a todo app do?"}]}""");

        await Assert.That(e.Count).IsEqualTo(1);
        await Assert.That(e[0].EventId).IsEqualTo(Guid.Parse("f098a737-cee1-4415-83d8-d0da04fcda97"));
        await Assert.That(e[0].RecordTimestamp).IsEqualTo("2026-06-17T14:10:04.178Z");
        await Assert.That(e[0].Timestamp).IsEqualTo(At("2026-06-17T14:10:04.178Z"));
        var message = (UserMessageReceived)e[0].Payload;
        await Assert.That(message.Content).IsEqualTo("In one short sentence, what does a todo app do?");
        await Assert.That(message.Timestamp).IsEqualTo(Timestamp.FromDateTimeOffset(e[0].Timestamp));
    }

    [Test]
    public async Task A_bare_string_prompt_is_its_own_text_and_text_parts_join_with_a_newline() {
        await Assert.That(((UserMessageReceived)E("""{"id":"u1","type":"user","content":"plain"}""")[0].Payload).Content).IsEqualTo("plain");
        await Assert.That(((UserMessageReceived)E("""{"id":"u2","type":"user","content":[{"text":"one"},{"text":""},{"inlineData":{}},{"text":"two"}]}""")[0].Payload).Content)
            .IsEqualTo("one\ntwo");
    }

    [Test]
    [Arguments("""{"id":"f3183e97-1757-4352-bb29-91f996b7afd5","timestamp":"2026-06-17T14:10:12.351Z","type":"user","content":[{"functionResponse":{"id":"read_file__2","name":"read_file","response":{"output":"file contents"}}}]}""")]
    [Arguments("""{"id":"d04923d3","timestamp":"2026-06-17T14:10:03.447Z","type":"user","content":[{"text":"<session_context>\nThis is the Gemini CLI.\n</session_context>"}]}""")]
    [Arguments("""{"id":"u3","type":"user","content":[]}""")]
    [Arguments("""{"id":"u4","type":"user","content":{"text":"an object is not a part list"}}""")]
    public async Task A_function_response_echo_the_session_bootstrap_and_an_empty_prompt_emit_nothing(string line) {
        await Assert.That(E(line)).IsEmpty();
    }

    [Test]
    public async Task Text_beside_function_responses_is_kept_and_a_part_carrying_both_is_an_echo() {
        var e = E("""{"id":"u5","type":"user","content":[{"functionResponse":{"response":{"output":"x"}},"text":"hidden"},{"text":"follow-up"}]}""");
        await Assert.That(((UserMessageReceived)e[0].Payload).Content).IsEqualTo("follow-up");
    }

    [Test]
    public async Task Text_carries_usage_with_input_net_of_cached_tokens() {
        var e = E("""{"id":"b4a011e3-14d9-4acd-9fc3-5edfa75dee59","timestamp":"2026-06-17T14:10:22.248Z","type":"gemini","content":"done","thoughts":[],"tokens":{"input":9591,"output":149,"cached":7521,"thoughts":191,"tool":0,"total":9931},"model":"gemini-3-flash-preview"}""");

        await Assert.That(e.Count).IsEqualTo(1);
        await Assert.That(e[0].EventType).IsEqualTo(CanonicalEventTypes.AssistantTextGenerated);
        await Assert.That(e[0].EventId).IsEqualTo(TranscriptIds.Sibling(Guid.Parse("b4a011e3-14d9-4acd-9fc3-5edfa75dee59"), "text"));
        await Assert.That(((AssistantTextGenerated)e[0].Payload).Content).IsEqualTo("done");

        var usage = e[0].Usage!;
        await Assert.That(usage.InputTokens).IsEqualTo(2070L);
        await Assert.That(usage.OutputTokens).IsEqualTo(149L);
        await Assert.That(usage.CachedInputTokens).IsEqualTo(7521L);
        await Assert.That(usage.TotalTokens).IsEqualTo(9740L);
        await Assert.That(usage.ReasoningTokens).IsEqualTo(191L);
        await Assert.That(usage.Model).IsEqualTo("gemini-3-flash-preview");
        await Assert.That(usage.AdditionalCounts).IsNull();
        await Assert.That(e[0].CacheCreationTokens).IsNull();
    }

    [Test]
    public async Task Thinking_a_call_and_its_inline_result_are_separate_events_a_tick_apart_with_usage_on_the_first() {
        var e = E(WholeTurn);

        await Assert.That(e.Select(x => x.EventType)).IsEquivalentTo([
            CanonicalEventTypes.AssistantThinkingGenerated, CanonicalEventTypes.AssistantToolCallsGenerated, CanonicalEventTypes.ToolResultReceived,
        ], CollectionOrdering.Matching);
        await Assert.That(e.Select(x => x.EventId)).IsEquivalentTo([
            TranscriptIds.Sibling(TurnRecord, "thinking"), TranscriptIds.Sibling(TurnRecord, "tool:list_directory__1"), TranscriptIds.Sibling(TurnRecord, "result:list_directory__1"),
        ], CollectionOrdering.Matching);

        var at = At("2026-06-17T14:10:12.305Z");
        await Assert.That(e.Select(x => x.Timestamp)).IsEquivalentTo([at, at.AddTicks(1), at.AddTicks(2)], CollectionOrdering.Matching);
        await Assert.That(e.All(x => x.RecordTimestamp == "2026-06-17T14:10:12.305Z")).IsTrue();
        await Assert.That(e[0].Usage).IsNotNull();
        await Assert.That(e[1].Usage).IsNull();
        await Assert.That(e[2].Usage).IsNull();

        await Assert.That(((AssistantThinkingGenerated)e[0].Payload).Content).IsEqualTo("**Listing Files**\nConfirming the files exist.");

        var calls = (AssistantToolCallsGenerated)e[1].Payload;
        await Assert.That(calls.Timestamp).IsEqualTo(Timestamp.FromDateTimeOffset(at.AddTicks(1)));
        var call = calls.ToolCalls.Single();
        await Assert.That(call.CallId).IsEqualTo("list_directory__1");
        await Assert.That(call.ToolName).IsEqualTo("list_directory");
        await Assert.That(call.ToolKind).IsEqualTo(AcpToolKind.Search);
        await Assert.That(call.Arguments.Fields["dir_path"].StringValue).IsEqualTo(".");

        var result = (ToolResultReceived)e[2].Payload;
        await Assert.That(result.CallId).IsEqualTo("list_directory__1");
        await Assert.That(result.Result).IsEqualTo("Directory listing for /tmp/proj:\napp.py");
        var status = SchemaExtensions.Slug(result, GeminiExtension.Slug)!;
        await Assert.That(SchemaExtensions.Flag(status, GeminiExtension.Success)).IsTrue();
        await Assert.That(SchemaExtensions.Text(status, GeminiExtension.Status)).IsEqualTo("success");
    }

    [Test]
    public async Task Thoughts_join_with_a_blank_line_and_keep_a_lone_subject_or_description() {
        var e = E("""{"id":"t1","type":"gemini","thoughts":[{"subject":"A","description":"a"},{"description":"b only"},{"subject":"C only"},{"subject":"","description":""}]}""");
        await Assert.That(((AssistantThinkingGenerated)e[0].Payload).Content).IsEqualTo("**A**\na\n\nb only\n\nC only");
    }

    [Test]
    public async Task A_status_other_than_success_marks_the_result_failed() {
        var e = E("""{"id":"f1","timestamp":"2026-06-17T14:10:12Z","type":"gemini","content":"","toolCalls":[{"id":"run_shell__1","name":"run_shell_command","args":{"command":"nope"},"result":[{"functionResponse":{"id":"run_shell__1","name":"run_shell_command","response":{"output":"command not found"}}}],"status":"error"}]}""");

        var status = SchemaExtensions.Slug(e.Single(x => x.EventType == CanonicalEventTypes.ToolResultReceived).Payload, GeminiExtension.Slug)!;
        await Assert.That(status.Fields[GeminiExtension.Success].BoolValue).IsFalse();
        await Assert.That(SchemaExtensions.Text(status, GeminiExtension.Status)).IsEqualTo("error");
    }

    [Test]
    public async Task A_call_without_status_hints_or_output_carries_no_slug_and_no_result_text() {
        var e = E("""{"id":"f2","type":"gemini","toolCalls":[{"id":"c1","name":"read_file"}]}""");

        var calls = (AssistantToolCallsGenerated)e[0].Payload;
        await Assert.That(calls.Extensions.ContainsKey(GeminiExtension.Slug)).IsFalse();
        await Assert.That(calls.ToolCalls[0].Arguments).IsNull();
        var result = (ToolResultReceived)e[1].Payload;
        await Assert.That(result.Extensions.ContainsKey(GeminiExtension.Slug)).IsFalse();
        await Assert.That(result.HasResult).IsFalse();
    }

    [Test]
    public async Task A_result_item_of_another_shape_is_kept_as_its_raw_json() {
        var e = E("""{"id":"f3","type":"gemini","toolCalls":[{"id":"c1","name":"read_file","result":[{"functionResponse":{"response":{"error":"denied"}}},{"text":"x"}]}]}""");
        await Assert.That(((ToolResultReceived)e[1].Payload).Result).IsEqualTo("""{"error":"denied"}""" + "\n" + """{"text":"x"}""");
    }

    [Test]
    public async Task Display_hints_ride_the_call_with_every_field_and_the_tool_prompt_rides_additional_counts() {
        var e = E("""{"id":"g9","timestamp":"2026-06-17T14:10:12Z","type":"gemini","content":"","tokens":{"input":100,"output":10,"cached":0,"thoughts":5,"tool":7,"total":122},"model":"gemini-3-flash-preview","toolCalls":[{"id":"list_directory__1","name":"list_directory","args":{"dir_path":"."},"status":"success","displayName":"ReadFolder","description":"List the current directory"}]}""");

        var hints = SchemaExtensions.Slug(e[0].Payload, GeminiExtension.Slug)!;
        await Assert.That(SchemaExtensions.Text(hints, GeminiExtension.DisplayName)).IsEqualTo("ReadFolder");
        await Assert.That(SchemaExtensions.Text(hints, GeminiExtension.Description)).IsEqualTo("List the current directory");
        await Assert.That(hints.Fields[GeminiExtension.AgentId].KindCase).IsEqualTo(Value.KindOneofCase.NullValue);

        var usage = e[0].Usage!;
        await Assert.That(usage.ReasoningTokens).IsEqualTo(5L);
        await Assert.That(usage.AdditionalCounts.Fields["tool_use_prompt_tokens"].NumberValue).IsEqualTo(7d);
    }

    [Test]
    public async Task An_invoke_agent_call_names_its_subagent_by_the_dashless_id() {
        var e = E("""{"id":"m1","timestamp":"2026-06-22T14:31:00Z","type":"gemini","content":"","toolCalls":[{"id":"invoke_agent__6wan1yb6","name":"invoke_agent","args":{"agent_name":"codebase_investigator"},"agentId":"57d9b498-2705-4af5-b060-ebaba4878c96","status":"success"}]}""");

        var calls = (AssistantToolCallsGenerated)e[0].Payload;
        await Assert.That(calls.ToolCalls[0].ToolKind).IsEqualTo(AcpToolKind.Other);
        await Assert.That(SchemaExtensions.Text(SchemaExtensions.Slug(calls, GeminiExtension.Slug), GeminiExtension.AgentId)).IsEqualTo("57d9b49827054af5b060ebaba4878c96");
    }

    [Test]
    public async Task Ask_user_is_named_as_a_claude_elicitation() {
        var e = E("""{"id":"q1","type":"gemini","toolCalls":[{"id":"ask_user__1","name":"ask_user","args":{"questions":[{"question":"Which?","header":"Pick","options":[{"label":"A","description":"a"}]}]},"displayName":"Ask User"}]}""");

        var call = ((AssistantToolCallsGenerated)e[0].Payload).ToolCalls[0];
        await Assert.That(call.ToolName).IsEqualTo("AskUserQuestion");
        await Assert.That(call.ToolKind).IsEqualTo(AcpToolKind.Other);
        await Assert.That(SchemaExtensions.Text(SchemaExtensions.Slug(e[0].Payload, GeminiExtension.Slug), GeminiExtension.DisplayName)).IsEqualTo("Ask User");
    }

    [Test]
    public async Task A_call_without_an_id_or_a_name_is_skipped() {
        const string line   = """{"id":"s1","type":"gemini","toolCalls":[{"name":"read_file"},{"id":"c2"},{"id":"c3","name":"glob"}]}""";
        var          record = TranscriptIds.GeminiRecord("s1", line);

        await Assert.That(E(line).Select(x => x.EventId)).IsEquivalentTo(
            [TranscriptIds.Sibling(record, "tool:c3"), TranscriptIds.Sibling(record, "result:c3")], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("read_file", AcpToolKind.Read)]
    [Arguments("read_many_files", AcpToolKind.Read)]
    [Arguments("write_file", AcpToolKind.Edit)]
    [Arguments("replace", AcpToolKind.Edit)]
    [Arguments("run_shell_command", AcpToolKind.Execute)]
    [Arguments("list_directory", AcpToolKind.Search)]
    [Arguments("glob", AcpToolKind.Search)]
    [Arguments("grep_search", AcpToolKind.Search)]
    [Arguments("search_file_content", AcpToolKind.Search)]
    [Arguments("web_fetch", AcpToolKind.Fetch)]
    [Arguments("google_web_search", AcpToolKind.Fetch)]
    [Arguments("save_memory", AcpToolKind.Other)]
    [Arguments("write_todos", AcpToolKind.Other)]
    [Arguments("mcp_server_tool", AcpToolKind.Other)]
    public async Task Each_built_in_tool_names_its_kind(string toolName, string kind) {
        await Assert.That(GeminiToolKinds.Of(toolName)).IsEqualTo(kind);
    }

    [Test]
    public async Task A_record_without_a_timestamp_takes_the_receive_time_and_records_none() {
        var e = E("""{"id":"r1","type":"gemini","content":"hi","toolCalls":[{"id":"c1","name":"glob"}]}""");

        await Assert.That(e.Select(x => x.Timestamp)).IsEquivalentTo([Received, Received.AddTicks(1), Received.AddTicks(2)], CollectionOrdering.Matching);
        await Assert.That(e.All(x => x.RecordTimestamp is null)).IsTrue();
        await Assert.That(((AssistantTextGenerated)e[0].Payload).Timestamp).IsEqualTo(Timestamp.FromDateTimeOffset(Received));
    }

    [Test]
    public async Task An_unparseable_timestamp_takes_the_receive_time_and_keeps_the_raw_string() {
        var e = E("""{"id":"r2","timestamp":"t","type":"user","content":"hi"}""");
        await Assert.That(e[0].Timestamp).IsEqualTo(Received);
        await Assert.That(e[0].RecordTimestamp).IsEqualTo("t");
    }

    [Test]
    public async Task A_record_whose_id_is_not_a_guid_takes_the_hash_of_its_line() {
        const string line = """{"id":"n1","type":"user","content":"hi"}""";
        await Assert.That(E(line)[0].EventId).IsEqualTo(TranscriptIds.GeminiRecord(null, line));
        await Assert.That(E("""{"type":"user","content":"no id"}""")[0].EventId).IsEqualTo(TranscriptIds.GeminiRecord(null, """{"type":"user","content":"no id"}"""));
    }

    [Test]
    [Arguments("""{"id":"x1","type":"gemini","content":"a","tokens":{"thoughts":5}}""", false)]
    [Arguments("""{"id":"x2","type":"gemini","content":"a","model":"m"}""", false)]
    [Arguments("""{"id":"x3","type":"gemini","content":"a","model":"m","tokens":{"tool":4}}""", false)]
    [Arguments("""{"id":"x4","type":"gemini","content":"a","model":"m","tokens":{"thoughts":5}}""", true)]
    [Arguments("""{"id":"x5","type":"gemini","content":"a","tokens":{"cached":3}}""", false)]
    [Arguments("""{"id":"x6","type":"gemini","content":"a","tokens":{"output":2}}""", true)]
    public async Task Usage_needs_input_output_or_model_and_a_count_to_hold(string line, bool hasUsage) {
        await Assert.That(E(line)[0].Usage is not null).IsEqualTo(hasUsage);
    }

    [Test]
    public async Task A_model_with_only_thoughts_has_reasoning_and_no_total() {
        var usage = E("""{"id":"x4","type":"gemini","content":"a","model":"m","tokens":{"thoughts":5}}""")[0].Usage!;
        await Assert.That(usage.HasTotalTokens).IsFalse();
        await Assert.That(usage.ReasoningTokens).IsEqualTo(5L);
        await Assert.That(usage.Model).IsEqualTo("m");
    }

    // ── what the caller has stored ────────────────────────────────────────────────────────────

    [Test]
    public async Task A_snapshots_repeat_leaves_out_the_stored_parts_and_carries_no_usage() {
        var ctx      = NewContext();
        var snapshot = E(Snapshot, ctx);
        foreach (var e in snapshot) ctx.MarkPersisted(e.EventId);

        var whole = E(WholeTurn, ctx);

        await Assert.That(snapshot.Single().Usage).IsNotNull();
        await Assert.That(whole.Select(x => x.EventType)).IsEquivalentTo([
            CanonicalEventTypes.AssistantToolCallsGenerated, CanonicalEventTypes.ToolResultReceived,
        ], CollectionOrdering.Matching);
        await Assert.That(whole.All(x => x.Usage is null)).IsTrue();
        await Assert.That(whole[0].Timestamp).IsEqualTo(At("2026-06-17T14:10:12.305Z").AddTicks(1));
    }

    [Test]
    public async Task Before_the_snapshot_is_stored_its_repeat_returns_every_part_with_usage_on_the_first() {
        var ctx      = NewContext();
        var snapshot = E(Snapshot, ctx);
        var whole    = E(WholeTurn, ctx);

        await Assert.That(whole.Count).IsEqualTo(3);
        await Assert.That(whole[0].EventId).IsEqualTo(snapshot[0].EventId);
        await Assert.That(whole[0].Usage).IsNotNull();
    }

    [Test]
    public async Task A_snapshot_that_emitted_nothing_leaves_usage_to_the_whole_turns_first_part() {
        var ctx = NewContext();
        await Assert.That(E("""{"id":"3f05a8fe-b798-4e12-9426-70c4699f1c20","type":"gemini","content":"","thoughts":[],"tokens":{"input":3106,"output":82},"model":"m"}""", ctx)).IsEmpty();

        var whole = E("""{"id":"3f05a8fe-b798-4e12-9426-70c4699f1c20","type":"gemini","content":"","thoughts":[],"tokens":{"input":3106,"output":82},"model":"m","toolCalls":[{"id":"c1","name":"glob","status":"success"}]}""", ctx);
        await Assert.That(whole[0].EventType).IsEqualTo(CanonicalEventTypes.AssistantToolCallsGenerated);
        await Assert.That(whole[0].Usage).IsNotNull();
    }

    [Test]
    public async Task A_part_returned_but_never_stored_is_returned_again_without_usage_once_the_first_is_stored() {
        var ctx   = NewContext();
        var first = E(WholeTurn, ctx);
        ctx.MarkPersisted(first[0].EventId);

        var retry = E(WholeTurn, ctx);

        await Assert.That(retry.Select(x => x.EventId)).IsEquivalentTo(first.Skip(1).Select(x => x.EventId), CollectionOrdering.Matching);
        await Assert.That(retry.All(x => x.Usage is null)).IsTrue();
    }

    [Test]
    public async Task A_record_whose_parts_are_all_stored_returns_nothing_across_batches() {
        var ctx = NewContext();
        foreach (var e in E(WholeTurn, ctx)) ctx.MarkPersisted(e.EventId);
        ctx.BeginBatch();

        var result = P(WholeTurn, ctx);
        await Assert.That(result.Events).IsEmpty();
        await Assert.That(result.Rejected).IsNull();
    }

    [Test]
    public async Task A_context_reads_only_its_own_marks() {
        var marked = NewContext();
        foreach (var e in E(WholeTurn, marked)) marked.MarkPersisted(e.EventId);

        await Assert.That(E(WholeTurn, NewContext()).Count).IsEqualTo(3);
    }
}
