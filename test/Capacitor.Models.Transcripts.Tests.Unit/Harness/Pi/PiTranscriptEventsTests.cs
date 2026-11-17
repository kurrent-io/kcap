using System.Globalization;
using System.Runtime.CompilerServices;
using Capacitor.Models.Transcripts.Harness.Pi;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;
using TUnit.Assertions.Enums;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Pi;

/// <summary>The rules kcap-server's Pi normalizer applies, which the projection reproduces: what each entry type
/// and message role becomes, the ids, usage on an assistant message's first event, the <c>pi</c> slug, and the tree
/// node every event carries.</summary>
public class PiTranscriptEventsTests {
    const string Session = "11111111222233334444555555555555";

    static readonly DateTimeOffset Received = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly PiTranscriptEvents Projection = PiTranscriptEvents.Instance;

    static PiTranscriptContext Ctx(string session = Session) => (PiTranscriptContext)Projection.CreateContext(session, null);

    static ProjectionResult P(string line, TranscriptContext ctx) => Projection.Project(line, 1, Received, ctx);

    static IReadOnlyList<CanonicalEvent> E(string line, TranscriptContext? ctx = null) => P(line, ctx ?? Ctx()).Events;

    static Struct Pi(object payload) => SchemaExtensions.Slug(payload, PiExtension.Slug)!;

    static Value Field(object payload, string field) => Pi(payload).Fields[field];

    static string? Meta(CanonicalEvent e, string key) => e.VendorMetadata?.GetValueOrDefault(key);

    [Test]
    public async Task The_header_starts_the_session_keyed_by_the_session_id_it_names() {
        var e = E("""{"type":"session","version":3,"id":"11111111-2222-3333-4444-555555555555","timestamp":"2026-06-12T10:00:00.000Z","cwd":"/tmp/pi-work","parentSession":"/old/session.jsonl"}""");

        await Assert.That(e).Count().IsEqualTo(1);
        await Assert.That(e[0].EventType).IsEqualTo(CanonicalEventTypes.SessionStarted);
        await Assert.That(e[0].EventId).IsEqualTo(TranscriptIds.PiEntry("11111111-2222-3333-4444-555555555555", "session", "SessionStarted"));
        await Assert.That(Field(e[0].Payload, "cwd").StringValue).IsEqualTo("/tmp/pi-work");
        await Assert.That(Field(e[0].Payload, "version").NumberValue).IsEqualTo(3);
        await Assert.That(Field(e[0].Payload, "parent_session").StringValue).IsEqualTo("/old/session.jsonl");
        await Assert.That(Field(e[0].Payload, "pi_version").KindCase).IsEqualTo(Value.KindOneofCase.NullValue);
        await Assert.That(Meta(e[0], PiTreeKeys.EntryId)).IsEqualTo("11111111-2222-3333-4444-555555555555");
        await Assert.That(e[0].RecordTimestamp).IsEqualTo("2026-06-12T10:00:00.000Z");
    }

    [Test]
    public async Task A_header_naming_no_session_id_is_keyed_by_its_line() {
        const string line = """{"type":"session","version":3,"timestamp":"2026-06-12T10:00:00.000Z","cwd":"/w"}""";

        await Assert.That(E(line)[0].EventId).IsEqualTo(TranscriptIds.PiHeaderLine(line));
    }

    [Test]
    [Arguments("""{"role":"user","content":"Create hello.txt"}""", "Create hello.txt")]
    [Arguments("""{"role":"user","content":[{"type":"text","text":"Look at this"},{"type":"image","data":"BLOB","mimeType":"image/png"},{"type":"text","text":""},{"type":"text","text":"and that"}]}""", "Look at this\nand that")]
    public async Task A_user_message_is_its_string_or_its_joined_text_blocks(string message, string expected) {
        var e = E($$"""{"type":"message","id":"a1b2c3d4","parentId":null,"timestamp":"2026-06-12T10:00:01.000Z","message":{{message}}}""");

        await Assert.That(((UserMessageReceived)e.Single().Payload).Content).IsEqualTo(expected);
        await Assert.That(e[0].EventId).IsEqualTo(TranscriptIds.PiEntry(Session, "a1b2c3d4", "user"));
    }

