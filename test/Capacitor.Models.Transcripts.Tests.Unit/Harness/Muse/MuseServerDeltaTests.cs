using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.Muse;
using Google.Protobuf;
using Kurrent.Agent.Schema.Events;
using TUnit.Assertions.Enums;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Muse;

/// <summary>kcap-server's Muse corpus logs against what its Muse normalizer stored for them in the <c>v1</c> golden
/// baseline, copied beside each log as <c>*.server-v1.jsonl</c>: every event on the same id with the same payload and
/// metadata, written here as the server's pipeline writes them around a library event. The pipeline's own
/// <c>capacitor_turn</c> block on a prompt is left out of the baseline, and one difference is kept: a tool kind on
/// each tool call.</summary>
public class MuseServerDeltaTests {
    static readonly DateTimeOffset Received = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    static readonly JsonFormatter  Schema   = new(JsonFormatter.Settings.Default.WithPreserveProtoFieldNames(true));

    [Test]
    [Arguments("muse-tool-turn")]
    [Arguments("muse-failed-tool")]
    [Arguments("muse-reminder-link")]
    [Arguments("muse-failed-run")]
    public async Task The_corpus_projects_to_the_baselines_events(string fixture) {
        var events   = await ProjectAsync(fixture);
        var baseline = (await File.ReadAllLinesAsync(Path.Combine(SourceDir(), "Fixtures", fixture + ".server-v1.jsonl")))
            .Where(l => l.Length > 0)
            .Select(l => Canonical(WithoutTurnClassification(JsonNode.Parse(l)!)))
            .ToArray();

        await Assert.That(events.Select(e => Canonical(WithoutToolKinds(Stored(e.Event, e.Line)))))
            .IsEquivalentTo(baseline, CollectionOrdering.Matching);
    }

    /// <summary>The server records no kind; the projection classifies each call, as it does for every vendor.</summary>
    [Test]
    public async Task The_corpus_tool_calls_carry_a_tool_kind_the_baseline_lacks() {
        var calls = (await ProjectAsync("muse-tool-turn")).Concat(await ProjectAsync("muse-failed-tool"))
            .Select(e => e.Event.Payload)
            .OfType<AssistantToolCallsGenerated>()
            .SelectMany(c => c.ToolCalls)
            .Select(c => (c.ToolName, c.ToolKind));

        await Assert.That(calls).IsEquivalentTo([("bash", AcpToolKind.Execute), ("edit_file", AcpToolKind.Edit)], CollectionOrdering.Matching);
    }

    static async Task<List<(CanonicalEvent Event, int Line)>> ProjectAsync(string fixture) {
        var lines = await File.ReadAllLinesAsync(Path.Combine(SourceDir(), "Fixtures", fixture + ".jsonl"));
        var ctx   = MuseTranscriptEvents.Instance.CreateContext("session", null);

        return [.. lines.SelectMany((line, number) => MuseTranscriptEvents.Instance.Project(line, number, Received, ctx).Events.Select(e => (e, number)))];
    }

    /// <summary>The event as the server's pipeline stores it: the metadata it writes around a library event, and the
    /// payload in the schema's JSON.</summary>
    static JsonObject Stored(CanonicalEvent e, int line) {
        var metadata = new JsonObject { ["$lineNumber"] = line, ["$vendor"] = "muse" };
        if (e.RecordTimestamp is { } recorded) metadata["$timestamp"] = recorded;
        foreach (var (key, value) in e.VendorMetadata ?? new Dictionary<string, string>()) metadata[key] = value;
        foreach (var (key, value) in e.VendorMetadataValues ?? new Dictionary<string, object>())
            metadata[key] = value switch { long n => JsonValue.Create(n), bool b => JsonValue.Create(b), _ => throw new InvalidOperationException(key) };
        if (e.Usage is { } usage) metadata["$usage"] = JsonNode.Parse(Schema.Format(usage));

        return new JsonObject {
            ["stream"]   = "session",
            ["type"]     = e.EventType,
            ["id"]       = e.EventId.ToString(),
            ["metadata"] = metadata,
            ["payload"]  = e.Payload is IMessage message ? JsonNode.Parse(Schema.Format(message)) : new JsonObject(),
        };
    }

    static JsonNode WithoutTurnClassification(JsonNode stored) {
        if (stored["payload"]?["extensions"] is JsonObject extensions) {
            extensions.Remove("capacitor_turn");
            if (extensions.Count == 0) stored["payload"]!.AsObject().Remove("extensions");
        }

        return stored;
    }

    static JsonNode WithoutToolKinds(JsonNode stored) {
        if (stored["payload"]?["tool_calls"] is JsonArray calls)
            foreach (var call in calls) call!.AsObject().Remove("tool_kind");

        return stored;
    }

    /// <summary>Keys sorted at every level, so two stores of one event compare as text.</summary>
    static string Canonical(JsonNode node) => Sorted(node)!.ToJsonString();

    static JsonNode? Sorted(JsonNode? node) => node switch {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, Sorted(kv.Value)))),
        JsonArray a  => new JsonArray([.. a.Select(Sorted)]),
        _            => node?.DeepClone(),
    };

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
