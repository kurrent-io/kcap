using System.Globalization;
using Capacitor.Models.Transcripts.Harness.MistralVibe;
using Kurrent.Agent.Schema.Events;
using TUnit.Assertions.Enums;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.MistralVibe;

/// <summary>Inputs pinned to the ids kcap-server's Mistral Vibe normalizer stores for them, and to the payload
/// differences kept on purpose: a call carries both its tool kind and the child it spawned, and an effect's result
/// and a legacy line's calls keep their line's time, in source order.</summary>
public class MistralVibeServerDeltaTests {
    static readonly DateTimeOffset Received = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

    static IReadOnlyList<CanonicalEvent> E(string line, int lineNumber = 7) =>
        MistralVibeTranscriptEvents.Instance.Project(line, lineNumber, Received, MistralVibeTranscriptEvents.Instance.CreateContext("sess", null)).Events;

    const string Spawn = """{"type":"effect","id":"effect-s","createdAt":1,"generationStatus":"completed","detail":{"kind":"subagent","toolName":"subagent.spawn","childSessionId":"child-1D89390ca0ec23cdd4c58954","input":{"task":"read hello.py","agent":"explore"}},"state":{"status":"completed","outputText":""}}""";

    /// <summary>The server stores the child and no tool kind; the projection carries both.</summary>
    [Test]
    public async Task A_spawn_call_carries_its_tool_kind_and_the_child_it_started() {
        var calls = (AssistantToolCallsGenerated)E(Spawn)[0].Payload;

        await Assert.That(calls.ToolCalls[0].ToolKind).IsEqualTo(AcpToolKind.Other);
        var slug = SchemaExtensions.Slug(calls, MistralVibeExtension.Slug)!;
        await Assert.That(slug.Fields[MistralVibeExtension.AgentId].StringValue).IsEqualTo("1d89390ca0ec23cdd4c58954");
    }

    [Test]
    [Arguments("child-")]
    [Arguments("child-ab-cd")]
    [Arguments("session-1d89")]
    public async Task A_call_naming_no_child_store_carries_no_child(string childSessionId) {
        var line  = Spawn.Replace("child-1D89390ca0ec23cdd4c58954", childSessionId, StringComparison.Ordinal);
        var calls = (AssistantToolCallsGenerated)E(line)[0].Payload;

        await Assert.That(SchemaExtensions.Slug(calls, MistralVibeExtension.Slug)).IsNull();
        await Assert.That(calls.ToolCalls[0].ToolKind).IsEqualTo(AcpToolKind.Other);
    }

    [Test]
    public async Task A_shell_effect_keeps_its_kind_and_names_no_child() {
        var calls = (AssistantToolCallsGenerated)E("""{"type":"effect","id":"effect-a1","createdAt":1000,"generationStatus":"completed","detail":{"kind":"shell","toolName":"file_system.bash","input":{"command":"ls"}},"state":{"status":"completed","outputText":"a.txt\n"}}""")[0].Payload;

        await Assert.That(calls.ToolCalls[0].ToolKind).IsEqualTo(AcpToolKind.Execute);
        await Assert.That(SchemaExtensions.Slug(calls, MistralVibeExtension.Slug)).IsNull();
    }

    /// <summary>A bash effect from kcap-server's corpus (<c>test/data/mistral-vibe/tool-turn.jsonl</c>), and what its
    /// normalizer stored for it in the <c>v1</c> golden baseline: the call and result ids below, and the result at
    /// <c>22:09:42.214000100Z</c>, one tick after the call.</summary>
    const string RecordedEffect = """{"createdAt":1791497382214,"detail":{"display":{"message":"file_system.bash","settledMessage":"file_system.bash","settledVerb":"Ran","statusText":"Running file_system.bash","summary":"file_system.bash","verb":"Running"},"input":{"command":"python3 hello.py"},"kind":"tool","toolName":"file_system.bash"},"generationStatus":"completed","id":"effect-71a7b78b-717b-5096-8df3-72b38473c111","inputEntryId":"ad2e89f7-f27b-4b15-83ac-589146a16892","relatedEntryId":null,"sessionId":"66f6af87-9047-d8f4-293b-de942752d13d","state":{"approvalSource":"bypass","approvalType":"always","decision":"execute","display":{"message":"file_system.bash","success":true},"output":{"_meta":{"approval":{"approvalSource":"bypass","approvalType":"always","decision":"execute"}},"content":[],"structured_content":{"command":"python3 hello.py","returncode":0,"stderr":"","stdout":"hello from vibe e2e\n","was_truncated":false},"type":"success"},"outputText":"","status":"completed"},"title":"file_system.bash","turnId":"turn-c304339c8931b5a618eebff1958e2c42","type":"effect","updatedAt":1791497382710}""";

