using Capacitor.Models.Transcripts.Harness.Claude;
using Kurrent.Agent.Schema.Events;
using TUnit.Assertions.Enums;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Claude;

/// <summary>The inputs on which the server's Claude normalizer and this projection gave different events,
/// each pinned to the behaviour kept: whatever either side recorded, with the ids the server stamps.</summary>
public class ClaudeServerDeltaTests {
    static readonly DateTimeOffset Received = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    const string Record    = "a1b2c3d4-0000-4000-8000-000000000001";
    const string Cancelled = "a1b2c3d4-0000-4000-8000-0000000000c1";
    const string Resubmit  = "a1b2c3d4-0000-4000-8000-0000000000c2";
    const string Parent    = "3944ba5e-0000-0000-0000-000000000000";

    static TranscriptContext Ctx() => ClaudeTranscriptEvents.Instance.CreateContext("sess", null);

    static IReadOnlyList<CanonicalEvent> E(string line, TranscriptContext? ctx = null) =>
        ClaudeTranscriptEvents.Instance.Project(line, 1, Received, ctx ?? Ctx()).Events;

    /// <summary>The server reads <c>content[0]</c> only.</summary>
    [Test]
    public async Task Each_assistant_content_block_is_its_own_event_and_the_first_keeps_the_record_id() {
        var e = E($$$"""{"type":"assistant","uuid":"{{{Record}}}","message":{"content":[{"type":"text","text":"a"},{"type":"thinking","thinking":"t"},{"type":"tool_use","id":"toolu_1","name":"Read","input":{}}]}}""");

        await Assert.That(e.Select(x => x.EventType)).IsEquivalentTo([
            CanonicalEventTypes.AssistantTextGenerated, CanonicalEventTypes.AssistantThinkingGenerated, CanonicalEventTypes.AssistantToolCallsGenerated,
        ], CollectionOrdering.Matching);
        await Assert.That(e[0].EventId).IsEqualTo(Guid.Parse(Record));
        await Assert.That(e[1].EventId).IsEqualTo(TranscriptIds.ClaudeBlock(Guid.Parse(Record), 1));
        await Assert.That(e[2].EventId).IsEqualTo(TranscriptIds.ClaudeBlock(Guid.Parse(Record), 2));
    }

    /// <summary>The server returns after the first <c>tool_result</c>.</summary>
    [Test]
    public async Task Every_tool_result_in_a_line_is_its_own_event() {
        var e = E($$$"""{"type":"user","uuid":"{{{Record}}}","message":{"content":[{"type":"tool_result","tool_use_id":"a","content":"1"},{"type":"tool_result","tool_use_id":"b","content":"2"}]}}""");

        await Assert.That(e.Select(x => ((ToolResultReceived)x.Payload).CallId)).IsEquivalentTo(["a", "b"], CollectionOrdering.Matching);
        await Assert.That(e[0].EventId).IsEqualTo(Guid.Parse(Record));
        await Assert.That(e[1].EventId).IsEqualTo(TranscriptIds.ClaudeBlock(Guid.Parse(Record), 1));
    }

    static string Boundary(string metadata, bool sidechain = false) =>
        $$$"""{"parentUuid":null,"logicalParentUuid":"{{{Parent}}}","isSidechain":{{{(sidechain ? "true" : "false")}}},"type":"system","subtype":"compact_boundary","content":"Conversation compacted","isMeta":false,"timestamp":"2026-02-16T19:05:54.853Z","uuid":"{{{Record}}}","level":"info","compactMetadata":""" + metadata + "}";

    [Test]
    public async Task A_compaction_boundary_is_a_compaction_on_the_record_id_with_its_counts() {
        var e = E(Boundary("""{"trigger":"auto","preTokens":225521,"postTokens":37040}"""));

        await Assert.That(e).Count().IsEqualTo(1);
        await Assert.That(e[0].EventType).IsEqualTo(CanonicalEventTypes.ContextCompacted);
        await Assert.That(e[0].EventId).IsEqualTo(Guid.Parse(Record));
        await Assert.That(e[0].RecordTimestamp).IsEqualTo("2026-02-16T19:05:54.853Z");
        var compacted = (ContextCompactedEvent)e[0].Payload;
        await Assert.That(compacted.PreTokens).IsEqualTo(225_521L);
        await Assert.That(compacted.PostTokens).IsEqualTo(37_040L);
        await Assert.That(compacted.Trigger).IsEqualTo("auto");
        await Assert.That(compacted.ReplacementHistory).IsNull();
    }

