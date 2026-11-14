using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.Gemini;
using Google.Protobuf;
using TUnit.Assertions.Enums;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Gemini;

/// <summary>Gemini recordings from kcap-server's corpus (<c>test/data/gemini/chats</c>), byte for byte, one stream per
/// file. Lines are numbered from 0, as the recorder numbers them, and blank ones are skipped. Each batch's events are
/// marked stored when it closes and an id is stored once, as the server's pipeline does. Run with
/// <c>KCAP_UPDATE_GOLDEN=1</c> to rewrite the goldens after a deliberate change, then review the diff.</summary>
public class GeminiCorpusTests {
    static readonly DateTimeOffset Received = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    const string Child = "gemini-subagent.57d9b49827054af5b060ebaba4878c96";

    /// What the server stored for each file in its v1 golden baseline (<c>test/data/golden/gemini/v1</c>): line, event
    /// type, id, and whether usage rode the event. These ids are what a re-import dedups against.
    static readonly Dictionary<string, string[]> Baseline = new() {
        ["gemini-text-turn"] = [
            "2 UserMessageReceived f098a737-cee1-4415-83d8-d0da04fcda97",
            "4 AssistantTextGenerated ce1e78cf-1e45-701a-30e4-44c4253d8973 usage",
        ],
        ["gemini-tool-turn-with-resume"] = [
            "2 UserMessageReceived 5a864aa0-459a-407f-a4d2-bcb4ebe89052",
            "4 AssistantThinkingGenerated df992b2b-ffcf-9188-4aa2-863991f852a8 usage",
            "6 AssistantToolCallsGenerated 8cd8f3fb-20a1-9558-e4a2-80049491b815",
            "6 ToolResultReceived 4a0fe4a1-2a39-5d72-bc01-a6af2e443724",
            "6 AssistantToolCallsGenerated 78e5e6d0-fa95-60ab-a911-887362f47605",
            "6 ToolResultReceived ec553e0f-b824-c778-5a28-92b290b6df5c",
            "6 AssistantToolCallsGenerated 20c4e7ea-0c52-cbfd-1e46-33d4955aece7",
            "6 ToolResultReceived a7480928-833e-f6cb-3d62-05b17c2234f3",
            "6 AssistantToolCallsGenerated 807d4e65-7fc9-9eee-c709-025e71fd4948",
            "6 ToolResultReceived 3932246f-92da-f6a4-6c2e-65390017cebe",
            "9 AssistantThinkingGenerated b5a879de-05d0-82c1-8241-27398e1ac8cb usage",
            "9 AssistantTextGenerated 63104bfd-dd78-234f-6c13-1993069bb5e2",
            "15 UserMessageReceived 8793743a-6650-4672-a880-3e8eeafeef85",
            "17 AssistantThinkingGenerated 52fb329a-3f18-d62b-be23-77f34ee72a01 usage",
            "19 AssistantToolCallsGenerated f111f3b2-1039-d072-a98d-53709cb0f0ae",
            "19 ToolResultReceived 31e197a5-e7e9-ae2a-1905-2a02f9cebbd7",
            "22 AssistantTextGenerated 8e6439b5-69d0-b5ae-8284-e1945de24603 usage",
        ],
        ["gemini-subagent"] = [
            "2 UserMessageReceived 83a95e53-33c3-4f2f-8bf5-ea80326d3e1b",
            "4 AssistantThinkingGenerated 3dda3c17-7015-253a-80f8-451b2446653a usage",
            "4 AssistantTextGenerated 8542f1e3-81fc-8fc2-3bb2-7eaf2624ac80",
            "6 AssistantToolCallsGenerated 49cc7d67-b5bd-aa41-cf5d-889ca1c65d00",
            "6 ToolResultReceived de0b81ab-1048-9318-b4f2-5778c6a78e00",
            "6 AssistantToolCallsGenerated 07714de5-19d7-47ca-60b3-ab84743e8020",
            "6 ToolResultReceived 15534406-e8a4-51ca-c325-86fe5e3c969f",
            "9 AssistantThinkingGenerated 8143e52b-4f61-ba43-e7e3-7edc50126644 usage",
            "11 AssistantToolCallsGenerated d449b3d6-08ee-73d8-3dfe-210aedb5c251",
            "11 ToolResultReceived 9f9fcb96-f35e-4e0c-884c-1b668ccc158e",
            "14 AssistantTextGenerated 702a5841-0ac9-4297-8927-77a9a07f5c88 usage",
        ],
        [Child] = [
            "1 UserMessageReceived 297dbfc8-3ae7-428c-bb2d-1e298bee8960",
            "5 AssistantToolCallsGenerated a195fb3d-1ad7-bac8-f0aa-e4bec8b2757a usage",
            "5 ToolResultReceived 61779c00-1042-2561-eaa8-e786eb85d010",
            "10 AssistantToolCallsGenerated 2c6aca97-6d7d-7215-ca4c-7ad37cbc412d usage",
            "10 ToolResultReceived 4b7ca6aa-9cd5-e3e1-dee2-973838349863",
            "15 AssistantToolCallsGenerated df3931c5-6822-7d90-274d-4ce25bb165b9 usage",
            "15 ToolResultReceived ac22dc7b-a45c-403e-9f50-6039a2a928eb",
            "18 AssistantThinkingGenerated 3b994c7c-ac9e-872f-0081-8b39bdf5c1dd usage",
            "20 AssistantToolCallsGenerated 881d5411-dd94-8206-166f-b89d6eabd2f3",
            "20 ToolResultReceived d8f6062c-ae9c-2c3c-65fa-3aff34fb176a",
        ],
    };

