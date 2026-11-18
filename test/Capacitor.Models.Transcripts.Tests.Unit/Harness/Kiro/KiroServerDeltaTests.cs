using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.Kiro;
using Google.Protobuf;
using Kurrent.Agent.Schema.Events;
using TUnit.Assertions.Enums;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Kiro;

/// <summary>kcap-server's corpus session (<c>test/data/kiro/session.jsonl</c>, copied as <c>kiro-session.jsonl</c>)
/// against what its Kiro normalizer stored for it in the <c>v1</c> golden baseline: the same events on the same ids
/// at the same times, and one difference kept, a tool kind on each tool call.</summary>
public class KiroServerDeltaTests {
    /// <summary>The session id the baseline was recorded under, in the server's canonical form.</summary>
    const string Session = "5f6c2b1a9e3d4a7cbb121c2d3e4f5a6b";

    static readonly DateTimeOffset Received = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Per baseline event: its type, id, payload time and <c>$timestamp</c>.</summary>
    static readonly (string Type, string Id, string PayloadTime, string RecordTime)[] Baseline = [
        (CanonicalEventTypes.UserMessageReceived,         "e64903f7-a47d-58e2-89f8-632e007b4463", "2026-06-17T20:16:03Z",           "2026-06-17T20:16:03.0000000+00:00"),
        (CanonicalEventTypes.AssistantTextGenerated,      "9108b803-6f7b-556b-8b2e-f4441b7909ba", "2026-06-17T20:16:03Z",           "2026-06-17T20:16:03.0000000+00:00"),
        (CanonicalEventTypes.AssistantToolCallsGenerated, "754e0fc2-e63c-52cc-8c35-0a5a381a6481", "2026-06-17T20:16:03.000000100Z", "2026-06-17T20:16:03.0000000+00:00"),
        (CanonicalEventTypes.ToolResultReceived,          "81337a67-0f4c-587a-8df4-221190129359", "2026-06-17T20:16:03Z",           "2026-06-17T20:16:03.0000000+00:00"),
        (CanonicalEventTypes.AssistantTextGenerated,      "fa95118c-6c08-5ffa-a6b2-91b7ac29fe6a", "2026-06-17T20:16:03Z",           "2026-06-17T20:16:03.0000000+00:00"),
    ];

    static async Task<IReadOnlyList<CanonicalEvent>> ProjectCorpusAsync() {
        var lines = await File.ReadAllLinesAsync(Path.Combine(SourceDir(), "Fixtures", "kiro-session.jsonl"));
        var ctx   = KiroTranscriptEvents.Instance.CreateContext(Session, null);

        return [.. lines.SelectMany((line, number) => KiroTranscriptEvents.Instance.Project(line, number, Received, ctx).Events)];
    }

    [Test]
    public async Task The_corpus_projects_to_the_baselines_events_ids_and_times() {
        var events = await ProjectCorpusAsync();

        await Assert.That(events.Select(e => e.EventType)).IsEquivalentTo(Baseline.Select(b => b.Type), CollectionOrdering.Matching);
        await Assert.That(events.Select(e => e.EventId.ToString())).IsEquivalentTo(Baseline.Select(b => b.Id), CollectionOrdering.Matching);
        await Assert.That(events.Select(e => e.RecordTimestamp)).IsEquivalentTo(Baseline.Select(b => (string?)b.RecordTime), CollectionOrdering.Matching);
        await Assert.That(events.Select(e => PayloadTime(e.Payload))).IsEquivalentTo(Baseline.Select(b => (string?)b.PayloadTime), CollectionOrdering.Matching);
    }

    [Test]
    public async Task The_corpus_tool_result_carries_the_baselines_kiro_slug() {
        var result = (await ProjectCorpusAsync()).Single(e => e.EventType == CanonicalEventTypes.ToolResultReceived);

        await Assert.That(JsonFormatter.Default.Format(SchemaExtensions.Slug(result.Payload, KiroExtension.Slug)!))
            .IsEqualTo("""{ "success": true, "tool_name": null, "status": "success" }""");
    }

    /// <summary>The server records no kind; the projection classifies the call, as it does for every vendor.</summary>
    [Test]
    public async Task The_corpus_tool_call_carries_a_tool_kind_the_baseline_lacks() {
        var calls = (AssistantToolCallsGenerated)(await ProjectCorpusAsync()).Single(e => e.EventType == CanonicalEventTypes.AssistantToolCallsGenerated).Payload;

        await Assert.That(calls.ToolCalls.Single().ToolKind).IsEqualTo(AcpToolKind.Execute);
    }

    /// <summary>The payload's time as the server stores it: the schema message's own JSON.</summary>
    static string? PayloadTime(object payload) => (string?)JsonNode.Parse(JsonFormatter.Default.Format((IMessage)payload))?["timestamp"];

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