    const string RecordedCallId   = "425faa72-c541-b4bc-b133-4bc36b9ca6e7";
    const string RecordedResultId = "5d8bd6d1-5edb-bac0-2cc7-a61ffdcbaf7b";

    /// <summary>The ids are the baseline's; the result follows its call in the returned list at the call's own
    /// time, where the baseline holds it a tick later.</summary>
    [Test]
    public async Task An_effects_result_follows_its_call_at_the_calls_time_on_the_ids_the_baseline_recorded() {
        var e = E(RecordedEffect);

        await Assert.That(e.Select(x => x.EventType)).IsEquivalentTo(
            [CanonicalEventTypes.AssistantToolCallsGenerated, CanonicalEventTypes.ToolResultReceived], CollectionOrdering.Matching);
        await Assert.That(e[0].EventId).IsEqualTo(Guid.Parse(RecordedCallId));
        await Assert.That(e[1].EventId).IsEqualTo(Guid.Parse(RecordedResultId));

        await Assert.That(e[0].Timestamp).IsEqualTo(DateTimeOffset.Parse("2026-10-08T22:09:42.214Z", CultureInfo.InvariantCulture));
        await Assert.That(e[1].Timestamp).IsEqualTo(e[0].Timestamp);
        await Assert.That(((ToolResultReceived)e[1].Payload).Timestamp).IsEqualTo(((AssistantToolCallsGenerated)e[0].Payload).Timestamp);
        await Assert.That(((ToolResultReceived)e[1].Payload).Result).IsEqualTo("hello from vibe e2e\n");
    }

    /// <summary>A legacy <c>messages.jsonl</c> assistant line, and the ids kcap-server's normalizer stores for it at
    /// line 2, which is what a session it already ingested holds.</summary>
    const string LegacyAssistant = """{"role":"assistant","content":"I'll read the file and run it.","tool_calls":[{"id":"call_read_1","index":0,"function":{"name":"read_file","arguments":"{\"path\":\"hello.py\"}"},"type":"function"},{"id":"call_bash_1","index":1,"function":{"name":"bash","arguments":"{\"command\":\"python3 hello.py\"}"},"type":"function"}]}""";

    /// <summary>The server stamps each call a tick later than the one before; here they keep the line's own time
    /// and source order carries the sequence.</summary>
    [Test]
    public async Task A_legacy_lines_text_and_each_call_take_the_ids_the_server_stores() {
        var e = E(LegacyAssistant, lineNumber: 2);

        await Assert.That(e.Select(x => x.EventType)).IsEquivalentTo(
            [CanonicalEventTypes.AssistantTextGenerated, CanonicalEventTypes.AssistantToolCallsGenerated, CanonicalEventTypes.AssistantToolCallsGenerated],
            CollectionOrdering.Matching);
        await Assert.That(e[0].EventId).IsEqualTo(Guid.Parse("68eba261-380e-1be3-3af1-c4f9279bc176"));
        await Assert.That(e[1].EventId).IsEqualTo(Guid.Parse("cbdce42a-69dc-9774-1e71-9507f6f511f6"));
        await Assert.That(((AssistantToolCallsGenerated)e[1].Payload).ToolCalls.Single().CallId).IsEqualTo("call_read_1");
        await Assert.That(e[2].EventId).IsEqualTo(Guid.Parse("9431b498-16b3-2b33-f2fd-c3435af24ad7"));
        await Assert.That(((AssistantToolCallsGenerated)e[2].Payload).ToolCalls.Single().CallId).IsEqualTo("call_bash_1");
        await Assert.That(e.Select(x => x.Timestamp).Distinct().Count()).IsEqualTo(1);
    }

    [Test]
    [Arguments(6, "c2a16a43-241f-2352-b5fa-6fe54783188d")]
    [Arguments(10, "97344d36-a271-7e07-1c2b-058010f71586")]
    public async Task A_repeated_legacy_message_takes_the_id_the_server_stores_for_its_line(int lineNumber, string id) {
        await Assert.That(E("""{"role":"user","content":"yes"}""", lineNumber).Single().EventId).IsEqualTo(Guid.Parse(id));
    }
}