    public static IEnumerable<string> Fixtures() => Baseline.Keys;

    [Test]
    [MethodDataSource(nameof(Fixtures))]
    public async Task The_fixture_projects_to_its_golden_file(string fixture) {
        var ctx       = GeminiTranscriptEvents.Instance.CreateContext("sess", null);
        var projected = new JsonArray();
        var lines     = await Lines(fixture);

        for (var number = 0; number < lines.Length; number++) {
            if (string.IsNullOrWhiteSpace(lines[number])) continue;

            var result = GeminiTranscriptEvents.Instance.Project(lines[number], number, Received, ctx);
            await Assert.That(result.Amendments).IsEmpty();
            projected.Add(new JsonObject {
                ["line"]     = number,
                ["rejected"] = result.Rejected,
                ["events"]   = new JsonArray([.. result.Events.Select(Render)]),
            });
        }

        var actual   = projected.ToJsonString(Indented).Replace("\r\n", "\n");
        var expected = Path.Combine(SourceDir(), "Fixtures", fixture + ".expected.json");
        if (Environment.GetEnvironmentVariable("KCAP_UPDATE_GOLDEN") == "1") await File.WriteAllTextAsync(expected, actual + "\n");

        await Assert.That(actual).IsEqualTo((await File.ReadAllTextAsync(expected)).Replace("\r\n", "\n").TrimEnd('\n'));
    }

    [Test]
    [MethodDataSource(nameof(Fixtures))]
    public async Task What_one_batch_stores_is_what_the_servers_baseline_recorded(string fixture) {
        var stored = Stored(await Lines(fixture), batchPerLine: false)
            .Select(s => $"{s.Line} {s.Event.EventType} {s.Event.EventId}{(s.Event.Usage is null ? "" : " usage")}");

        await Assert.That(stored).IsEquivalentTo(Baseline[fixture], CollectionOrdering.Matching);
    }

    /// Line by line, each line sees what the lines before it stored, so a snapshot's repeat returns only its new parts;
    /// in one batch it returns every part and the store keeps the first of each id. Either way the same events land.
    [Test]
    [MethodDataSource(nameof(Fixtures))]
    public async Task Line_by_line_stores_exactly_what_one_batch_stores(string fixture) {
        var lines = await Lines(fixture);
        var whole = Stored(lines, batchPerLine: false).Select(s => Render(s.Event).ToJsonString()).ToArray();
        var split = Stored(lines, batchPerLine: true).Select(s => Render(s.Event).ToJsonString()).ToArray();

        await Assert.That(split).IsEquivalentTo(whole, CollectionOrdering.Matching);
    }

    static List<(int Line, CanonicalEvent Event)> Stored(string[] lines, bool batchPerLine) {
        var ctx      = GeminiTranscriptEvents.Instance.CreateContext("sess", null);
        var stored   = new List<(int, CanonicalEvent)>();
        var storedId = new HashSet<Guid>();
        var batch    = new List<Guid>();

        void Close() {
            foreach (var id in batch) ctx.MarkPersisted(id);
            batch.Clear();
            ctx.BeginBatch();
        }

        for (var number = 0; number < lines.Length; number++) {
            if (string.IsNullOrWhiteSpace(lines[number])) continue;

            foreach (var e in GeminiTranscriptEvents.Instance.Project(lines[number], number, Received, ctx).Events) {
                batch.Add(e.EventId);
                if (storedId.Add(e.EventId)) stored.Add((number, e));
            }
            if (batchPerLine) Close();
        }
        Close();
        return stored;
    }

    static JsonNode Render(CanonicalEvent e) => new JsonObject {
        ["type"]            = e.EventType,
        ["id"]              = e.EventId.ToString(),
        ["timestamp"]       = e.Timestamp.ToString("O"),
        ["recordTimestamp"] = e.RecordTimestamp,
        ["payload"]         = JsonNode.Parse(JsonFormatter.Default.Format((IMessage)e.Payload)),
        ["usage"]           = e.Usage is null ? null : JsonNode.Parse(JsonFormatter.Default.Format(e.Usage)),
    };

    static Task<string[]> Lines(string fixture) => File.ReadAllLinesAsync(Path.Combine(SourceDir(), "Fixtures", fixture + ".jsonl"));

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
