using System.Runtime.CompilerServices;
using System.Text.Json;
using Capacitor.Models.Transcripts.Harness.Copilot;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Copilot;

/// <summary>A forked Copilot chat: its file starts with its own <c>session.start</c>, repeats its parent's lines with
/// their original times and ids, then carries the marker naming the parent and its own lines. <c>3aa78294</c> is such a
/// fork from kcap-server's corpus; its parent had spent 11366085000 nano-AIU when forked, and the fork's one model call
/// took that to 58658885000.</summary>
public class CopilotForkTests {
    const string ParentId = "d6c19ec2-5a16-47b2-a311-a59ed6609a25";
    const string ForkId   = "3aa78294-78c5-4ea7-bd0d-97a6b61a8197";
    const string OtherId  = "e9bd3e16-07ec-460e-9dc7-5cb1e67eb340";
    const string StartAt  = "2026-11-05T04:00:00.000Z";

    static readonly DateTimeOffset Received = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    static string N(string id) => id.Replace("-", "");

    static CopilotContext Context(string id = ForkId) => (CopilotContext)CopilotTranscriptEvents.Instance.CreateContext(N(id), null);

    static List<CanonicalEvent> Project(IEnumerable<string> lines, CopilotContext? ctx = null) {
        var context = ctx ?? Context();
        return [.. lines.SelectMany((line, n) => CopilotTranscriptEvents.Instance.Project(line, n, Received, context).Events)];
    }

    static string[] Corpus() => File.ReadAllLines(Path.Combine(SourceDir(), "Fixtures", "3aa78294.jsonl"));

    static IEnumerable<string> Links(IEnumerable<CanonicalEvent> events) =>
        events.Select(e => e.Payload).OfType<SessionStarted>().Where(s => s.HasPreviousSessionId).Select(s => s.PreviousSessionId);

    static IEnumerable<T> Of<T>(IEnumerable<CanonicalEvent> events) => events.Select(e => e.Payload).OfType<T>();

    static string Start(string? startTime = StartAt) => startTime is null
        ? $$$"""{"type":"session.start","data":{"sessionId":"{{{ForkId}}}","context":{}},"id":"00000000-0000-4000-8000-000000000001","timestamp":"{{{StartAt}}}"}"""
        : $$$"""{"type":"session.start","data":{"sessionId":"{{{ForkId}}}","startTime":"{{{startTime}}}","context":{}},"id":"00000000-0000-4000-8000-000000000001","timestamp":"{{{StartAt}}}"}""";

    static string Line(int n, string type, string data, string at) =>
        $$"""{"type":"{{type}}","data":{{data}},"id":"00000000-0000-4000-8000-0000000002{{n:D2}}","timestamp":"{{at}}","parentId":null}""";

    static string Copied(int n, string type, string data) => Line(n, type, data, $"2026-11-05T03:00:{n:D2}.000Z");

    static string Marker(string message, string at = "2026-11-05T04:00:00.002Z") =>
        $$$"""{"type":"session.info","id":"00000000-0000-4000-8000-000000000099","timestamp":"{{{at}}}","parentId":null,"data":{"infoType":"fork","message":{{{JsonSerializer.Serialize(message)}}}}}""";

    static string ForkedFrom(string parent = ParentId, string fork = ForkId) => $"Forked from \"Parent\" ({parent}) as \"Fork\" ({fork}).";

    [Test]
    public async Task A_fork_keeps_its_own_prompts_and_replies_and_none_of_its_parents() {
        var lines  = Corpus();
        var events = Project(lines);

        await Assert.That(lines.Take(141).Count(l => l.Contains("\"type\":\"user.message\"", StringComparison.Ordinal))).IsEqualTo(6);
        await Assert.That(Of<UserMessageReceived>(events).Select(m => m.Content)).IsEquivalentTo(new[] { "What was my first prompt?", "What was my first prompt?" });
        await Assert.That(Of<AssistantTextGenerated>(events).Count()).IsEqualTo(1);
    }

    [Test]
    public async Task A_forks_marker_links_it_to_its_parent_and_is_marked_for_the_caller() {
        var link = Project(Corpus()).Single(e => e.Payload is SessionStarted { HasPreviousSessionId: true });

        await Assert.That(((SessionStarted)link.Payload).PreviousSessionId).IsEqualTo(N(ParentId));
        await Assert.That(link.EventId).IsEqualTo(Guid.Parse("3a6d8623-62ab-4845-a98a-7c31781edd4f"));
        await Assert.That(link.VendorMetadata).IsEquivalentTo(new Dictionary<string, string> { [CopilotTranscriptEvents.ForkMarkerKey] = "true" });
        await Assert.That(SchemaExtensions.Text(SchemaExtensions.Slug(link.Payload, CopilotExtension.Slug), "source")).IsEqualTo("fork");
    }

