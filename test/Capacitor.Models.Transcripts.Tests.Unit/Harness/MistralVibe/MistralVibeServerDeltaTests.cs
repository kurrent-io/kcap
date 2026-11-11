using System.Globalization;
using Capacitor.Models.Transcripts.Harness.MistralVibe;
using Kurrent.Agent.Schema.Events;
using TUnit.Assertions.Enums;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.MistralVibe;

/// <summary>The inputs on which the server's Mistral Vibe normalizer and this projection gave different events,
/// each pinned to the behaviour kept: a call carries its tool kind and the child it spawned, and an effect's
/// result follows its call in source order at the call's own time, on the ids the server's baseline holds.</summary>
public class MistralVibeServerDeltaTests {
    static readonly DateTimeOffset Received = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

    static IReadOnlyList<CanonicalEvent> E(string line) =>
        MistralVibeTranscriptEvents.Instance.Project(line, 7, Received, MistralVibeTranscriptEvents.Instance.CreateContext("sess", null)).Events;

    const string Spawn = """{"type":"effect","id":"effect-s","createdAt":1,"generationStatus":"completed","detail":{"kind":"subagent","toolName":"subagent.spawn","childSessionId":"child-1D89390ca0ec23cdd4c58954","input":{"task":"read hello.py","agent":"explore"}},"state":{"status":"completed","outputText":""}}""";

    /// <summary>The server names the child and no kind; the projection named the kind and no child.</summary>
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
}
