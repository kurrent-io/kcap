using System.IO.Hashing;
using System.Text;
using Capacitor.Models.Transcripts.Harness.MistralVibe;
using Kurrent.Agent.Schema.Events;
using TUnit.Assertions.Enums;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.MistralVibe;

/// <summary>The inputs on which the server's Mistral Vibe normalizer and this projection gave different events,
/// each pinned to the behaviour kept: a call carries its tool kind and the child it spawned, and an effect's
/// result follows its call in source order at the call's own time, on the ids the server stamps.</summary>
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

    static Guid ServerSibling(Guid primary, string suffix) {
        var input = new byte[16 + Encoding.UTF8.GetByteCount(suffix)];
        primary.TryWriteBytes(input);
        Encoding.UTF8.GetBytes(suffix).CopyTo(input, 16);
        return new Guid(XxHash128.Hash(input));
    }

    /// <summary>The server stamps the result one tick after its call to keep the pair in order; here the
    /// result shares the call's time and follows it in the returned list, and both ids are the server's:
    /// the entry id under its <c>vibe-entry:</c> prefix, and that id's <c>result</c> sibling.</summary>
    [Test]
    public async Task An_effects_result_follows_its_call_at_the_calls_time_on_the_servers_ids() {
        var e = E(Spawn);

        await Assert.That(e.Select(x => x.EventType)).IsEquivalentTo(
            [CanonicalEventTypes.AssistantToolCallsGenerated, CanonicalEventTypes.ToolResultReceived], CollectionOrdering.Matching);
        await Assert.That(e[1].Timestamp).IsEqualTo(e[0].Timestamp);
        await Assert.That(((ToolResultReceived)e[1].Payload).Timestamp).IsEqualTo(((AssistantToolCallsGenerated)e[0].Payload).Timestamp);

        var serverCallId = new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes("vibe-entry:effect-s")));
        await Assert.That(e[0].EventId).IsEqualTo(serverCallId);
        await Assert.That(e[1].EventId).IsEqualTo(ServerSibling(serverCallId, "result"));
    }
}
