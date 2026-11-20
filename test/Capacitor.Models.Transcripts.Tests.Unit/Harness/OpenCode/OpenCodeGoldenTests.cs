using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.OpenCode;
using Google.Protobuf;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.OpenCode;

/// <summary>The server's OpenCode corpus and transcripts the plugin recorded on OpenCode 2.3.2 (the <c>v2-</c> fixtures),
/// each stream projected through one context and every channel of every
/// result compared against a reviewed golden file. Lines are numbered by their 0-based index, as the recorder
/// numbers them. Run with <c>KCAP_UPDATE_GOLDEN=1</c> to rewrite the goldens after a deliberate change, then review
/// the diff.</summary>
public class OpenCodeGoldenTests {
    static readonly DateTimeOffset Received = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Test]
    [Arguments("text-turn", null)]
    [Arguments("tool-turn", null)]
    [Arguments("real-tool-turn", null)]
    [Arguments("subagent-parent", null)]
    [Arguments("subagent-child", "ses_oc_child")]
    [Arguments("v2-tool-turn", null)]
    [Arguments("v2-shell-and-error", null)]
    [Arguments("v2-subagent-parent", null)]
    [Arguments("v2-subagent-child", "ses_e08d4e556ffeohsWSjMhC21WqH")]
    public async Task The_fixture_projects_to_its_golden_file(string fixture, string? agentId) {
        var dir       = Path.Combine(SourceDir(), "Fixtures");
        var lines     = await File.ReadAllLinesAsync(Path.Combine(dir, fixture + ".jsonl"));
        var ctx       = OpenCodeTranscriptEvents.Instance.CreateContext("ses_oc_parent", agentId);
        var projected = new JsonArray();

        ctx.BeginBatch();
        for (var number = 0; number < lines.Length; number++) {
            var result = OpenCodeTranscriptEvents.Instance.Project(lines[number], number, Received, ctx);
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
        ["type"]                = e.EventType,
        ["id"]                  = e.EventId.ToString(),
        ["timestamp"]           = e.Timestamp.ToString("O"),
        ["recordTimestamp"]     = e.RecordTimestamp,
        ["causedBy"]            = e.CausedBy,
        ["payload"]             = JsonNode.Parse(JsonFormatter.Default.Format((IMessage)e.Payload)),
        ["usage"]               = e.Usage is null ? null : JsonNode.Parse(JsonFormatter.Default.Format(e.Usage)),
        ["usageIsEcho"]         = e.UsageIsEcho,
        ["cacheCreationTokens"] = e.CacheCreationTokens,
    };

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