    /// <summary>The fork's one model call, and the totals with the parent's spend taken off them.</summary>
    [Test]
    public async Task A_forks_usage_and_credits_are_its_own() {
        var events = Project(Corpus());

        await Assert.That(Of<CopilotUsageBackfill>(events).Select(b => (b.Model, b.InputTokens, b.OutputTokens, b.CacheReadTokens, b.CacheWriteTokens)))
            .IsEquivalentTo(new[] { ((string?)"claude-sonnet-5.5", 4L, 25L, 0L, 189068L), ("claude-sonnet-5.5", 0L, 0L, 0L, 0L) });
        await Assert.That(Of<CopilotUsageBackfill>(events).All(b => b is { NanoAiu: null, RequestCost: null, RequestCount: null })).IsTrue();
        await Assert.That(Of<CopilotSessionMetrics>(events).Select(m => m.TotalNanoAiu).Distinct()).IsEquivalentTo(new long?[] { 47_292_800_000L });
    }

    [Test]
    public async Task A_parents_marker_saying_it_was_forked_into_another_session_links_nothing() =>
        await Assert.That(Links(Project([Start(), Marker($"Forked this session into \"Parent (fork)\" ({ForkId}).")], Context(ParentId)))).IsEmpty();

    [Test]
    [Arguments($"Forked from \"Parent\" ({ParentId}) as \"Parent (fork)\" ({ForkId}).", true)]
    [Arguments($"Forked from \"Parent\" ({ParentId}) before event 9ca4bebc-0000-4000-8000-000000000000 as \"Parent (fork)\" ({ForkId}).", true)]
    [Arguments($"Forked from {ParentId} as {ForkId}.", true)]
    [Arguments($"Forked from {ParentId} before event 9ca4bebc-0000-4000-8000-000000000000 as \"Named\" ({ForkId}).", true)]
    [Arguments($"Forked from \"a (11111111-1111-4111-8111-111111111111) b\" ({ParentId}) as \"c\" ({ForkId}).", true)]
    [Arguments($"Forked this session into \"Parent (fork)\" ({ForkId}).", false)]
    [Arguments($"Forked from \"Parent\" ({ParentId}) as \"Someone else\" ({OtherId}).", false)]
    [Arguments($"Forked from \"Self\" ({ForkId}) as \"Self\" ({ForkId}).", false)]
    public async Task Only_a_marker_saying_this_session_was_forked_links_it(string message, bool links) =>
        await Assert.That(Links(Project([Marker(message)])).SingleOrDefault()).IsEqualTo(links ? N(ParentId) : null);

    /// <summary>The caller may name the stream dashed or not; the marker names the session either way.</summary>
    [Test]
    public async Task A_dashed_session_id_still_matches_the_marker() {
        var ctx = (CopilotContext)CopilotTranscriptEvents.Instance.CreateContext(ForkId, null);

        await Assert.That(Links(Project([Marker(ForkedFrom())], ctx)).Single()).IsEqualTo(N(ParentId));
    }

    [Test]
    public async Task A_fork_of_a_fork_links_to_its_immediate_parent() {
        var lines = new[] {
            Start(),
            Marker($"Forked from \"Root\" ({OtherId}) as \"Middle\" ({ParentId}).", "2026-11-05T03:50:00.000Z"),
            Marker($"Forked from \"Middle\" ({ParentId}) as \"Leaf\" ({ForkId})."),
        };

        await Assert.That(Links(Project(lines)).Single()).IsEqualTo(N(ParentId));
    }

    /// <summary>Older transcripts carry no start time, so none of their lines can be told to be copied.</summary>
    [Test]
    public async Task A_start_without_a_start_time_holds_nothing_back() {
        var lines = new[] { Start(startTime: null), Copied(1, "user.message", """{"content":"hello"}""") };

        await Assert.That(Of<UserMessageReceived>(Project(lines)).Count()).IsEqualTo(1);
    }

    /// <summary>The copy can end on usage the parent had not totalled yet, so its last total is not what the fork's
    /// include, and the fork reports none.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_fork_whose_parents_spend_is_not_known_reports_no_totals(bool totalFirst) {
        var lines = new List<string> { Start() };
        if (totalFirst) lines.Add(Copied(1, "session.usage_checkpoint", """{"totalNanoAiu":100,"totalPremiumRequests":1}"""));
        lines.Add(Copied(2, "session.usage_record", """{"usage":{"model":"claude-haiku-4.5","inputTokens":10,"outputTokens":1}}"""));
        lines.Add(Marker(ForkedFrom()));
        lines.Add(Line(3, "session.usage_checkpoint", """{"totalNanoAiu":500,"totalPremiumRequests":3}""", "2026-11-05T04:01:00.000Z"));

