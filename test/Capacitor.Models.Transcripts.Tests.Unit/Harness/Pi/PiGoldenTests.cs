using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.Pi;
using Google.Protobuf;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Pi;

/// <summary>The Pi sessions of kcap-server's corpus (<c>test/data/pi/sessions/</c>), each projected through one
/// context with the session id and line numbers the server's pipeline gives them, every channel compared against a
/// reviewed golden file. The ids and metadata in the goldens are the ones kcap-server's <c>v1</c> baseline holds for
/// these lines, which its parity suite checks. Run with <c>KCAP_UPDATE_GOLDEN=1</c> to rewrite the goldens after a
/// deliberate change, then review the diff.</summary>
public class PiGoldenTests {
    static readonly DateTimeOffset Received = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// Proto field names, as the server stores a payload, so a golden reads against its baseline.
    static readonly JsonFormatter Proto = new(JsonFormatter.Settings.Default.WithPreserveProtoFieldNames(true));

    public static IEnumerable<string> Fixtures() => [
        "019ed736-4cb3-7d79-b2a3-a6f858b5328f",
        "019ed76b-c66e-74ed-b751-5ab9d7d58a74",
        "019ed892-0000-7000-8000-000000000001",
        "019ed892-2234-7000-8000-000000000002",
    ];

    [Test]
    [MethodDataSource(nameof(Fixtures))]
    public async Task The_session_projects_to_its_golden_file(string fixture) {
        var actual   = Render(await Project(fixture, lineByLine: false)).ToJsonString(Indented).Replace("\r\n", "\n");
        var expected = Path.Combine(SourceDir(), "Fixtures", fixture + ".expected.json");
        if (Environment.GetEnvironmentVariable("KCAP_UPDATE_GOLDEN") == "1") await File.WriteAllTextAsync(expected, actual + "\n");

        await Assert.That(actual).IsEqualTo((await File.ReadAllTextAsync(expected)).Replace("\r\n", "\n").TrimEnd('\n'));
    }

    /// <summary>The tree spans the session, so a batch boundary after every line changes nothing.</summary>
    [Test]
    [MethodDataSource(nameof(Fixtures))]
    public async Task A_boundary_after_every_line_projects_the_same_events(string fixture) {
        var oneBatch   = Render(await Project(fixture, lineByLine: false)).ToJsonString();
        var lineByLine = Render(await Project(fixture, lineByLine: true)).ToJsonString();

        await Assert.That(lineByLine).IsEqualTo(oneBatch);
    }

    /// <summary>The server's session id is the canonical form of the file's, and its line numbers are 0-based
    /// indexes with blank lines counted.</summary>
    static async Task<List<(int Line, ProjectionResult Result)>> Project(string fixture, bool lineByLine) {
        var lines   = await File.ReadAllLinesAsync(Path.Combine(SourceDir(), "Fixtures", fixture + ".jsonl"));
        var ctx     = PiTranscriptEvents.Instance.CreateContext(Guid.Parse(fixture).ToString("N"), null);
        var results = new List<(int, ProjectionResult)>();

        ctx.BeginBatch();
        for (var i = 0; i < lines.Length; i++) {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            if (lineByLine) ctx.BeginBatch();
            results.Add((i, PiTranscriptEvents.Instance.Project(lines[i], i, Received, ctx)));
        }

        return results;
    }

    static JsonArray Render(List<(int Line, ProjectionResult Result)> results) => new([
        .. results.Select(r => (JsonNode)new JsonObject {
            ["line"]     = r.Line,
            ["rejected"] = r.Result.Rejected,
            ["events"]   = new JsonArray([.. r.Result.Events.Select(Render)]),
        }),
    ]);

    static JsonNode Render(CanonicalEvent e) => new JsonObject {
        ["type"]            = e.EventType,
        ["id"]              = e.EventId.ToString(),
        ["timestamp"]       = e.Timestamp.ToString("O"),
        ["recordTimestamp"] = e.RecordTimestamp,
        ["metadata"]        = e.VendorMetadata is { } metadata
            ? new JsonObject(metadata.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => KeyValuePair.Create(m.Key, (JsonNode?)m.Value)))
            : null,
        ["usage"]         = e.Usage is null ? null : JsonNode.Parse(Proto.Format(e.Usage)),
        ["cacheCreation"] = e.CacheCreationTokens,
        ["payload"]       = e.Payload switch {
            IMessage message                => JsonNode.Parse(Proto.Format(message)),
            ContextCompactedEvent compacted => JsonNode.Parse(JsonSerializer.Serialize(compacted, TranscriptEventJsonContext.Default.ContextCompactedEvent)),
            _                               => throw new InvalidOperationException($"No rendering for {e.Payload.GetType().Name}"),
        },
    };

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