    [Test]
    [Arguments("""{"role":"user","content":""}""")]
    [Arguments("""{"role":"user","content":[{"type":"image","data":"BLOB"}]}""")]
    [Arguments("""{"role":"assistant","content":[{"type":"text","text":""},{"type":"toolCall","name":"read"}]}""")]
    [Arguments("""{"role":"toolResult","toolCallId":"","content":"x"}""")]
    [Arguments("""{"role":"bashExecution","command":""}""")]
    [Arguments("""{"role":"custom","content":"x"}""")]
    public async Task A_message_with_nothing_to_record_emits_nothing(string message) =>
        await Assert.That(E($$"""{"type":"message","id":"a1b2c3d4","timestamp":"2026-06-12T10:00:01.000Z","message":{{message}}}""")).IsEmpty();

    const string Assistant = """{"type":"message","id":"b2c3d4e5","parentId":"a1b2c3d4","timestamp":"2026-06-12T10:00:05.000Z","message":{"role":"assistant","content":[{"type":"thinking","thinking":"Plan the file."},{"type":"text","text":"Creating it now."},{"type":"toolCall","id":"call_write_1","name":"write","arguments":{"file_path":"/tmp/pi-work/hello.txt","content":"hello world"}}],"provider":"anthropic","model":"claude-sonnet-4-6","usage":{"input":1200,"output":85,"cacheRead":400,"cacheWrite":7,"totalTokens":99999,"cost":{"input":0.00001,"total":0.03877725}},"stopReason":"toolUse"}}""";

    [Test]
    public async Task An_assistant_message_emits_one_event_per_block_in_order_each_a_tick_later() {
        var e = E(Assistant);

        await Assert.That(e.Select(x => x.EventType)).IsEquivalentTo(
            [CanonicalEventTypes.AssistantThinkingGenerated, CanonicalEventTypes.AssistantTextGenerated, CanonicalEventTypes.AssistantToolCallsGenerated],
            CollectionOrdering.Matching);
        await Assert.That(e.Select(x => x.EventId)).IsEquivalentTo([
            TranscriptIds.PiEntry(Session, "b2c3d4e5", "assistant:0:thinking"),
            TranscriptIds.PiEntry(Session, "b2c3d4e5", "assistant:1:text"),
            TranscriptIds.PiEntry(Session, "b2c3d4e5", "assistant:2:tool:call_write_1"),
        ], CollectionOrdering.Matching);

        var at = DateTimeOffset.Parse("2026-06-12T10:00:05.000Z", CultureInfo.InvariantCulture);
        await Assert.That(e.Select(x => x.Timestamp)).IsEquivalentTo([at, at.AddTicks(1), at.AddTicks(2)], CollectionOrdering.Matching);
        await Assert.That(e.Select(x => CanonicalEventTime.Of(x.Payload))).IsEquivalentTo(e.Select(x => (DateTimeOffset?)x.Timestamp), CollectionOrdering.Matching);
        await Assert.That(e.All(x => x.RecordTimestamp == "2026-06-12T10:00:05.000Z")).IsTrue();

        var call = ((AssistantToolCallsGenerated)e[2].Payload).ToolCalls.Single();
        await Assert.That(call.CallId).IsEqualTo("call_write_1");
        await Assert.That(call.ToolName).IsEqualTo("write");
        await Assert.That(call.ToolKind).IsEqualTo(AcpToolKind.Edit);
        await Assert.That(call.Arguments.Fields["file_path"].StringValue).IsEqualTo("/tmp/pi-work/hello.txt");
    }

