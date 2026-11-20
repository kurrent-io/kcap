using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.Cursor;
using Google.Protobuf;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Cursor;

/// <summary>Three of kcap-server's Cursor corpus sessions, copied verbatim, against what its normalizer stored for
/// them in the <c>v1</c> golden baseline, copied beside each as <c>.server.jsonl</c>: the same events on the same
/// ids, with the same payloads and line metadata. Two differences are kept, a tool kind on each tool call and the
/// receive time where the server read its own clock, which the baseline masks as <c>(wall clock)</c>. The
/// <c>capacitor_turn</c> slug is the pipeline's, not the normalizer's, and is left out.</summary>
public class CursorServerDeltaTests {
    static readonly DateTimeOffset Received = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    const string ReceivedJson = "2026-11-15T10:00:00Z";
    const string WallClock    = "(wall clock)";

    static readonly JsonFormatter Formatter = new(JsonFormatter.Settings.Default.WithPreserveProtoFieldNames(true));

    [Test]
    [Arguments("cursor-skill-probe", "c0cab74dcfb64bcbbeb8e86ad0ce5281", null)]
    [Arguments("cursor-skill-probe-two-turns", "2eef1089a2b748f1b84f88bc27ae98cd", null)]
    [Arguments("cursor-tool-results", "cc000000000040008000000000000001", "cc0000000000400080000000000000c1")]
    public async Task The_corpus_projects_to_the_baselines_events(string fixture, string sessionId, string? agentId) {
        var dir      = Path.Combine(SourceDir(), "Fixtures");
        var expected = (await File.ReadAllLinesAsync(Path.Combine(dir, fixture + ".server.jsonl"))).Select(l => Canonical(Expected(JsonNode.Parse(l)!.AsObject())));
        var actual   = new List<string>();

        actual.AddRange(await ProjectAsync(Path.Combine(dir, fixture + ".jsonl"), sessionId, null, "session"));
        if (agentId is not null)
            actual.AddRange(await ProjectAsync(Path.Combine(dir, $"{fixture}.{agentId}.jsonl"), sessionId, agentId, "agent/" + agentId));

        await Assert.That(string.Join('\n', actual)).IsEqualTo(string.Join('\n', expected));
    }

    /// <summary>Numbered from 0 with blank lines counted, as the recorder numbers them; the server skips a blank line
    /// before its normalizer sees it.</summary>
    static async Task<IEnumerable<string>> ProjectAsync(string path, string sessionId, string? agentId, string stream) {
        var ctx = CursorTranscriptEvents.Instance.CreateContext(sessionId, agentId);

        return (await File.ReadAllLinesAsync(path))
            .SelectMany((line, number) => string.IsNullOrWhiteSpace(line) ? [] : CursorTranscriptEvents.Instance.Project(line, number, Received, ctx).Events)
            .Select(e => Canonical(Actual(e, stream)))
            .ToArray();
    }

    static JsonObject Actual(CanonicalEvent e, string stream) {
        var payload = JsonNode.Parse(Formatter.Format((IMessage)e.Payload))!.AsObject();
        foreach (var call in payload["tool_calls"]?.AsArray() ?? []) call!.AsObject().Remove("tool_kind");

        return new JsonObject {
            ["stream"]   = stream,
            ["type"]     = e.EventType,
            ["id"]       = e.EventId.ToString(),
            ["metadata"] = new JsonObject([.. e.VendorMetadata!.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))]),
            ["payload"]  = payload,
        };
    }

    /// <summary>The baseline line less what the caller writes, <c>$lineNumber</c> and <c>$vendor</c>, and less the
    /// pipeline's slug, with the server's clock reads replaced by the receive time.</summary>
    static JsonObject Expected(JsonObject stored) {
        var metadata = stored["metadata"]!.AsObject();
        metadata.Remove("$lineNumber");
        metadata.Remove("$vendor");

        var payload = stored["payload"]!.AsObject();
        if (payload["extensions"] is JsonObject extensions) {
            extensions.Remove("capacitor_turn");
            if (extensions.Count == 0) payload.Remove("extensions");
        }

        foreach (var key in new[] { "created_at", "timestamp" })
            if ((string?)payload[key] == WallClock) payload[key] = ReceivedJson;

        return stored;
    }

    /// <summary>Keys in ordinal order, so two renderings of one event compare as text.</summary>
    static string Canonical(JsonNode node) => Sorted(node)!.ToJsonString();

    static JsonNode? Sorted(JsonNode? node) => node switch {
        JsonObject obj => new JsonObject(obj.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, Sorted(kv.Value)))),
        JsonArray arr  => new JsonArray([.. arr.Select(Sorted)]),
        _              => node?.DeepClone(),
    };

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
