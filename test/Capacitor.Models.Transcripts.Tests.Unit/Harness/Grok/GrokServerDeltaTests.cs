using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.Codex;
using Capacitor.Models.Transcripts.Harness.Grok;
using Google.Protobuf;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Grok;

/// <summary>kcap-server's corpus session and its subagent (<c>test/data/grok/</c>, copied as <c>grok-tool-turn.jsonl</c>
/// and <c>grok-subagent-child.jsonl</c>) against what its Grok normalizer stored for them in the <c>v1</c> golden
/// baseline (copied as <c>grok-tool-turn.baseline.jsonl</c>): every event on the same stream, id, line number and
/// <c>$timestamp</c>, with the same payload apart from the turn stamp the server's pipeline puts on a user message.</summary>
public class GrokServerDeltaTests {
    const string Session = "01b00000000070008000000000000001";
    const string Child   = "01b000000000700080000000000000c1";

    static readonly DateTimeOffset Received = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    static readonly JsonFormatter Proto = new(JsonFormatter.Settings.Default.WithPreserveProtoFieldNames(true));

    [Test]
    public async Task The_corpus_projects_to_the_baselines_events() {
        var expected = (await File.ReadAllLinesAsync(Fixture("grok-tool-turn.baseline.jsonl"))).Select(Baseline).ToArray();
        var actual   = (await Project("grok-tool-turn.jsonl", "session", null)).Concat(await Project("grok-subagent-child.jsonl", $"agent/{Child}", Child)).ToArray();

        await Assert.That(actual.Length).IsEqualTo(expected.Length);
        for (var i = 0; i < expected.Length; i++)
            await Assert.That(JsonNode.DeepEquals(actual[i], expected[i])).IsTrue().Because($"event {i}: {actual[i].ToJsonString()} against {expected[i].ToJsonString()}");
    }

    static async Task<IEnumerable<JsonObject>> Project(string fixture, string stream, string? agentId) {
        var lines = await File.ReadAllLinesAsync(Fixture(fixture));
        var ctx   = GrokTranscriptEvents.Instance.CreateContext(Session, agentId);

        return lines.SelectMany((line, number) => GrokTranscriptEvents.Instance.Project(line, number, Received, ctx).Events.Select(e => new JsonObject {
            ["stream"]    = stream,
            ["type"]      = e.EventType,
            ["id"]        = e.EventId.ToString(),
            ["line"]      = number,
            ["timestamp"] = e.RecordTimestamp,
            ["payload"]   = e.Payload switch {
                IMessage message                   => JsonNode.Parse(Proto.Format(message)),
                CodexUsageBackfilledEvent backfill => JsonSerializer.SerializeToNode(backfill),
                _                                  => throw new InvalidOperationException(e.EventType),
            },
        })).ToArray();
    }

    static JsonObject Baseline(string line) {
        var stored  = JsonNode.Parse(line)!.AsObject();
        var payload = stored["payload"]!.DeepClone().AsObject();

        if (payload["extensions"] is JsonObject extensions) {
            extensions.Remove("capacitor_turn");
            if (extensions.Count == 0) payload.Remove("extensions");
        }

        return new JsonObject {
            ["stream"]    = (string)stored["stream"]!,
            ["type"]      = (string)stored["type"]!,
            ["id"]        = (string)stored["id"]!,
            ["line"]      = (int)stored["metadata"]!["$lineNumber"]!,
            ["timestamp"] = (string?)stored["metadata"]!["$timestamp"],
            ["payload"]   = payload,
        };
    }

    static string Fixture(string name, [CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "Fixtures", name);
}