    [Test]
    public async Task A_boundary_without_post_tokens_still_records_the_compaction() {
        var compacted = (ContextCompactedEvent)E(Boundary("""{"trigger":"manual","preTokens":167005}"""))[0].Payload;

        await Assert.That(compacted.PreTokens).IsEqualTo(167_005L);
        await Assert.That(compacted.PostTokens).IsNull();
        await Assert.That(compacted.Trigger).IsEqualTo("manual");
    }

    [Test]
    public async Task A_sidechain_boundary_or_one_without_metadata_projects_nothing() {
        await Assert.That(E(Boundary("""{"trigger":"auto","preTokens":1}""", sidechain: true))).IsEmpty();
        await Assert.That(E(Boundary("null"))).IsEmpty();
    }

    static string Prompt(string uuid, string? parent, string content = "prompt", bool meta = false) {
        var parentField = parent is null ? "" : $"\"parentUuid\":\"{parent}\",";
        var metaField   = meta ? "\"isMeta\":true," : "";
        return $$$"""{"type":"user","uuid":"{{{uuid}}}",{{{parentField}}}{{{metaField}}}"timestamp":"2026-09-04T10:00:08Z","message":{"role":"user","content":"{{{content}}}"}}""";
    }

    static string Answer(string parent) =>
        $$$"""{"type":"assistant","uuid":"a1b2c3d4-0000-4000-8000-0000000000a1","parentUuid":"{{{parent}}}","message":{"content":[{"type":"text","text":"ok"}]}}""";

    [Test]
    public async Task A_sibling_of_an_unanswered_prompt_retracts_it_ahead_of_itself() {
        var ctx = Ctx();
        E(Prompt(Cancelled, Parent), ctx);
        var e = E(Prompt(Resubmit, Parent), ctx);

        await Assert.That(e.Select(x => x.EventType)).IsEquivalentTo([CanonicalEventTypes.UserMessageRetracted, CanonicalEventTypes.UserMessageReceived], CollectionOrdering.Matching);
        await Assert.That(((UserMessageRetractedEvent)e[0].Payload).MessageId).IsEqualTo(Guid.Parse(Cancelled));
        await Assert.That(e[0].EventId).IsEqualTo(TranscriptIds.ClaudeRetraction(Guid.Parse(Cancelled)));
        // The server's retraction carries the prompt's record time and no cause.
        await Assert.That(e[0].RecordTimestamp).IsEqualTo("2026-09-04T10:00:08Z");
        await Assert.That(e[0].CausedBy).IsNull();
        await Assert.That(e[1].EventId).IsEqualTo(Guid.Parse(Resubmit));
    }

    [Test]
    public async Task Redelivered_lines_retract_the_same_prompt_once_more_under_the_same_id() {
        var ctx = Ctx();
        E(Prompt(Cancelled, Parent), ctx);
        var first = E(Prompt(Resubmit, Parent), ctx)[0];

        await Assert.That(E(Prompt(Cancelled, Parent), ctx)).Count().IsEqualTo(1);
        var again = E(Prompt(Resubmit, Parent), ctx);
        await Assert.That(again).Count().IsEqualTo(2);
        await Assert.That(again[0].EventId).IsEqualTo(first.EventId);
    }

    [Test]
    public async Task A_prompt_an_assistant_record_followed_is_never_retracted() {
        var ctx = Ctx();
        E(Prompt(Cancelled, Parent), ctx);
        E(Answer(Cancelled), ctx);

        await Assert.That(E(Prompt(Resubmit, Parent), ctx)).Count().IsEqualTo(1);
    }

    /// <summary>The answer was seen once, so a later copy of the prompt it answered cannot reopen it for a
    /// sibling to cancel.</summary>
    [Test]
    public async Task A_redelivered_answered_prompt_is_never_retracted() {
        var ctx = Ctx();
        E(Prompt(Cancelled, Parent), ctx);
        E(Answer(Cancelled), ctx);
        await Assert.That(E(Prompt(Cancelled, Parent), ctx)).Count().IsEqualTo(1);

        await Assert.That(E(Prompt(Resubmit, Parent), ctx)).Count().IsEqualTo(1);
    }

