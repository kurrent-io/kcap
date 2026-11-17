using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.Copilot;
using Google.Protobuf;
using TUnit.Assertions.Enums;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Copilot;

/// <summary>kcap-server corpus sessions (copied as <c>Fixtures/&lt;id&gt;.jsonl</c>) against what its Copilot normalizer
/// stored for them in the <c>v1</c> golden baseline: the same events on the same ids at the same times, and the same
/// attachment blobs.</summary>
public class CopilotServerDeltaTests {
    static readonly DateTimeOffset Received = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The fork's session-stream events in the baseline: type, id, payload time and <c>$timestamp</c>. A
    /// backfill or metrics payload carries no time of its own.</summary>
    static readonly (string Type, string Id, string? PayloadTime, string RecordTime)[] ForkBaseline = [
        (CanonicalEventTypes.SessionStarted,         "3b18312a-ff14-40d2-be84-1aa53e5ef921", "2026-11-05T03:44:15.084Z", "2026-11-05T03:44:15.084Z"),
        (CanonicalEventTypes.SessionStarted,         "3a6d8623-62ab-4845-a98a-7c31781edd4f", "2026-11-05T03:44:15.087Z", "2026-11-05T03:44:15.087Z"),
        (CanonicalEventTypes.UserMessageReceived,    "ba64d21e-a0fc-4823-b2ae-ba01d2aba105", "2026-11-05T03:44:18.573Z", "2026-11-05T03:44:18.573Z"),
        (CanonicalEventTypes.UserMessageReceived,    "2e60fa52-7fae-4c94-8b07-986558a6af40", "2026-11-05T03:56:51.094Z", "2026-11-05T03:56:51.094Z"),
        (CanonicalEventTypes.CopilotUsageBackfilled, "3b4b3842-2edd-4d17-8f86-2abda177f0a6", null,                       "2026-11-05T03:56:53.006Z"),
        (CanonicalEventTypes.AssistantTextGenerated, "027e339f-c500-dc3d-bb18-e53caee0288f", "2026-11-05T03:56:53.019Z", "2026-11-05T03:56:53.019Z"),
        (CanonicalEventTypes.CopilotSessionMetrics,  "ade49d17-a835-499e-b285-b35923e136f9", null,                       "2026-11-05T03:56:53.240Z"),
        (CanonicalEventTypes.CopilotUsageBackfilled, "b0a77db6-3701-404a-8925-8124f3ca8a09", null,                       "2026-11-05T03:57:38.359Z"),
        (CanonicalEventTypes.CopilotSessionMetrics,  "3c3b7931-d9a1-5c14-7701-3395838383f9", null,                       "2026-11-05T03:57:38.359Z"),
    ];

    static IReadOnlyList<CanonicalEvent> ProjectCorpus(string fixture, string sessionId) {
        var lines = File.ReadAllLines(Path.Combine(SourceDir(), "Fixtures", fixture + ".jsonl"));
        var ctx   = CopilotTranscriptEvents.Instance.CreateContext(sessionId, null);

        return [.. lines.SelectMany((line, number) => CopilotTranscriptEvents.Instance.Project(line, number, Received, ctx).Events)];
    }

    [Test]
    public async Task The_fork_projects_to_the_baselines_events_ids_and_times() {
        var events = ProjectCorpus("3aa78294", "3aa7829478c54ea7bd0d97a6b61a8197");

        await Assert.That(events.Select(e => e.EventType)).IsEquivalentTo(ForkBaseline.Select(b => b.Type), CollectionOrdering.Matching);
        await Assert.That(events.Select(e => e.EventId.ToString())).IsEquivalentTo(ForkBaseline.Select(b => b.Id), CollectionOrdering.Matching);
        await Assert.That(events.Select(e => e.RecordTimestamp)).IsEquivalentTo(ForkBaseline.Select(b => (string?)b.RecordTime), CollectionOrdering.Matching);
        await Assert.That(events.Select(e => PayloadTime(e.Payload))).IsEquivalentTo(ForkBaseline.Select(b => b.PayloadTime), CollectionOrdering.Matching);
    }

    /// <summary>The server names an attachment's blob stream by its id and the baseline digests the blob's bytes.</summary>
    [Test]
    public async Task The_attachments_are_the_baselines_blobs() {
        var prompt = ProjectCorpus("a4bbf204", "a4bbf204240d42d4950333d0e3c9da90").Single(e => e.Attachments is not null);

        await Assert.That(prompt.Attachments!.Select(a => ($"attachment-{a.Id:N}", a.FileName, a.ContentType, Convert.ToHexStringLower(SHA256.HashData(a.Data)))))
            .IsEquivalentTo(new[] {
                ("attachment-ae7a9925a70bed3378a45d999b62b92a", "red.png", "image/png", "ca96adb50da6a5f6cbfe03c9c5eb8ad8f1a6c88f526b8a4b9d9d7d3b9ef90157"),
                ("attachment-f703b7d9731c0695b3465386f0485857", "doc.pdf", "application/pdf", "1ddc8299e0e4f3fa3338cada3029b5b64be709ab9d3d37f83ba8754809b6c7a4"),
            }, CollectionOrdering.Matching);
    }

    static string? PayloadTime(object payload) =>
        payload is IMessage message ? (string?)JsonNode.Parse(JsonFormatter.Default.Format(message))?["timestamp"] : null;

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