    /// <summary>The total is the sum of the buckets, not Pi's own <c>totalTokens</c>, and cache writes ride beside
    /// the usage.</summary>
    [Test]
    public async Task Usage_and_the_turn_ride_only_the_first_event() {
        var e = E(Assistant);

        var usage = e[0].Usage!;
        await Assert.That(usage.InputTokens).IsEqualTo(1200);
        await Assert.That(usage.OutputTokens).IsEqualTo(85);
        await Assert.That(usage.CachedInputTokens).IsEqualTo(400);
        await Assert.That(usage.TotalTokens).IsEqualTo(1200 + 85 + 400 + 7);
        await Assert.That(usage.Model).IsEqualTo("claude-sonnet-4-6");
        await Assert.That(e[0].CacheCreationTokens).IsEqualTo(7);
        await Assert.That(e.Skip(1).All(x => x.Usage is null && x.CacheCreationTokens is null)).IsTrue();

        await Assert.That(Field(e[0].Payload, "provider").StringValue).IsEqualTo("anthropic");
        await Assert.That(Field(e[0].Payload, "model").StringValue).IsEqualTo("claude-sonnet-4-6");
        await Assert.That(Field(e[0].Payload, "stop_reason").StringValue).IsEqualTo("toolUse");
        await Assert.That(Field(e[0].Payload, "parent_id").StringValue).IsEqualTo("a1b2c3d4");
        await Assert.That(Field(e[0].Payload, "cost").NumberValue).IsEqualTo(0.03877725);
        await Assert.That(SchemaExtensions.Slug(e[1].Payload, PiExtension.Slug)).IsNull();
        await Assert.That(SchemaExtensions.Slug(e[2].Payload, PiExtension.Slug)).IsNull();
    }

    [Test]
    [Arguments("""{"model":"m"}""")]
    [Arguments("""{"model":"m","usage":{"totalTokens":5}}""")]
    public async Task A_message_reporting_no_token_bucket_records_no_usage(string fields) {
        var line = $$$"""{"type":"message","id":"b2c3d4e5","timestamp":"2026-06-12T10:00:05.000Z","message":{"role":"assistant","content":[{"type":"text","text":"x"}],{{{fields[1..^1]}}}}}""";
        var e    = E(line).Single();

        await Assert.That(e.Usage).IsNull();
        await Assert.That(e.CacheCreationTokens).IsNull();
        await Assert.That(Field(e.Payload, "model").StringValue).IsEqualTo("m");
    }

    [Test]
    public async Task An_assistant_message_with_no_turn_field_carries_no_pi_slug() =>
        await Assert.That(SchemaExtensions.Slug(E("""{"type":"message","id":"b2c3d4e5","message":{"role":"assistant","content":[{"type":"text","text":"x"}]}}""").Single().Payload, PiExtension.Slug)).IsNull();

    /// <summary>Without the block's index in the id, two text blocks of one message would share an id and the
    /// store would keep only the first.</summary>
    [Test]
    public async Task Repeated_text_and_thinking_blocks_get_distinct_ids() {
        var e = E("""{"type":"message","id":"dup12345","parentId":null,"timestamp":"2026-06-12T10:00:05.000Z","message":{"role":"assistant","content":[{"type":"thinking","thinking":"first thought"},{"type":"thinking","thinking":"second thought"},"not a block",{"type":"text","text":"first answer"},{"type":"text","text":"second answer"}]}}""");

        await Assert.That(e).Count().IsEqualTo(4);
        await Assert.That(e.Select(x => x.EventId).Distinct()).Count().IsEqualTo(4);
        await Assert.That(e[3].EventId).IsEqualTo(TranscriptIds.PiEntry(Session, "dup12345", "assistant:4:text"));
    }

    [Test]
    public async Task A_tool_result_carries_its_success_and_tool_name() {
        var ok  = E("""{"type":"message","id":"c3d4e5f6","parentId":"b2c3d4e5","timestamp":"2026-06-12T10:00:06.000Z","message":{"role":"toolResult","toolCallId":"call_write_1","toolName":"write","content":[{"type":"text","text":"Wrote 11 bytes"}],"isError":false}}""").Single();
        var bad = E("""{"type":"message","id":"e5f6a7b8","timestamp":"2026-06-12T10:00:09.000Z","message":{"role":"toolResult","toolCallId":"call_bash_1","toolName":"bash","content":"No such file","isError":true,"details":42}}""").Single();

        var result = (ToolResultReceived)ok.Payload;
        await Assert.That(result.CallId).IsEqualTo("call_write_1");
        await Assert.That(result.Result).IsEqualTo("Wrote 11 bytes");
        await Assert.That(ok.EventId).IsEqualTo(TranscriptIds.PiEntry(Session, "c3d4e5f6", "toolResult"));
        await Assert.That(Field(ok.Payload, "success").BoolValue).IsTrue();
        await Assert.That(Field(ok.Payload, "tool_name").StringValue).IsEqualTo("write");
        await Assert.That(Field(ok.Payload, "details_json").KindCase).IsEqualTo(Value.KindOneofCase.NullValue);
        await Assert.That(Field(ok.Payload, "user_bash").KindCase).IsEqualTo(Value.KindOneofCase.NullValue);

        await Assert.That(Field(bad.Payload, "success").BoolValue).IsFalse();
        await Assert.That(Field(bad.Payload, "details").StringValue).IsEqualTo("42");
    }