    /// <summary>An answer often reaches its prompt through the records between them, a system record the
    /// projection otherwise ignores among them.</summary>
    [Test]
    public async Task An_answer_that_descends_from_its_prompt_through_other_records_answers_it() {
        const string between = "a1b2c3d4-0000-4000-8000-0000000000b1";
        const string meta    = "a1b2c3d4-0000-4000-8000-0000000000b2";
        var ctx = Ctx();
        E(Prompt(Cancelled, Parent), ctx);
        E($$$"""{"type":"system","subtype":"informational","uuid":"{{{between}}}","parentUuid":"{{{Cancelled}}}","content":"x"}""", ctx);
        E(Prompt(meta, between, meta: true), ctx);
        E(Answer(meta), ctx);
        E(Prompt(Cancelled, Parent), ctx);

        await Assert.That(E(Prompt(Resubmit, Parent), ctx)).Count().IsEqualTo(1);
    }

    /// <summary>A copy of the open prompt arriving between it and its answer leaves what descends from it known.</summary>
    [Test]
    public async Task A_redelivered_open_prompt_keeps_its_descendants() {
        const string between = "a1b2c3d4-0000-4000-8000-0000000000b1";
        var ctx = Ctx();
        E(Prompt(Cancelled, Parent), ctx);
        E($$$"""{"type":"system","subtype":"informational","uuid":"{{{between}}}","parentUuid":"{{{Cancelled}}}","content":"x"}""", ctx);
        E(Prompt(Cancelled, Parent), ctx);
        E(Answer(between), ctx);
        E(Prompt(Cancelled, Parent), ctx);

        await Assert.That(E(Prompt(Resubmit, Parent), ctx)).Count().IsEqualTo(1);
    }

    /// <summary>A re-delivered answer to another prompt closes the open one, as the server's does, but does not
    /// make it answered: that prompt's own re-delivery reopens it, and its sibling then retracts it.</summary>
    [Test]
    public async Task A_redelivered_answer_to_another_prompt_does_not_answer_the_open_one() {
        const string earlier = "a1b2c3d4-0000-4000-8000-0000000000e1";
        const string other   = "8b1f0000-0000-0000-0000-000000000000";
        var ctx = Ctx();
        E(Prompt(earlier, Parent), ctx);
        E(Answer(earlier), ctx);
        E(Prompt(Cancelled, other), ctx);
        E(Answer(earlier), ctx);
        E(Prompt(Cancelled, other), ctx);

        var e = E(Prompt(Resubmit, other), ctx);
        await Assert.That(e).Count().IsEqualTo(2);
        await Assert.That(((UserMessageRetractedEvent)e[0].Payload).MessageId).IsEqualTo(Guid.Parse(Cancelled));
    }

    [Test]
    public async Task A_child_of_the_unanswered_prompt_retracts_nothing() {
        var ctx = Ctx();
        E(Prompt(Cancelled, Parent), ctx);

        await Assert.That(E(Prompt(Resubmit, Cancelled), ctx)).Count().IsEqualTo(1);
    }

    /// <summary>Neither a harness payload, a meta record nor a parentless prompt is a prompt that can be cancelled or
    /// cancel one.</summary>
    [Test]
    public async Task Harness_payloads_meta_records_and_parentless_prompts_take_no_part() {
        const string notification = "<task-notification> <status>completed</status> <summary>done</summary> </task-notification>";
        var ctx = Ctx();
        E(Prompt("a1b2c3d4-0000-4000-8000-0000000000d1", Parent, notification), ctx);
        await Assert.That(E(Prompt(Cancelled, Parent), ctx)).Count().IsEqualTo(1);
        await Assert.That(E(Prompt("a1b2c3d4-0000-4000-8000-0000000000d2", Parent, notification), ctx)).Count().IsEqualTo(1);
        await Assert.That(E(Prompt("a1b2c3d4-0000-4000-8000-0000000000d3", Parent, meta: true), ctx)).Count().IsEqualTo(1);

        var parentless = Ctx();
        E(Prompt(Cancelled, null), parentless);
        await Assert.That(E(Prompt(Resubmit, null), parentless)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task A_retraction_reads_back_from_its_stored_json() {
        var parsed = CanonicalEventJson.TryParse(CanonicalEventTypes.UserMessageRetracted, $$$"""{"message_id":"{{{Cancelled}}}"}""");

        await Assert.That(parsed).IsTypeOf<UserMessageRetractedEvent>();
        await Assert.That(((UserMessageRetractedEvent)parsed!).MessageId).IsEqualTo(Guid.Parse(Cancelled));
    }
}
