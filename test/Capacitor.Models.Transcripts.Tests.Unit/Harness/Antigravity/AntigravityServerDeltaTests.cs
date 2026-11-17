using System.Runtime.CompilerServices;
using System.Text.Json;
using Capacitor.Models.Transcripts.Harness.Antigravity;
using Google.Protobuf;
using Kurrent.Agent.Schema.Events;
using TUnit.Assertions.Enums;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Antigravity;

/// <summary>kcap-server's two Antigravity corpus sessions (<c>test/data/antigravity/chats/</c>, copied with an
/// <c>antigravity-</c> prefix) against what its normalizer stored for them in the <c>v1</c> golden baseline: the
/// same events on the same ids, lines, times and call ids, whether a session arrives in one batch or a line at a
/// time, and one difference kept, a tool kind on each tool call. In <c>failing-tool-turn</c> the result at step 6
/// is written before the planner at step 5 that calls it.</summary>
public class AntigravityServerDeltaTests {
    static readonly DateTimeOffset Received = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    const string User     = CanonicalEventTypes.UserMessageReceived;
    const string Calls    = CanonicalEventTypes.AssistantToolCallsGenerated;
    const string Result   = CanonicalEventTypes.ToolResultReceived;
    const string Thinking = CanonicalEventTypes.AssistantThinkingGenerated;
    const string Text     = CanonicalEventTypes.AssistantTextGenerated;

    /// <summary>Per baseline event, in stored order: its type, id, <c>$lineNumber</c>, payload time, call id, and
    /// whether the result carries the failure slug.</summary>
    static readonly Row[] TextAndToolTurn = [
        new(User, "2e44bea8-735f-91b3-92dd-a0b7cbefc254", 0, "2026-07-02T19:04:07Z", null),
        new(Calls, "da1c36f4-52f2-d227-b4b1-184b3980cae6", 2, "2026-07-02T19:04:07Z", "678ceb0b309f5a23977db697295c262a"),
        new(Result, "8bba4f72-cc42-dfaf-85db-07c7c33109ce", 3, "2026-07-02T19:04:08Z", "678ceb0b309f5a23977db697295c262a"),
        new(Calls, "c5dbc0f9-7706-a2e5-8ee7-a567d6f2a147", 5, "2026-07-02T19:04:08Z", "059f21ac3e86d5e865b6b052b8279650"),
        new(Result, "113d6267-8f59-b49e-67e1-0e06e4797c83", 6, "2026-07-02T19:04:09Z", "059f21ac3e86d5e865b6b052b8279650"),
        new(Thinking, "85285924-ab03-809c-1b0b-c88f352e93af", 7, "2026-07-02T19:04:09Z", null),
        new(Calls, "e59f5e5e-b8f1-481a-a05e-616d9b1dcc9c", 7, "2026-07-02T19:04:09.000000100Z", "bd2dd331b7822829b17dab304a628fe8"),
        new(Result, "88929260-b827-0f39-6cfc-0acabfb49597", 8, "2026-07-02T19:04:11Z", "bd2dd331b7822829b17dab304a628fe8"),
        new(Calls, "9c6201e4-bb77-133a-d7bf-9247d1df7686", 9, "2026-07-02T19:04:11Z", "b65a051c775865232063a2e83edff906"),
        new(Result, "b59c72e3-627e-6375-bb18-7f17df719067", 10, "2026-07-02T19:04:12Z", "b65a051c775865232063a2e83edff906"),
        new(Text, "bb3ad7dc-913a-7f2b-ab46-5571768ecca3", 11, "2026-07-02T19:04:28Z", null),
    ];

