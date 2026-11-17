using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.Antigravity;
using Google.Protobuf;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Antigravity;

/// <summary>Whole Antigravity transcripts projected through one context, every channel of every result compared
/// against a reviewed golden file. <c>text-and-tool-turn</c> and <c>failing-tool-turn</c> are kcap-server's corpus
/// sessions under the session ids their baselines were recorded with; <c>antigravity-turns</c> is synthetic. A
/// <c>#batch</c> line is a batch boundary and is not projected or numbered. Each batch is projected in step order,
/// as a caller sorts a step-ordered projection's batch. Run with <c>KCAP_UPDATE_GOLDEN=1</c> to rewrite the
/// goldens after a deliberate change, then review the diff.</summary>
public class AntigravityGoldenTests {
    static readonly DateTimeOffset        Received = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    const string                          Boundary = "#batch";

    [Test]
    [Arguments("antigravity-text-and-tool-turn", "51fadfa309ae008cbcfc8abb50b18516", null)]
    [Arguments("antigravity-failing-tool-turn", "62cb31f9bcdbfa61fb7d6de487895bf8", null)]
    [Arguments("antigravity-turns", "sess", "agent-1")]
    public async Task The_fixture_projects_to_its_golden_file(string fixture, string sessionId, string? agentId) {
        var projection = AntigravityTranscriptEvents.Instance;
        var dir        = Path.Combine(SourceDir(), "Fixtures");
        var ctx        = projection.CreateContext(sessionId, agentId);
        var projected  = new JsonArray();
        var number     = -1;
        var batches    = new List<List<(string Text, int Number)>> { new() };

        foreach (var line in await File.ReadAllLinesAsync(Path.Combine(dir, fixture + ".jsonl"))) {
            if (line == Boundary) batches.Add([]);
            // Numbered from 0, as the recorder numbers a transcript's lines.
            else batches[^1].Add((line, ++number));
        }

        for (var batch = 0; batch < batches.Count; batch++) {
            ctx.BeginBatch();
            foreach (var (text, lineNumber) in batches[batch].OrderBy(l => projection.StepOrder(l.Text))) {
                var result = projection.Project(text, lineNumber, Received, ctx);
                projected.Add(new JsonObject {
                    ["line"]     = lineNumber,
                    ["batch"]    = batch + 1,
                    ["rejected"] = result.Rejected is null ? null : JsonValue.Create(true),
                    ["events"]   = new JsonArray([.. result.Events.Select(Render)]),
                });
            }
        }

        var actual   = projected.ToJsonString(Indented).Replace("\r\n", "\n");
        var expected = Path.Combine(dir, fixture + ".expected.json");
        if (Environment.GetEnvironmentVariable("KCAP_UPDATE_GOLDEN") == "1") await File.WriteAllTextAsync(expected, actual + "\n");

        await Assert.That(actual).IsEqualTo((await File.ReadAllTextAsync(expected)).Replace("\r\n", "\n").TrimEnd('\n'));
    }

    static JsonNode Render(CanonicalEvent e) => new JsonObject {
        ["type"]            = e.EventType,
        ["id"]              = e.EventId.ToString(),
        ["lineNumber"]      = e.LineNumber,
        ["timestamp"]       = e.Timestamp.ToString("O"),
        ["recordTimestamp"] = e.RecordTimestamp,
        ["payload"]         = Payload(e.Payload),
    };

    static JsonNode? Payload(object payload) => payload switch {
        IMessage message                  => JsonNode.Parse(JsonFormatter.Default.Format(message)),
        AntigravityUsageBackfill backfill => new JsonObject {
            ["model"]              = backfill.Model,
            ["input_tokens"]       = backfill.InputTokens,
            ["output_tokens"]      = backfill.OutputTokens,
            ["cache_read_tokens"]  = backfill.CacheReadTokens,
            ["cache_write_tokens"] = backfill.CacheWriteTokens,
        },
        _ => throw new InvalidOperationException($"No rendering for {payload.GetType().Name}"),
    };

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
