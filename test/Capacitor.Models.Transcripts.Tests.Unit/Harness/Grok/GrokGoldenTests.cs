using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.Codex;
using Capacitor.Models.Transcripts.Harness.Grok;
using Google.Protobuf;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Grok;

/// <summary>Whole Grok logs projected through one context, every channel of every result compared against a
/// reviewed golden file. <c>grok-tool-turn</c> and <c>grok-subagent-child</c> are kcap-server's corpus session and
/// its subagent, under the ids its baseline was recorded with; <c>grok-turns</c> is synthetic. A <c>#batch</c> line is
/// a batch boundary: BeginBatch runs there and the line is not projected or numbered. Run with
/// <c>KCAP_UPDATE_GOLDEN=1</c> to rewrite the goldens after a deliberate change, then review the diff.</summary>
public class GrokGoldenTests {
    static readonly DateTimeOffset Received = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    static readonly JsonFormatter Proto = new(JsonFormatter.Settings.Default.WithPreserveProtoFieldNames(true));
    const string Boundary = "#batch";
    const string Session = "01b00000000070008000000000000001";
    const string Child   = "01b000000000700080000000000000c1";

    [Test]
    [Arguments("grok-tool-turn", Session, null)]
    [Arguments("grok-subagent-child", Session, Child)]
    [Arguments("grok-turns", "s1", null)]
    public async Task The_fixture_projects_to_its_golden_file(string fixture, string sessionId, string? agentId) {
        var actual   = Render(await Project(fixture, sessionId, agentId, lineByLine: false)).ToJsonString(Indented).Replace("\r\n", "\n");
        var expected = Path.Combine(SourceDir(), "Fixtures", fixture + ".expected.json");
        if (Environment.GetEnvironmentVariable("KCAP_UPDATE_GOLDEN") == "1") await File.WriteAllTextAsync(expected, actual + "\n");

        await Assert.That(actual).IsEqualTo((await File.ReadAllTextAsync(expected)).Replace("\r\n", "\n").TrimEnd('\n'));
    }

    /// <summary>A turn can span batches, so a boundary after every line changes nothing.</summary>
    [Test]
    [Arguments("grok-tool-turn")]
    [Arguments("grok-turns")]
    public async Task A_boundary_after_every_line_projects_the_same_events(string fixture) {
        var asWritten  = Render(await Project(fixture, "s1", null, lineByLine: false)).ToJsonString();
        var lineByLine = Render(await Project(fixture, "s1", null, lineByLine: true)).ToJsonString();

        await Assert.That(lineByLine).IsEqualTo(asWritten);
    }

    /// <summary>Numbered from 0, as the recorder numbers a transcript's lines.</summary>
    static async Task<List<(int Line, ProjectionResult Result)>> Project(string fixture, string sessionId, string? agentId, bool lineByLine) {
        var lines   = await File.ReadAllLinesAsync(Path.Combine(SourceDir(), "Fixtures", fixture + ".jsonl"));
        var ctx     = GrokTranscriptEvents.Instance.CreateContext(sessionId, agentId);
        var results = new List<(int, ProjectionResult)>();
        var number  = -1;

        ctx.BeginBatch();
        foreach (var line in lines) {
            if (line == Boundary || lineByLine) ctx.BeginBatch();
            if (line == Boundary) continue;

            results.Add((++number, GrokTranscriptEvents.Instance.Project(line, number, Received, ctx)));
        }

        return results;
    }

    static JsonArray Render(List<(int Line, ProjectionResult Result)> results) =>
        new([.. results.Select(JsonNode (r) => new JsonObject {
            ["line"]       = r.Line,
            ["rejected"]   = r.Result.Rejected is null ? null : JsonValue.Create(true),
            ["events"]     = new JsonArray([.. r.Result.Events.Select(Render)]),
            ["amendments"] = r.Result.Amendments.Count,
        })]);

    static JsonNode Render(CanonicalEvent e) => new JsonObject {
        ["type"]            = e.EventType,
        ["id"]              = e.EventId.ToString(),
        ["timestamp"]       = e.Timestamp.ToString("O"),
        ["recordTimestamp"] = e.RecordTimestamp,
        ["payload"]         = Payload(e.Payload),
        ["usage"]           = e.Usage is null ? null : JsonNode.Parse(Proto.Format(e.Usage)),
    };

    static JsonNode? Payload(object payload) => payload switch {
        IMessage message                   => JsonNode.Parse(Proto.Format(message)),
        CodexUsageBackfilledEvent backfill => JsonSerializer.SerializeToNode(backfill),
        _                                  => throw new InvalidOperationException($"No rendering for {payload.GetType().Name}"),
    };

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