    [Test]
    public async Task A_tool_result_with_nothing_for_the_slug_carries_none() =>
        await Assert.That(SchemaExtensions.Slug(E("""{"type":"message","id":"c3d4e5f6","message":{"role":"toolResult","toolCallId":"c1","content":"ok"}}""").Single().Payload, PiExtension.Slug)).IsNull();

    [Test]
    public async Task A_subagent_result_keeps_its_details_without_child_transcripts_and_sums_them() {
        var e = E("""{"type":"message","id":"dd00aa11","parentId":"cc00aa11","timestamp":"2026-06-12T10:00:10.000Z","message":{"role":"toolResult","toolCallId":"call_subagent_1","toolName":"subagent","content":[{"type":"text","text":"Subagents complete"}],"isError":false,"details":{"mode":"parallel","agentScope":"project","projectAgentsDir":"/work/.pi/agents","results":[{"agent":"reviewer","exitCode":0,"messages":[{"role":"assistant","content":[{"type":"text","text":"looks good"}]}],"usage":{"input":10,"output":4,"cacheRead":2,"cacheWrite":1,"cost":0.0123,"contextTokens":17,"turns":1},"stopReason":"stop"},{"agent":"tester","messages":[],"usage":{"input":7,"output":3,"cacheRead":0,"cacheWrite":0,"cost":0.004,"contextTokens":10,"turns":1},"stopReason":"error","errorMessage":"tests failed"},"skipped"]}}}""").Single();

        var details = Field(e.Payload, "details_json").StructValue;
        await Assert.That(details.Fields["mode"].StringValue).IsEqualTo("parallel");
        var results = details.Fields["results"].ListValue.Values;
        await Assert.That(results).Count().IsEqualTo(3);
        await Assert.That(results[0].StructValue.Fields.ContainsKey("messages")).IsFalse();
        await Assert.That(results[1].StructValue.Fields.ContainsKey("messages")).IsFalse();
        await Assert.That(results[1].StructValue.Fields["errorMessage"].StringValue).IsEqualTo("tests failed");
        await Assert.That(Field(e.Payload, "details").KindCase).IsEqualTo(Value.KindOneofCase.NullValue);

        var subagent = Field(e.Payload, "subagent").StructValue.Fields;
        await Assert.That(subagent["mode"].StringValue).IsEqualTo("parallel");
        await Assert.That(subagent["agent_scope"].StringValue).IsEqualTo("project");
        await Assert.That(subagent["project_agents_dir"].StringValue).IsEqualTo("/work/.pi/agents");
        await Assert.That(subagent["result_count"].NumberValue).IsEqualTo(2);
        await Assert.That(subagent["success_count"].NumberValue).IsEqualTo(1);
        await Assert.That(subagent["failure_count"].NumberValue).IsEqualTo(1);
        await Assert.That(subagent["input_tokens"].NumberValue).IsEqualTo(17);
        await Assert.That(subagent["output_tokens"].NumberValue).IsEqualTo(7);
        await Assert.That(subagent["cache_read_tokens"].NumberValue).IsEqualTo(2);
        await Assert.That(subagent["cache_write_tokens"].NumberValue).IsEqualTo(1);
        await Assert.That(subagent["context_tokens"].NumberValue).IsEqualTo(27);
        await Assert.That(subagent["turns"].NumberValue).IsEqualTo(2);
        await Assert.That(subagent["cost"].NumberValue).IsEqualTo(0.0123 + 0.004);
    }

