using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.MuseAssistant;
using Google.Protobuf;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.MuseAssistant;

/// <summary>kcap-server's corpus chat (<c>test/data/muse-assistant/chat.jsonl</c>, copied as <c>chat.jsonl</c>) against
/// what its Muse Assistant normalizer stored for it in the <c>v1</c> golden baseline (copied as
/// <c>chat.baseline.jsonl</c>): the same events in the same order, on the same ids, with the same payloads and
/// <c>$timestamp</c>. A prompt's <c>capacitor_turn</c> block is written by the server's pipeline, not by a
/// normalizer, so it is the one thing taken out of the baseline before comparing.</summary>
public class MuseAssistantServerDeltaTests {
    /// <summary>The session id the baseline was recorded under, in the server's canonical form.</summary>
    const string Session = "66c0235eaa090568f04da9bf3d2675f2";

    static readonly DateTimeOffset Received = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task The_corpus_projects_to_the_baselines_events() {
        var dir      = Path.Combine(SourceDir(), "Fixtures");
        var lines    = await File.ReadAllLinesAsync(Path.Combine(dir, "chat.jsonl"));
        var baseline = (await File.ReadAllLinesAsync(Path.Combine(dir, "chat.baseline.jsonl"))).Select(l => JsonNode.Parse(l)!.AsObject()).ToArray();
        var ctx      = MuseAssistantTranscriptEvents.Instance.CreateContext(Session, null);
        var events   = lines.SelectMany((line, number) => MuseAssistantTranscriptEvents.Instance.Project(line, number, Received, ctx).Events).ToArray();

        await Assert.That(events.Length).IsEqualTo(baseline.Length);
        for (var i = 0; i < events.Length; i++) {
            await Assert.That(events[i].EventType).IsEqualTo((string?)baseline[i]["type"]);
            await Assert.That(events[i].EventId.ToString()).IsEqualTo((string?)baseline[i]["id"]);
            await Assert.That(events[i].RecordTimestamp).IsEqualTo((string?)baseline[i]["metadata"]!["$timestamp"]);
            await Assert.That(JsonNode.Parse(JsonFormatter.Default.Format((IMessage)events[i].Payload))!.ToJsonString()).IsEqualTo(NormalizerPayload(baseline[i]));
        }
    }

    static string NormalizerPayload(JsonObject stored) {
        var payload = stored["payload"]!.DeepClone().AsObject();
        if (payload["extensions"] is JsonObject extensions) {
            extensions.Remove("capacitor_turn");
            if (extensions.Count == 0) payload.Remove("extensions");
        }

        return payload.ToJsonString();
    }

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
