using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.Kiro;
using Google.Protobuf;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Kiro;

/// <summary>Whole Kiro logs projected through one context, every channel of every result compared against a
/// reviewed golden file. <c>kiro-session</c> is kcap-server's corpus session under the session id its baseline
/// was recorded with; <c>kiro-turns</c> is synthetic. A <c>#batch</c> line is a batch boundary: BeginBatch runs
/// there and the line is not projected or numbered. Run with <c>KCAP_UPDATE_GOLDEN=1</c> to rewrite the goldens
/// after a deliberate change, then review the diff.</summary>
public class KiroGoldenTests {
    static readonly DateTimeOffset Received = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    const string Boundary = "#batch";

    [Test]
    [Arguments("kiro-session", "5f6c2b1a9e3d4a7cbb121c2d3e4f5a6b", null)]
    [Arguments("kiro-turns", "sess", "agent-1")]
    public async Task The_fixture_projects_to_its_golden_file(string fixture, string sessionId, string? agentId) {
        var dir       = Path.Combine(SourceDir(), "Fixtures");
        var lines     = await File.ReadAllLinesAsync(Path.Combine(dir, fixture + ".jsonl"));
        var ctx       = KiroTranscriptEvents.Instance.CreateContext(sessionId, agentId);
        var projected = new JsonArray();
        var batch     = 1;
        var number    = -1;

        ctx.BeginBatch();
        foreach (var line in lines) {
            if (line == Boundary) {
                ctx.BeginBatch();
                batch++;
                continue;
            }

            // Numbered from 0, as the recorder numbers a transcript's lines.
            var result = KiroTranscriptEvents.Instance.Project(line, ++number, Received, ctx);
            projected.Add(new JsonObject {
                ["line"]       = number,
                ["batch"]      = batch,
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
        ["causedBy"]        = e.CausedBy,
        ["payload"]         = Payload(e.Payload),
        ["usage"]           = e.Usage is null ? null : JsonNode.Parse(JsonFormatter.Default.Format(e.Usage)),
        ["usageIsEcho"]     = e.UsageIsEcho,
    };

    static JsonNode? Payload(object payload) => payload switch {
        IMessage message           => JsonNode.Parse(JsonFormatter.Default.Format(message)),
        KiroUsageBackfill backfill => new JsonObject {
            ["credits"]                  = backfill.Credits,
            ["context_usage_percentage"] = backfill.ContextUsagePercentage,
        },
        _ => throw new InvalidOperationException($"No rendering for {payload.GetType().Name}"),
    };

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