    [Test]
    public async Task Another_tools_structured_details_are_kept_verbatim() {
        var e = E("""{"type":"message","id":"dd00aa12","message":{"role":"toolResult","toolCallId":"c2","toolName":"read","content":"x","details":[{"messages":1}]}}""").Single();

        await Assert.That(Field(e.Payload, "details_json").ListValue.Values[0].StructValue.Fields["messages"].NumberValue).IsEqualTo(1);
        await Assert.That(Field(e.Payload, "subagent").KindCase).IsEqualTo(Value.KindOneofCase.NullValue);
    }

    [Test]
    public async Task A_user_shell_command_is_a_bash_call_and_its_result() {
        var e = E("""{"type":"message","id":"f6a7b8c9","parentId":"e5f6a7b8","timestamp":"2026-06-12T10:00:12.000Z","message":{"role":"bashExecution","command":"ls -la","output":"file1\nfile2","exitCode":2,"cancelled":false,"truncated":false}}""");

        await Assert.That(e.Select(x => x.EventType)).IsEquivalentTo(
            [CanonicalEventTypes.AssistantToolCallsGenerated, CanonicalEventTypes.ToolResultReceived], CollectionOrdering.Matching);
        await Assert.That(e[0].EventId).IsEqualTo(TranscriptIds.PiEntry(Session, "f6a7b8c9", "bash:call"));
        await Assert.That(e[1].EventId).IsEqualTo(TranscriptIds.PiEntry(Session, "f6a7b8c9", "bash:result"));
        await Assert.That(e[1].Timestamp).IsEqualTo(e[0].Timestamp.AddTicks(1));

        var call = ((AssistantToolCallsGenerated)e[0].Payload).ToolCalls.Single();
        await Assert.That(call.CallId).IsEqualTo("pi-bash-f6a7b8c9");
        await Assert.That(call.ToolName).IsEqualTo("bash");
        await Assert.That(call.ToolKind).IsEqualTo(AcpToolKind.Execute);
        await Assert.That(call.Arguments.Fields["command"].StringValue).IsEqualTo("ls -la");

        var result = (ToolResultReceived)e[1].Payload;
        await Assert.That(result.CallId).IsEqualTo(call.CallId);
        await Assert.That(result.Result).IsEqualTo("file1\nfile2");
        await Assert.That(Field(result, "user_bash").BoolValue).IsTrue();
        await Assert.That(Field(result, "exit_code").NumberValue).IsEqualTo(2);
        await Assert.That(Field(result, "success").BoolValue).IsFalse();
        await Assert.That(Field(result, "command").StringValue).IsEqualTo("ls -la");
        await Assert.That(SchemaExtensions.Slug(e[0].Payload, PiExtension.Slug)).IsNull();
    }

    [Test]
    public async Task A_compaction_is_the_entry_itself_as_replacement_history() {
        const string line = """{"type":"compaction","id":"3a4b5c6d","parentId":"29384b5c","timestamp":"2026-06-12T10:00:22.000Z","summary":"s","firstKeptEntryId":"c3d4e5f6","tokensBefore":50000}""";
        var e = E(line).Single();

        await Assert.That(e.EventType).IsEqualTo(CanonicalEventTypes.ContextCompacted);
        await Assert.That(e.EventId).IsEqualTo(TranscriptIds.PiEntry(Session, "3a4b5c6d", "compaction"));
        var compacted = (ContextCompactedEvent)e.Payload;
        await Assert.That(compacted.ReplacementHistory!.Value.GetRawText()).IsEqualTo(line);
        await Assert.That(compacted.EncryptedContent).IsNull();
    }

    /// <summary>A compaction entry is one object, not a list of entries, so over the budget nothing partial is
    /// worth keeping: it is dropped and its size recorded.</summary>
    [Test]
    public async Task A_compaction_over_the_budget_drops_the_entry_and_records_its_size() {
        var e = E($$"""{"type":"compaction","id":"3a4b5c6d","timestamp":"2026-06-12T10:00:22.000Z","summary":"{{new string('a', CompactionHistoryBound.MaxBytes)}}"}""").Single();

        var compacted = (ContextCompactedEvent)e.Payload;
        await Assert.That(compacted.ReplacementHistory).IsNull();
        await Assert.That(compacted.ReplacementHistoryTruncated).IsTrue();
        await Assert.That(compacted.ReplacementHistoryEntries).IsNull();
        await Assert.That(compacted.ReplacementHistoryBytes!.Value).IsGreaterThan(CompactionHistoryBound.MaxBytes);
    }

