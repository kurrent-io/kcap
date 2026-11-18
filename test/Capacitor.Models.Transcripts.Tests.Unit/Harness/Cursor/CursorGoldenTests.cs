using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.Cursor;
using Google.Protobuf;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Cursor;

/// <summary>Whole Cursor transcripts projected through one context, every channel of every result compared against
/// a reviewed golden file. The transcripts are kcap-server's corpus sessions under the ids its baseline was recorded
/// with; <c>cursor-tool-results</c> is the constructed one, with a subagent transcript beside it. Run with
/// <c>KCAP_UPDATE_GOLDEN=1</c> to rewrite the goldens after a deliberate change, then review the diff.</summary>
public class CursorGoldenTests {
    static readonly DateTimeOffset Received = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Test]
    [Arguments("cursor-skill-probe", "c0cab74dcfb64bcbbeb8e86ad0ce5281", null)]
    [Arguments("cursor-skill-probe-two-turns", "2eef1089a2b748f1b84f88bc27ae98cd", null)]
    [Arguments("cursor-tool-results", "cc000000000040008000000000000001", null)]
    [Arguments("cursor-tool-results.cc0000000000400080000000000000c1", "cc000000000040008000000000000001", "cc0000000000400080000000000000c1")]
    public async Task The_fixture_projects_to_its_golden_file(string fixture, string sessionId, string? agentId) {
        var dir       = Path.Combine(SourceDir(), "Fixtures");
        var lines     = await File.ReadAllLinesAsync(Path.Combine(dir, fixture + ".jsonl"));
        var ctx       = CursorTranscriptEvents.Instance.CreateContext(sessionId, agentId);
        var projected = new JsonArray();

        ctx.BeginBatch();
        for (var number = 0; number < lines.Length; number++) {
            var result = CursorTranscriptEvents.Instance.Project(lines[number], number, Received, ctx);
            projected.Add(new JsonObject {
                ["line"]       = number,
                ["rejected"]   = result.Rejected is null ? null : JsonValue.Create(true),
                ["events"]     = new JsonArray([.. result.Events.Select(Render)]),
                ["amendments"] = result.Amendments.Count,
            });
        }

        var actual   = projected.ToJsonString(Indented).Replace("\r\n", "\n");
        var expected = Path.Combine(dir, fixture + ".expected.json");
        if (Environment.GetEnvironmentVariable("KCAP_UPDATE_GOLDEN") == "1") await File.WriteAllTextAsync(expected, actual + "\n");

        await Assert.That(actual).IsEqualTo((await File.ReadAllTextAsync(expected)).Replace("\r\n", "\n").TrimEnd('\n'));
    }

    static JsonNode Render(CanonicalEvent e) => new JsonObject {
        ["type"]            = e.EventType,
        ["id"]              = e.EventId.ToString(),
        ["timestamp"]       = e.Timestamp.ToString("O"),
        ["recordTimestamp"] = e.RecordTimestamp,
        ["metadata"]        = new JsonObject([.. e.VendorMetadata!.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))]),
        ["payload"]         = JsonNode.Parse(JsonFormatter.Default.Format((IMessage)e.Payload)),
    };

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
