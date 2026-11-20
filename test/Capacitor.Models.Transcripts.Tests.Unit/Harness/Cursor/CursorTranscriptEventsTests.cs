using System.Text.Json;
using Capacitor.Models.Transcripts.Harness.Cursor;
using Google.Protobuf;
using Kurrent.Agent.Schema.Events;
using TUnit.Assertions.Enums;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Cursor;

/// <summary>The rules of kcap-server's Cursor normalizer, one line at a time: what each role emits, the ids and
/// times it emits them on, how a prompt pairs with its hook-reported turn, and which lines it ignores or
/// rejects.</summary>
public class CursorTranscriptEventsTests {
    const string Session = "c0cab74dcfb64bcbbeb8e86ad0ce5281";

    static readonly DateTimeOffset Received = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    static CursorContext Context(string sessionId = Session, string? agentId = null) =>
        (CursorContext)CursorTranscriptEvents.Instance.CreateContext(sessionId, agentId);

    static ProjectionResult P(string line, CursorContext? ctx = null, int lineNumber = 4) =>
        CursorTranscriptEvents.Instance.Project(line, lineNumber, Received, ctx ?? Context());

    static IReadOnlyList<CanonicalEvent> E(string line, CursorContext? ctx = null, int lineNumber = 4) => P(line, ctx, lineNumber).Events;

    static string Line(string role, params string[] blocks) =>
        $$$"""{"role":"{{{role}}}","message":{"content":[{{{string.Join(',', blocks)}}}]}}""";

    static string Text(string text) => $$"""{"type":"text","text":{{JsonSerializer.Serialize(text)}}}""";

    static string User(string text) => Line("user", Text(text));

    const string Wrapped = "<timestamp>Wednesday, Jul 8, 2026, 9:49 AM (UTC-4)</timestamp>\n<user_query>\nHave we worked on ACP before?\n</user_query>";

    // ── User messages ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_prompt_is_a_user_message_unwrapped_at_its_send_time() {
        var e       = E(User(Wrapped)).Single();
        var message = (UserMessageReceived)e.Payload;
        var sentAt  = new DateTimeOffset(2026, 7, 8, 13, 49, 0, TimeSpan.Zero);