    [Test]
    public async Task A_branch_summary_is_assistant_text_carrying_its_links() {
        var e = E("""{"type":"branch_summary","id":"aabbccdd","parentId":"a1b2c3d4","timestamp":"2026-06-12T10:00:22.000Z","fromId":"f6a7b8c9","summary":"Branch explored approach A.","details":{"reason":"tree navigation"},"fromHook":false}""").Single();

        await Assert.That(e.EventId).IsEqualTo(TranscriptIds.PiEntry(Session, "aabbccdd", "branch_summary"));
        await Assert.That(((AssistantTextGenerated)e.Payload).Content).IsEqualTo("Branch explored approach A.");
        await Assert.That(Field(e.Payload, "from_id").StringValue).IsEqualTo("f6a7b8c9");
        await Assert.That(Field(e.Payload, "parent_id").StringValue).IsEqualTo("a1b2c3d4");
        await Assert.That(Field(e.Payload, "summary").StringValue).IsEqualTo("Branch explored approach A.");
        await Assert.That(Field(e.Payload, "details").StructValue.Fields["reason"].StringValue).IsEqualTo("tree navigation");
        await Assert.That(Field(e.Payload, "from_hook").BoolValue).IsFalse();
    }

    [Test]
    [Arguments("""{"type":"branch_summary","id":"aabbccdd","summary":"  "}""")]
    [Arguments("""{"type":"model_change","id":"29384b5c","parentId":"18293a4b","timestamp":"2026-06-12T10:00:21.000Z","provider":"openai","modelId":"gpt-5"}""")]
    [Arguments("""{"type":"session_info","id":"4b5c6d7e","parentId":"3a4b5c6d","timestamp":"2026-06-12T10:00:23.000Z","name":"Create hello.txt"}""")]
    [Arguments("""{"type":"thinking_level_change","id":"5c6d7e8f","parentId":"4b5c6d7e","thinkingLevel":"high"}""")]
    [Arguments("""{"type":"label","id":"6d7e8f90","parentId":"5c6d7e8f","targetId":"b2c3d4e5","label":"checkpoint-1"}""")]
    [Arguments("""{"type":"custom","id":"h8i9j0k1","parentId":"g7h8i9j0","customType":"ext","data":{"count":42}}""")]
    [Arguments("""{"type":"some.future_event","id":"b3499ffe","parentId":null}""")]
    [Arguments("""{"type":"message","id":"b3499fff"}""")]
    [Arguments("""{"id":"b3499ff0","message":{"role":"user","content":"untyped"}}""")]
    public async Task Control_and_unknown_entries_emit_nothing_and_are_not_rejected(string line) {
        var result = P(line, Ctx());

        await Assert.That(result.Rejected).IsNull();
        await Assert.That(result.Events).IsEmpty();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("{not json")]
    [Arguments("[1,2]")]
    [Arguments("\"session\"")]
    public async Task A_line_that_is_not_a_json_object_is_rejected(string line) =>
        await Assert.That(P(line, Ctx()).Rejected).IsNotNull();

    [Test]
    public async Task An_entry_with_no_id_or_a_stream_with_no_session_id_is_keyed_by_its_line() {
        const string noEntry = """{"type":"message","timestamp":"2026-06-12T10:00:01.000Z","message":{"role":"user","content":"hi"}}""";
        const string entry   = """{"type":"message","id":"a1","timestamp":"2026-06-12T10:00:01.000Z","message":{"role":"user","content":"hi"}}""";

        await Assert.That(E(noEntry).Single().EventId).IsEqualTo(TranscriptIds.PiLine(noEntry, "user"));
        await Assert.That(E(entry, Ctx("")).Single().EventId).IsEqualTo(TranscriptIds.PiLine(entry, "user"));
        await Assert.That(E(entry, Ctx(null!)).Single().EventId).IsEqualTo(TranscriptIds.PiLine(entry, "user"));
        await Assert.That(E(noEntry).Single().VendorMetadata).IsNull();
    }

