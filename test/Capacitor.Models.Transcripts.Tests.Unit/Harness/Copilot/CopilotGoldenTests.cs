using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.Copilot;
using Google.Protobuf;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Copilot;

/// <summary>Whole Copilot transcripts projected through one context, every channel of every result compared against a
/// reviewed golden file. Every fixture but <c>copilot-synthetic</c> is a kcap-server corpus session under the session
/// id its baseline was recorded with, its <c>system.message</c> text replaced, which the projection never reads. A
/// <c>#batch</c> line is a batch boundary: BeginBatch runs there and the line is not projected or numbered. Run with
/// <c>KCAP_UPDATE_GOLDEN=1</c> to rewrite the goldens after a deliberate change, then review the diff.</summary>
public class CopilotGoldenTests {
    static readonly DateTimeOffset Received = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    const string Boundary = "#batch";

    /// Proto field names, as the server stores a payload, so a golden reads against kcap-server's baseline directly.
    static readonly JsonFormatter Proto = new(JsonFormatter.Settings.Default.WithPreserveProtoFieldNames(true));

    [Test]
    [Arguments("3aa78294", "3aa7829478c54ea7bd0d97a6b61a8197")]
    [Arguments("a4bbf204", "a4bbf204240d42d4950333d0e3c9da90")]
    [Arguments("494ed425", "494ed425f7f6494a99cb4ab2ef7831fe")]
    [Arguments("4ae28a73", "4ae28a73dd6646ac81d2be94b5e87079")]
    [Arguments("1053bee4", "1053bee4574f40e384ca463bd7a82dc2")]
    [Arguments("vscode-edit-tools", "e7baa036803544399d1dd4d4e76adcb0")]
    [Arguments("copilot-synthetic", "0c0c0c0c00004000800000000000000f")]
    public async Task The_fixture_projects_to_its_golden_file(string fixture, string sessionId) {
        var dir       = Path.Combine(SourceDir(), "Fixtures");
        var lines     = await File.ReadAllLinesAsync(Path.Combine(dir, fixture + ".jsonl"));
        var ctx       = CopilotTranscriptEvents.Instance.CreateContext(sessionId, null);
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
            var result = CopilotTranscriptEvents.Instance.Project(line, ++number, Received, ctx);
            if (result.Rejected is null && result.Events.Count == 0 && result.Amendments.Count == 0) continue;

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
        ["payload"]         = Payload(e.Payload),
        ["usage"]           = e.Usage is null ? null : JsonNode.Parse(Proto.Format(e.Usage)),
        ["vendorMetadata"]  = e.VendorMetadata is null ? null : new JsonObject([.. e.VendorMetadata.Select(m => KeyValuePair.Create(m.Key, (JsonNode?)m.Value))]),
        ["attachments"]     = e.Attachments is null ? null : new JsonArray([.. e.Attachments.Select(Attachment)]),
    };

    static JsonNode Attachment(TranscriptAttachment a) => new JsonObject {
        ["id"]          = a.Id.ToString(),
        ["fileName"]    = a.FileName,
        ["contentType"] = a.ContentType,
        ["sha256"]      = Convert.ToHexStringLower(SHA256.HashData(a.Data)),
    };

    static JsonNode? Payload(object payload) => payload switch {
        IMessage message                              => JsonNode.Parse(Proto.Format(message)),
        CopilotUsageBackfill or CopilotSessionMetrics => JsonSerializer.SerializeToNode(payload, payload.GetType()),
        _ => throw new InvalidOperationException($"No rendering for {payload.GetType().Name}"),
    };

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
