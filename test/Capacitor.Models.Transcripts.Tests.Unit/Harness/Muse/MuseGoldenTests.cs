using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.Muse;
using Google.Protobuf;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Muse;

/// <summary>Whole Muse logs projected through one context, every channel of every result compared against a
/// reviewed golden file. <c>muse-tool-turn</c> is kcap-server's corpus log; <c>muse-turns</c> is synthetic. A
/// <c>#batch</c> line is a batch boundary: BeginBatch runs there and the line is not projected or numbered. Run with
/// <c>KCAP_UPDATE_GOLDEN=1</c> to rewrite the goldens after a deliberate change, then review the diff.</summary>
public class MuseGoldenTests {
    static readonly DateTimeOffset Received = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    const string Boundary = "#batch";

    [Test]
    [Arguments("muse-tool-turn")]
    [Arguments("muse-turns")]
    public async Task The_fixture_projects_to_its_golden_file(string fixture) {
        var dir       = Path.Combine(SourceDir(), "Fixtures");
        var lines     = await File.ReadAllLinesAsync(Path.Combine(dir, fixture + ".jsonl"));
        var ctx       = MuseTranscriptEvents.Instance.CreateContext("session", null);
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
            var result = MuseTranscriptEvents.Instance.Project(line, ++number, Received, ctx);
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
        ["type"]                 = e.EventType,
        ["id"]                   = e.EventId.ToString(),
        ["timestamp"]            = e.Timestamp.ToString("O"),
        ["recordTimestamp"]      = e.RecordTimestamp,
        ["payload"]              = Payload(e.Payload),
        ["usage"]                = e.Usage is null ? null : JsonNode.Parse(JsonFormatter.Default.Format(e.Usage)),
        ["usageIsEcho"]          = e.UsageIsEcho,
        ["cacheCreationTokens"]  = e.CacheCreationTokens,
        ["vendorMetadata"]       = e.VendorMetadata is { } strings ? new JsonObject([.. strings.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))]) : null,
        ["vendorMetadataValues"] = e.VendorMetadataValues is { } values ? new JsonObject([.. values.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)Value(kv.Value)))]) : null,
    };

    static JsonValue Value(object value) => value switch {
        long n => JsonValue.Create(n),
        bool b => JsonValue.Create(b),
        _      => throw new InvalidOperationException($"A vendor metadata value is a long or a bool, not {value.GetType().Name}"),
    };

    static JsonNode? Payload(object payload) => payload switch {
        IMessage message  => JsonNode.Parse(JsonFormatter.Default.Format(message)),
        MuseUsageBackfill => new JsonObject(),
        _                 => throw new InvalidOperationException($"No rendering for {payload.GetType().Name}"),
    };

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