    /// <summary>The server stamps a line with no time of its own at the minimum instant; the projection uses the
    /// receive time, as every projection does.</summary>
    [Test]
    public async Task A_line_with_no_time_of_its_own_is_stamped_with_the_receive_time() {
        var e = E("""{"type":"message","id":"a1","message":{"role":"user","content":"hi"}}""").Single();

        await Assert.That(e.Timestamp).IsEqualTo(Received);
        await Assert.That(((UserMessageReceived)e.Payload).Timestamp.ToDateTimeOffset()).IsEqualTo(Received);
        await Assert.That(e.RecordTimestamp).IsNull();
    }

    [Test]
    public async Task Projecting_the_same_line_again_yields_the_same_ids() {
        await Assert.That(E(Assistant).Select(x => x.EventId)).IsEquivalentTo(E(Assistant).Select(x => x.EventId), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("read", AcpToolKind.Read)]
    [Arguments("edit", AcpToolKind.Edit)]
    [Arguments("write", AcpToolKind.Edit)]
    [Arguments("bash", AcpToolKind.Execute)]
    [Arguments("powershell", AcpToolKind.Execute)]
    [Arguments("grep", AcpToolKind.Search)]
    [Arguments("find", AcpToolKind.Search)]
    [Arguments("ls", AcpToolKind.Search)]
    [Arguments("subagent", AcpToolKind.Other)]
    [Arguments("mcp__server__tool", AcpToolKind.Other)]
    public async Task A_tool_call_is_classified_by_its_name(string name, string kind) =>
        await Assert.That(PiToolKinds.Of(name)).IsEqualTo(kind);

    // ── the tree ─────────────────────────────────────────────────────────────────────────────────

    const string Header   = """{"type":"session","version":3,"id":"s-1","timestamp":"2026-06-12T10:00:00.000Z"}""";
    const string Model    = """{"type":"model_change","id":"m1","parentId":"u1","timestamp":"2026-06-12T10:00:02.000Z"}""";
    const string Label    = """{"type":"label","id":"l1","parentId":"m1","timestamp":"2026-06-12T10:00:03.000Z"}""";
    const string Prompt   = """{"type":"message","id":"u1","parentId":null,"timestamp":"2026-06-12T10:00:01.000Z","message":{"role":"user","content":"a"}}""";
    const string Reply    = """{"type":"message","id":"a1","parentId":"l1","timestamp":"2026-06-12T10:00:04.000Z","message":{"role":"assistant","content":[{"type":"text","text":"x"},{"type":"text","text":"y"}]}}""";

    /// <summary>The raw parent names the control entry, which the picker needs to find a fork; the render parent
    /// skips past it to the entry that produced an event, and every event of one entry carries the same node.</summary>
    [Test]
    public async Task Every_event_carries_its_entry_its_raw_parent_and_its_nearest_emitting_ancestor() {
        var ctx = Ctx();
        foreach (var line in new[] { Header, Prompt, Model, Label }) P(line, ctx);
        var reply = E(Reply, ctx);

        await Assert.That(reply).Count().IsEqualTo(2);
        foreach (var e in reply) {
            await Assert.That(Meta(e, PiTreeKeys.EntryId)).IsEqualTo("a1");
            await Assert.That(Meta(e, PiTreeKeys.ParentId)).IsEqualTo("l1");
            await Assert.That(Meta(e, PiTreeKeys.RenderParent)).IsEqualTo("u1");
        }
    }

    [Test]
    public async Task A_root_entry_carries_no_parent_and_no_render_parent() {
        var prompt = E(Prompt).Single();

        await Assert.That(prompt.VendorMetadata!.Keys).IsEquivalentTo([PiTreeKeys.EntryId]);
    }

    [Test]
    public async Task An_entry_that_emitted_nothing_is_never_a_render_parent() {
        var ctx = Ctx();
        P(Prompt, ctx);
        P("""{"type":"message","id":"a0","parentId":"u1","message":{"role":"assistant","content":[]}}""", ctx);
        var e = E("""{"type":"message","id":"r0","parentId":"a0","message":{"role":"toolResult","toolCallId":"c","content":"x"}}""", ctx).Single();

        await Assert.That(Meta(e, PiTreeKeys.RenderParent)).IsEqualTo("u1");
    }

    /// <summary>The tree spans the session, so a batch boundary between an entry and its child changes nothing.</summary>
    [Test]
    public async Task The_tree_survives_a_batch_boundary() {
        var ctx = Ctx();
        foreach (var line in new[] { Header, Prompt, Model, Label }) {
            ctx.BeginBatch();
            P(line, ctx);
        }
        ctx.BeginBatch();

        await Assert.That(Meta(E(Reply, ctx)[0], PiTreeKeys.RenderParent)).IsEqualTo("u1");
    }

    /// <summary>A context that never read the earlier lines resolves no render parent until the caller seeds it with
    /// the entries it stored.</summary>
    [Test]
    public async Task A_new_context_resolves_render_parents_from_the_entries_seeded_into_it() {
        await Assert.That(Meta(E(Reply)[0], PiTreeKeys.RenderParent)).IsNull();

        var seeded = Ctx();
        seeded.Seed("u1", null, emitted: true);
        seeded.Seed("m1", "u1", emitted: false);
        seeded.Seed("l1", "m1", emitted: false);

        await Assert.That(Meta(E(Reply, seeded)[0], PiTreeKeys.RenderParent)).IsEqualTo("u1");
    }

    /// <summary>Stored events hold no control entry's link, so seeding only them leaves an entry whose parent is a
    /// control entry without a render parent, while one whose parent emitted still resolves.</summary>
    [Test]
    public async Task Seeding_only_emitting_entries_resolves_through_them_and_not_through_control_entries() {
        var seeded = Ctx();
        seeded.Seed("u1", null, emitted: true);

        await Assert.That(Meta(E(Reply, seeded)[0], PiTreeKeys.RenderParent)).IsNull();
        await Assert.That(Meta(E("""{"type":"message","id":"a2","parentId":"u1","message":{"role":"assistant","content":[{"type":"text","text":"x"}]}}""", seeded)[0], PiTreeKeys.RenderParent)).IsEqualTo("u1");
    }

    [Test]
    public async Task A_parent_cycle_resolves_no_render_parent() {
        var ctx = Ctx();
        ctx.Seed("x1", "x2", emitted: false);
        ctx.Seed("x2", "x1", emitted: false);

        await Assert.That(Meta(E("""{"type":"message","id":"u9","parentId":"x1","message":{"role":"user","content":"a"}}""", ctx).Single(), PiTreeKeys.RenderParent)).IsNull();
    }

    [Test]
    public async Task A_rejected_line_leaves_the_tree_as_it_was() {
        var ctx = Ctx();
        P(Prompt, ctx);
        P("""{"type":"message","id":"l1","parentId":"u1","message":{"role":"user","content":"b"}""", ctx);

        await Assert.That(Meta(E(Reply, ctx)[0], PiTreeKeys.RenderParent)).IsNull();
    }

    /// <summary>Both branches of the fork in the corpus's branched session start at the same control entry: what
    /// the server's branch picker detects the fork by.</summary>
    [Test]
    public async Task Both_branches_of_a_fork_name_the_entry_they_split_at() {
        var lines = await File.ReadAllLinesAsync(Path.Combine(SourceDir(), "Fixtures", "019ed76b-c66e-74ed-b751-5ab9d7d58a74.jsonl"));
        var ctx   = Ctx("019ed76bc66e74edb7515ab9d7d58a74");
        var users = lines.Select((line, i) => Projection.Project(line, i, Received, ctx))
                         .SelectMany(r => r.Events)
                         .Where(e => e.Payload is UserMessageReceived)
                         .ToArray();

        var roots = users.Where(e => Meta(e, PiTreeKeys.ParentId) == "d74ff1c3").ToArray();
        await Assert.That(roots).Count().IsEqualTo(2);
        await Assert.That(((UserMessageReceived)roots[0].Payload).Content).Contains("hello");
        await Assert.That(((UserMessageReceived)roots[1].Payload).Content).Contains("goodbye");
        await Assert.That(roots.All(e => Meta(e, PiTreeKeys.RenderParent) is null)).IsTrue();
    }

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