        await Assert.That(e.EventType).IsEqualTo(CanonicalEventTypes.UserMessageReceived);
        await Assert.That(message.Content).IsEqualTo("Have we worked on ACP before?");
        await Assert.That(message.MessageId).IsEqualTo("line:4");
        await Assert.That(message.Timestamp.ToDateTimeOffset()).IsEqualTo(sentAt);
        await Assert.That(message.CreatedAt.ToDateTimeOffset()).IsEqualTo(sentAt);
        await Assert.That(e.Timestamp).IsEqualTo(sentAt);
        await Assert.That(e.EventId).IsEqualTo(TranscriptIds.CursorBlock(Session, 4, "user", 0, CanonicalEventTypes.UserMessageReceived));
    }

    [Test]
    public async Task A_prompt_without_a_send_time_takes_the_receive_time() {
        var e = E(User("thanks")).Single();

        await Assert.That(((UserMessageReceived)e.Payload).Content).IsEqualTo("thanks");
        await Assert.That(((UserMessageReceived)e.Payload).Timestamp.ToDateTimeOffset()).IsEqualTo(Received);
        await Assert.That(e.Timestamp).IsEqualTo(Received);
    }

    /// <summary>The server stores no <c>$timestamp</c>, no cause and no usage for a Cursor event, and the same two
    /// line keys on each.</summary>
    [Test]
    public async Task Every_event_carries_the_line_metadata_and_nothing_else_beside_its_payload() {
        var events = E(Line("assistant", Text("one"), """{"type":"tool_use","name":"Read","input":{}}"""), lineNumber: 12);

        await Assert.That(events.Count).IsEqualTo(2);
        foreach (var e in events) {
            await Assert.That(e.VendorMetadata!.Count).IsEqualTo(2);
            await Assert.That(e.VendorMetadata["$source"]).IsEqualTo("cursor-live");
            await Assert.That(e.VendorMetadata["line"]).IsEqualTo("12");
            await Assert.That(e.RecordTimestamp).IsNull();
            await Assert.That(e.CausedBy).IsNull();
            await Assert.That(e.Usage).IsNull();
            await Assert.That(e.Attachments).IsNull();
        }
    }

    [Test]
    public async Task Only_the_first_non_empty_text_block_is_the_message_on_that_blocks_index() {
        var e = E(Line("user", Text(""), Text("first"), Text("second"))).Single();

        await Assert.That(((UserMessageReceived)e.Payload).Content).IsEqualTo("first");
        await Assert.That(e.EventId).IsEqualTo(TranscriptIds.CursorBlock(Session, 4, "user", 1, CanonicalEventTypes.UserMessageReceived));
    }

    /// <summary>An attachment-only prompt unwraps to an empty query; the raw text is what has to be non-empty.</summary>
    [Test]
    public async Task An_empty_query_still_emits_and_asks_for_its_turn() {
        var asked = new List<string>();
        var ctx   = Context();
        ctx.TakeTurn = q => {
            asked.Add(q);
            return null;
        };

        var e = E(User("<user_query>\n</user_query>"), ctx).Single();

        await Assert.That(((UserMessageReceived)e.Payload).Content).IsEqualTo("");
        await Assert.That(asked).IsEquivalentTo(new[] { "" });
    }

    [Test]
    public async Task A_line_with_tool_results_and_text_emits_both_in_block_order() {
        var line = Line("user",
            """{"type":"tool_result","tool_use_id":"toolu_04Build","content":"Build succeeded."}""",
            Text("also make it resizable"),
            Text("and remember its size"));

        var events = E(line);

        await Assert.That(events.Select(e => e.EventType))
            .IsEquivalentTo(new[] { CanonicalEventTypes.ToolResultReceived, CanonicalEventTypes.UserMessageReceived }, CollectionOrdering.Matching);
        await Assert.That(((UserMessageReceived)events[1].Payload).Content).IsEqualTo("also make it resizable");
        await Assert.That(events[1].EventId).IsEqualTo(TranscriptIds.CursorBlock(Session, 4, "user", 1, CanonicalEventTypes.UserMessageReceived));
    }

    // ── The hook-reported turn ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_turn_the_caller_holds_for_the_query_becomes_the_cursor_slug() {
        var ctx = Context();
        ctx.TakeTurn = q => q == "Have we worked on ACP before?"
            ? new CursorTurn("gen-9", [new CursorTurnAttachment("rule", "AGENTS.md"), new CursorTurnAttachment("selection", null)])
            : null;

        var slug = SchemaExtensions.Slug(E(User(Wrapped), ctx).Single().Payload, CursorExtension.Slug);

        await Assert.That(slug).IsNotNull();
        await Assert.That(JsonFormatter.Default.Format(slug!)).IsEqualTo(
            """{ "generation_id": "gen-9", "attachments": [ { "type": "rule", "file_path": "AGENTS.md" }, { "type": "selection", "file_path": null } ] }""");
    }

    [Test]
    public async Task No_turn_leaves_the_message_without_a_slug() {
        var ctx = Context();
        ctx.TakeTurn = _ => null;

        await Assert.That(((UserMessageReceived)E(User("hi"), ctx).Single().Payload).Extensions).IsEmpty();
        await Assert.That(((UserMessageReceived)E(User("hi")).Single().Payload).Extensions).IsEmpty();
    }

    /// <summary>The caller's queue moves on every ask, so only a user message asks, once.</summary>
    [Test]
    public async Task Only_a_user_message_asks_for_a_turn() {
        var asked = 0;
        var ctx   = Context();
        ctx.TakeTurn = _ => {
            asked++;
            return null;
        };

        E(Line("user", Text("one"), Text("two")), ctx);
        E(Line("user", """{"type":"tool_result","tool_use_id":"t","content":"x"}"""), ctx);
        E(Line("assistant", Text("reply")), ctx);
        E(Line("user", Text("")), ctx);

        await Assert.That(asked).IsEqualTo(1);
    }

    // ── Tool results ───────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_tool_result_is_received_at_the_receive_time_on_its_blocks_index() {
        var events = E(Line("user",
            """{"type":"tool_result","tool_use_id":"tu-a","content":"out-a"}""",
            """{"type":"tool_result","tool_use_id":"tu-b","content":"out-b"}"""));

        await Assert.That(events.Select(e => ((ToolResultReceived)e.Payload).CallId)).IsEquivalentTo(new[] { "tu-a", "tu-b" }, CollectionOrdering.Matching);
        await Assert.That(events.Select(e => ((ToolResultReceived)e.Payload).Result)).IsEquivalentTo(new[] { "out-a", "out-b" }, CollectionOrdering.Matching);
        await Assert.That(events[1].EventId).IsEqualTo(TranscriptIds.CursorBlock(Session, 4, "tool_result", 1, CanonicalEventTypes.ToolResultReceived));
        await Assert.That(((ToolResultReceived)events[0].Payload).MessageId).IsEqualTo("line:4");
        await Assert.That(((ToolResultReceived)events[0].Payload).Timestamp.ToDateTimeOffset()).IsEqualTo(Received);
        await Assert.That(events[0].Timestamp).IsEqualTo(Received);
    }

    [Test]
    [Arguments("""{"type":"tool_result","tool_use_id":"t","content":[{"type":"text","text":"a"}]}""", """[{"type":"text","text":"a"}]""")]
    [Arguments("""{"type":"tool_result","tool_use_id":"t","content":null}""", "null")]
    [Arguments("""{"type":"tool_result","tool_use_id":"t"}""", "")]
    public async Task A_result_that_is_not_a_string_is_its_raw_json(string block, string expected) {
        await Assert.That(((ToolResultReceived)E(Line("user", block)).Single().Payload).Result).IsEqualTo(expected);
    }

    [Test]
    public async Task A_result_without_a_call_id_has_an_empty_one() {
        await Assert.That(((ToolResultReceived)E(Line("user", """{"type":"tool_result","content":"x"}""")).Single().Payload).CallId).IsEqualTo("");
    }

    // ── Assistant messages ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task An_assistant_line_emits_each_text_and_tool_use_on_its_blocks_index() {
        var events = E(Line("assistant",
            Text("I'll glob files"),
            Text(""),
            """{"type":"thinking","thinking":"hidden"}""",
            """{"type":"tool_use","name":"Glob","input":{"pattern":"*.cs"}}"""));

        await Assert.That(events.Select(e => e.EventType))
            .IsEquivalentTo(new[] { CanonicalEventTypes.AssistantTextGenerated, CanonicalEventTypes.AssistantToolCallsGenerated }, CollectionOrdering.Matching);
        await Assert.That(events[0].EventId).IsEqualTo(TranscriptIds.CursorBlock(Session, 4, "assistant_text", 0, CanonicalEventTypes.AssistantTextGenerated));
        await Assert.That(events[1].EventId).IsEqualTo(TranscriptIds.CursorBlock(Session, 4, "tool_use", 3, CanonicalEventTypes.AssistantToolCallsGenerated));

        var text = (AssistantTextGenerated)events[0].Payload;
        await Assert.That(text.Content).IsEqualTo("I'll glob files");
        await Assert.That(text.MessageId).IsEqualTo("line:4");
        await Assert.That(text.Timestamp.ToDateTimeOffset()).IsEqualTo(Received);

        var call = ((AssistantToolCallsGenerated)events[1].Payload).ToolCalls.Single();
        await Assert.That(call.ToolName).IsEqualTo("Glob");
        await Assert.That(call.ToolKind).IsEqualTo(AcpToolKind.Search);
        await Assert.That(JsonFormatter.Default.Format(call.Arguments)).IsEqualTo("""{ "pattern": "*.cs" }""");
    }

    /// <summary>The paired result names the block's id, so a real one is kept; Cursor writes none on most calls.</summary>
    [Test]
    [Arguments(""","id":"toolu_abc" """, "toolu_abc")]
    [Arguments("", "line:6:1")]
    [Arguments(""","id":123""", "line:6:1")]
    [Arguments(""","id":"" """, "line:6:1")]
    public async Task A_tool_use_keeps_its_own_id_or_takes_its_place(string id, string expected) {
        var line = Line("assistant", Text("x"), $$$"""{"type":"tool_use"{{{id}}},"name":"Glob","input":{}}""");

        var calls = (AssistantToolCallsGenerated)E(line, lineNumber: 6)[1].Payload;

        await Assert.That(calls.ToolCalls.Single().CallId).IsEqualTo(expected);
    }

    [Test]
    [Arguments(""","input":"just text" """)]
    [Arguments(""","input":["a"]""")]
    [Arguments(""","input":null""")]
    [Arguments("")]
    public async Task An_input_that_is_not_an_object_leaves_no_arguments(string input) {
        var call = ((AssistantToolCallsGenerated)E(Line("assistant", $$"""{"type":"tool_use","name":"Shell"{{input}}}""")).Single().Payload).ToolCalls.Single();

        await Assert.That(call.Arguments).IsNull();
        await Assert.That(call.ToolName).IsEqualTo("Shell");
    }

    [Test]
    public async Task A_tool_use_without_a_name_has_an_empty_one() {
        var call = ((AssistantToolCallsGenerated)E(Line("assistant", """{"type":"tool_use","input":{}}""")).Single().Payload).ToolCalls.Single();

        await Assert.That(call.ToolName).IsEqualTo("");
        await Assert.That(call.ToolKind).IsEqualTo(AcpToolKind.Other);
    }

    [Test]
    [Arguments("Read", AcpToolKind.Read)]
    [Arguments("Write", AcpToolKind.Edit)]
    [Arguments("StrReplace", AcpToolKind.Edit)]
    [Arguments("Delete", AcpToolKind.Delete)]
    [Arguments("Shell", AcpToolKind.Execute)]
    [Arguments("Grep", AcpToolKind.Search)]
    [Arguments("WebFetch", AcpToolKind.Fetch)]
    [Arguments("Task", AcpToolKind.Other)]
    [Arguments("mcp_kcap_search", AcpToolKind.Other)]
    public async Task A_tool_call_is_classified_by_its_name(string name, string kind) {
        var call = ((AssistantToolCallsGenerated)E(Line("assistant", $$$"""{"type":"tool_use","name":"{{{name}}}","input":{}}""")).Single().Payload).ToolCalls.Single();

        await Assert.That(call.ToolKind).IsEqualTo(kind);
    }

    // ── Ids ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A subagent's transcript numbers its lines from zero too, so its ids are scoped by its agent id.</summary>
    [Test]
    public async Task A_subagent_stream_scopes_its_ids_apart_from_the_parents() {
        var line = Line("assistant", Text("hi"));

        var parent = E(line, Context()).Single();
        var child  = E(line, Context(Session, "child")).Single();

        await Assert.That(child.EventId).IsNotEqualTo(parent.EventId);
        await Assert.That(child.EventId).IsEqualTo(TranscriptIds.CursorBlock($"{Session}:child", 4, "assistant_text", 0, CanonicalEventTypes.AssistantTextGenerated));
    }

    [Test]
    public async Task The_same_line_projects_to_the_same_ids_whatever_came_before() {
        var ctx  = Context();
        var line = Line("assistant", Text("a"), """{"type":"tool_use","name":"Read","input":{}}""");

        var first = E(line, ctx).Select(e => e.EventId).ToArray();
        E(User("between"), ctx);
        ctx.BeginBatch();

        await Assert.That(E(line, ctx).Select(e => e.EventId)).IsEquivalentTo(first, CollectionOrdering.Matching);
    }

    // ── Ignored and rejected lines ─────────────────────────────────────────────────────────────

    [Test]
    [Arguments("""{"type":"turn_ended","status":"success"}""")]
    [Arguments("""{"role":"system","message":{"content":[{"type":"text","text":"x"}]}}""")]
    [Arguments("""{"role":"assistant","message":{"content":[]}}""")]
    [Arguments("""{"role":"assistant","message":{"content":"not blocks"}}""")]
    [Arguments("""{"role":"assistant"}""")]
    [Arguments("""{"role":"assistant","message":{"content":[{"type":"thinking","thinking":"x"}]}}""")]
    [Arguments("""{"role":"user","message":{"content":[{"type":"image","source":{}}]}}""")]
    public async Task A_line_with_nothing_to_show_emits_nothing(string line) {
        var result = P(line);

        await Assert.That(result.Rejected).IsNull();
        await Assert.That(result.Events).IsEmpty();
    }

    /// <summary>A wrong-typed field reads as absent: the line or block is ignored rather than failed.</summary>
    [Test]
    [Arguments("""{"role":7,"message":{"content":[{"type":"text","text":"x"}]}}""")]
    [Arguments("""{"role":"assistant","message":"x"}""")]
    [Arguments("""{"role":"assistant","message":{"content":["x",{"type":7,"text":"y"},{"type":"text","text":7}]}}""")]
    public async Task A_wrong_typed_field_is_ignored(string line) {
        var result = P(line);

        await Assert.That(result.Rejected).IsNull();
        await Assert.That(result.Events).IsEmpty();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("{not json")]
    [Arguments("[1,2]")]
    [Arguments("\"text\"")]
    public async Task A_line_that_is_not_a_json_object_rejects(string line) {
        await Assert.That(P(line).Rejected).IsNotNull();
    }

    /// <summary>The server throws on such a line; every id needs the scope.</summary>
    [Test]
    public async Task A_stream_without_a_session_id_rejects_a_line_with_content() {
        await Assert.That(P(User("hi"), Context("")).Rejected).IsNotNull();
        await Assert.That(P("""{"type":"turn_ended"}""", Context("")).Rejected).IsNull();
    }
}