    static readonly Row[] FailingToolTurn = [
        new(User, "17b88861-d82b-5128-4dc1-d2d59b7976ff", 0, "2026-07-02T19:04:51Z", null),
        new(Calls, "0066cfa4-6cd6-a61e-e758-d23e106f9d99", 2, "2026-07-02T19:04:51Z", "bfe6d5710cd520010e9bb16d9e9348bb"),
        new(Result, "2411e962-2434-0c16-81ef-cb0ff1cccba2", 3, "2026-07-02T19:04:57Z", "bfe6d5710cd520010e9bb16d9e9348bb"),
        new(Calls, "fe90f2d1-ce67-168f-9396-cb56cad02a32", 6, "2026-07-02T19:04:57Z", "2a67458d65734e191768c4d73e6cf2ac"),
        new(Calls, "fb16560d-a753-2e34-8e0d-d2351568f23c", 6, "2026-07-02T19:04:57.000000100Z", "1ca89bd08f055a70b1d1db2b9f8753bb"),
        new(Result, "cbe44c36-72c8-a4b2-4396-659fa130f413", 5, "2026-07-02T19:04:58Z", "2a67458d65734e191768c4d73e6cf2ac"),
        new(Result, "1f10beee-eb0d-2dc2-4098-5665a161c729", 7, "2026-07-02T19:04:58Z", "1ca89bd08f055a70b1d1db2b9f8753bb"),
        new(Calls, "a8b54db4-6a3b-86f1-5427-84528adcf7c3", 8, "2026-07-02T19:04:59Z", "9573777bf0e4521dab14097686ecdf9c"),
        new(Result, "42576dd3-f125-81bf-6ef7-b330d66fe79c", 9, "2026-07-02T19:05:01Z", "9573777bf0e4521dab14097686ecdf9c"),
        new(Calls, "c7dce551-30f4-f1fa-b390-94ca69c9f896", 10, "2026-07-02T19:05:22Z", "fbda97f199d61385c548c8bf1570124c"),
        new(Result, "50db1c49-546d-2371-44a9-439dc6e647b6", 11, "2026-07-02T19:05:23Z", "fbda97f199d61385c548c8bf1570124c"),
        new(Calls, "5f5777bf-0335-92b6-2893-a048598d864f", 12, "2026-07-02T19:05:26Z", "ba2c3a2996a2c5e9346cc0abae06b240"),
        new(Result, "5fbd51eb-2b19-b899-1293-d1e2cf7395ed", 13, "2026-07-02T19:05:27Z", "ba2c3a2996a2c5e9346cc0abae06b240"),
        new(Calls, "653e4741-355e-e5e0-cde4-f2ee9b188a75", 14, "2026-07-02T19:05:41Z", "d7f01ebe2e07addafa1070b9e0c9bf2b"),
        new(Result, "2247df1f-daf9-19f2-fadd-59c2c55889c6", 15, "2026-07-02T19:05:42Z", "d7f01ebe2e07addafa1070b9e0c9bf2b"),
        new(Calls, "b1143647-6b5f-7a83-0f29-699c30f791b9", 16, "2026-07-02T19:05:49Z", "a510d765bac739809b67bbe696811543"),
        new(Result, "8041c78a-3ea3-71df-83be-c32d4bdb3de7", 17, "2026-07-02T19:05:50Z", "a510d765bac739809b67bbe696811543"),
        new(Calls, "c34424bd-119c-50ec-8527-d6587524ce26", 18, "2026-07-02T19:05:52Z", "97ac3241b0151887f05e9e05710031f5"),
        new(Result, "1b105b41-d023-5170-af31-9bd713bd68dc", 19, "2026-07-02T19:05:53Z", "97ac3241b0151887f05e9e05710031f5", Failed: true),
        new(Thinking, "869833ab-9dda-f939-2eed-364f8f6cebe6", 20, "2026-07-02T19:05:54Z", null),
        new(Calls, "1e148901-1824-bed4-336d-8b87c0a8b41b", 20, "2026-07-02T19:05:54.000000100Z", "a49e7593a055ac6c7bd84b63e53c9f60"),
        new(Result, "a1473fad-601a-2db1-7a61-496f1845da81", 21, "2026-07-02T19:05:55Z", "a49e7593a055ac6c7bd84b63e53c9f60"),
        new(Calls, "d680c59a-a2f1-bfc0-8cce-09b4535dba1a", 22, "2026-07-02T19:05:55Z", "e9a1495afc76661a85efdfcb1df61143"),
        new(Result, "e60d4a6f-a147-29fb-f35d-93edc9342af7", 23, "2026-07-02T19:05:56Z", "e9a1495afc76661a85efdfcb1df61143"),
        new(Thinking, "a49dfe77-12cd-c4cc-694c-04d7a72964ca", 24, "2026-07-02T19:05:57Z", null),
        new(Calls, "5d810470-63ce-0723-317e-3bfdd06a0847", 24, "2026-07-02T19:05:57.000000100Z", "cbf6a945d4f9602ae9adfb848fe7edd2"),
        new(Result, "a1a9ed4a-1bbe-1670-3cf0-704f10d18083", 25, "2026-07-02T19:05:58Z", "cbf6a945d4f9602ae9adfb848fe7edd2"),
        new(Text, "46dbdd42-792a-c2e5-0b30-9f56c52e2091", 26, "2026-07-02T19:05:58Z", null),
    ];