        await Assert.That(Of<CopilotSessionMetrics>(Project(lines))).IsEmpty();
    }

    /// <summary>Premium requests are fractional; a resumed fork's totals stay session-wide, so each takes the parent's
    /// last total off.</summary>
    [Test]
    public async Task A_forks_totals_leave_out_the_parents_last_total() {
        const string marks = """ "usageAccountingWatermarks":{"p":9,"f":1} """;
        var lines = new[] {
            Start(),
            Copied(1, "session.usage_checkpoint", """{"totalNanoAiu":100,"totalPremiumRequests":1.32}"""),
            Marker(ForkedFrom()),
            Line(2, "session.usage_checkpoint", $$"""{ {{marks}}, "totalNanoAiu":150,"totalPremiumRequests":1.65}""", "2026-11-05T04:01:00.000Z"),
            Line(3, "session.resume", "{}", "2026-11-05T05:00:00.000Z"),
            Line(4, "session.usage_checkpoint", $$"""{ {{marks}}, "totalNanoAiu":180,"totalPremiumRequests":1.98}""", "2026-11-05T05:01:00.000Z"),
        };

        var metrics = Of<CopilotSessionMetrics>(Project(lines)).ToList();

        await Assert.That(metrics.Select(m => m.TotalNanoAiu)).IsEquivalentTo(new long?[] { 50, 80 });
        await Assert.That(metrics[0].TotalPremiumRequests!.Value).IsEqualTo(0.33).Within(1e-9);
        await Assert.That(metrics[1].TotalPremiumRequests!.Value).IsEqualTo(0.66).Within(1e-9);
        await Assert.That(metrics.Select(m => m.RunMarker)).IsEquivalentTo(new Guid?[] {
            Guid.Parse("00000000-0000-4000-8000-000000000001"), Guid.Parse("00000000-0000-4000-8000-000000000203"),
        });
    }

    /// <summary>Copilot's code changes and per-model figures cannot be split into the parent's and the fork's.</summary>
    [Test]
    public async Task A_forks_shutdown_reports_no_code_changes_and_only_its_current_models_tokens() {
        var lines = new[] {
            Start(),
            Copied(4, "session.usage_checkpoint", """{"totalNanoAiu":2}"""),
            Marker(ForkedFrom()),
            Line(3, "session.shutdown",
                """{"currentModel":"claude-haiku-4.5","totalNanoAiu":5,"codeChanges":{"linesAdded":40,"linesRemoved":2,"filesModified":["/tmp/parent.txt"]},"modelMetrics":{"claude-sonnet-4.6":{"usage":{"inputTokens":9}},"claude-haiku-4.5":{"requests":{"count":2,"cost":1},"usage":{"inputTokens":7},"totalNanoAiu":5}}}""",
                "2026-11-05T04:01:00.000Z"),
        };

        var events  = Project(lines);
        var metrics = Of<CopilotSessionMetrics>(events).Single();

        await Assert.That((metrics.TotalNanoAiu, metrics.LinesAdded, metrics.LinesRemoved, metrics.FilesModified)).IsEqualTo((3L, null, null, null));
        await Assert.That(Of<CopilotUsageBackfill>(events).Single()).IsEqualTo(new CopilotUsageBackfill { Model = "claude-haiku-4.5", SessionModel = "claude-haiku-4.5" });
    }

    // ── a context rebuilt from what the caller stored ──────────────────────────────────────────────

    /// <summary>With no start stored, nothing tells a copied line from the fork's own until the start comes again.</summary>
    [Test]
    public async Task A_context_seeded_with_no_start_holds_every_line_until_the_session_starts() {
        var ctx = Context();
        ctx.Seed(isFork: false, transcriptStart: null);

        var before = Project([Line(1, "user.message", """{"content":"own?"}""", "2026-11-05T04:30:00.000Z")], ctx);
        var after  = Project([Start(), Line(2, "user.message", """{"content":"own"}""", "2026-11-05T04:30:00.000Z")], ctx);

        await Assert.That(before).IsEmpty();
        await Assert.That(Of<UserMessageReceived>(after).Single().Content).IsEqualTo("own");
    }

    [Test]
    public async Task A_context_seeded_with_the_stored_start_holds_back_the_lines_before_it() {
        var ctx = Context();
        ctx.Seed(isFork: false, transcriptStart: DateTimeOffset.Parse(StartAt));

        var events = Project([Copied(1, "user.message", """{"content":"parent's"}"""), Line(2, "user.message", """{"content":"own"}""", "2026-11-05T04:30:00.000Z")], ctx);

        await Assert.That(Of<UserMessageReceived>(events).Select(m => m.Content)).IsEquivalentTo(new[] { "own" });
    }

    /// <summary>The parent's spend is not stored anywhere the fork's caller reads, so a rebuilt fork reports no totals.</summary>
    [Test]
    public async Task A_context_seeded_as_a_fork_reports_no_totals_and_no_code_changes() {
        var ctx = Context();
        ctx.Seed(isFork: true, transcriptStart: DateTimeOffset.Parse(StartAt));

        var events = Project([Line(1, "session.shutdown", """{"totalNanoAiu":5,"codeChanges":{"linesAdded":4}}""", "2026-11-05T04:30:00.000Z")], ctx);

        await Assert.That(events).IsEmpty();
    }

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