    sealed record Row(string Type, string Id, int Line, string PayloadTime, string? CallId, bool Failed = false) {
        public static Row Of((CanonicalEvent Event, int Line) projected) {
            var (e, line) = projected;
            var (time, callId, failed) = e.Payload switch {
                ToolResultReceived r          => (r.Timestamp, r.CallId, r.Extensions.ContainsKey(AntigravityExtension.Slug)),
                AssistantToolCallsGenerated c => (c.Timestamp, c.ToolCalls[0].CallId, false),
                UserMessageReceived u         => (u.Timestamp, (string?)null, false),
                AssistantThinkingGenerated t  => (t.Timestamp, null, false),
                AssistantTextGenerated t      => (t.Timestamp, null, false),
                _                             => throw new InvalidOperationException($"No row for {e.EventType}"),
            };

            return new(e.EventType, e.EventId.ToString(), line, JsonFormatter.Default.Format(time).Trim('"'), callId, failed);
        }
    }

    public static IEnumerable<(string Fixture, bool LineByLine)> Runs() => [
        ("text-and-tool-turn", false),
        ("text-and-tool-turn", true),
        ("failing-tool-turn", false),
        ("failing-tool-turn", true),
    ];

    [Test]
    [MethodDataSource(nameof(Runs))]
    public async Task The_corpus_projects_to_the_baselines_events(string fixture, bool lineByLine) {
        var expected = fixture == "failing-tool-turn" ? FailingToolTurn : TextAndToolTurn;
        var actual   = (await ProjectCorpusAsync(fixture, lineByLine)).Select(Row.Of).ToArray();

        await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>Each event's record time is the <c>created_at</c> of the line it is stored under.</summary>
    [Test]
    [MethodDataSource(nameof(Runs))]
    public async Task Each_event_carries_the_time_its_line_was_written(string fixture, bool lineByLine) {
        var lines  = await ReadAsync(fixture);
        var events = await ProjectCorpusAsync(fixture, lineByLine);

        await Assert.That(events.Select(e => e.Event.RecordTimestamp))
            .IsEquivalentTo(events.Select(e => CreatedAt(lines[e.Line])), CollectionOrdering.Matching);
    }

    /// <summary>The server records no kind; the projection classifies each call, as it does for every vendor.</summary>
    [Test]
    public async Task The_corpus_tool_calls_carry_tool_kinds_the_baseline_lacks() {
        var kinds = (await ProjectCorpusAsync("text-and-tool-turn", false))
            .Select(e => e.Event.Payload).OfType<AssistantToolCallsGenerated>()
            .Select(c => (c.ToolCalls[0].ToolName, c.ToolCalls[0].ToolKind)).ToArray();

        await Assert.That(kinds).IsEquivalentTo(new[] {
            ("list_dir", AcpToolKind.Search),
            ("view_file", AcpToolKind.Read),
            ("replace_file_content", AcpToolKind.Edit),
            ("run_command", AcpToolKind.Execute),
        }, CollectionOrdering.Matching);
    }

    /// <summary>One batch is sorted by step, as the caller sorts a step-ordered projection's batch; a line at a time
    /// is each line its own batch, in file order, so a result written early is held until its planner.</summary>
    static async Task<IReadOnlyList<(CanonicalEvent Event, int Line)>> ProjectCorpusAsync(string fixture, bool lineByLine) {
        var projection = AntigravityTranscriptEvents.Instance;
        var lines      = await ReadAsync(fixture);
        var ctx        = projection.CreateContext("session", null);
        var numbered   = lines.Select((text, number) => (Text: text, Number: number)).ToArray();
        var batches    = lineByLine ? numbered.Select(l => new[] { l }).ToArray() : [numbered.OrderBy(l => projection.StepOrder(l.Text)).ToArray()];
        var events     = new List<(CanonicalEvent, int)>();

        foreach (var batch in batches) {
            ctx.BeginBatch();
            foreach (var (text, number) in batch)
                events.AddRange(projection.Project(text, number, Received, ctx).Events.Select(e => (e, e.LineNumber ?? number)));
        }

        return events;
    }

    static Task<string[]> ReadAsync(string fixture) => File.ReadAllLinesAsync(Path.Combine(SourceDir(), "Fixtures", $"antigravity-{fixture}.jsonl"));

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    static string? CreatedAt(string line) {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.Str("created_at");
    }
}
