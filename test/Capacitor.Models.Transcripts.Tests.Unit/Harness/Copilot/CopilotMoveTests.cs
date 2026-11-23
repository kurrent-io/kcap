using System.Runtime.CompilerServices;
using Capacitor.Models.Transcripts.Harness.Copilot;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Copilot;

/// <summary>A VS Code chat moved from Local to Copilot: its transcript starts with VS Code's migration
/// <c>session.start</c>, repeats the Local chat's one turn up to the first <c>session.resume</c>, then holds its own.
/// <c>e66918ea</c> is such a chat, captured live and shared with kcap-server's corpus.</summary>
public class CopilotMoveTests {
    const string MovedId     = "e66918ea-28db-4c85-879a-f4538ce0725e";
    const string LocalId     = "fa0d6e8d-7c9d-446f-a93c-5a2a89e011b4";
    const string LocalTurn   = "MOVE-CHECK step 1 (Local): Add a multiply(a, b) function to calc.py.";
    const string MovedTurn   = "MOVE-CHECK step 2 (Copilot): Add a divide(a, b) function to calc.py that raises ValueError on division by zero.";
    const int    FirstResume = 27;

    static readonly DateTimeOffset Received = new(2026, 11, 18, 18, 0, 0, TimeSpan.Zero);

    static CopilotContext Context() => (CopilotContext)CopilotTranscriptEvents.Instance.CreateContext(MovedId.Replace("-", ""), null);

    static List<CanonicalEvent> Project(IEnumerable<string> lines, CopilotContext? ctx = null, int first = 0) {
        var context = ctx ?? Context();
        return [.. lines.SelectMany((line, n) => CopilotTranscriptEvents.Instance.Project(line, first + n, Received, context).Events)];
    }

    static string[] Corpus() => File.ReadAllLines(Path.Combine(SourceDir(), "Fixtures", "e66918ea.jsonl"));

    static string Marker(string sessionId) =>
        $$"""{"type":"{{CopilotTranscriptEvents.MovedFromMarker}}","data":{"sessionId":"{{sessionId}}"},"id":"00000000-0000-4000-8000-000000000501","timestamp":"2026-11-18T17:53:40.900Z","parentId":null}""";

    static string[] WithMarker(string sessionId) {
        var lines = Corpus().ToList();
        lines.Insert(FirstResume + 1, Marker(sessionId));

        return [.. lines];
    }

    static IEnumerable<string> Prompts(IEnumerable<CanonicalEvent> events) =>
        events.Select(e => e.Payload).OfType<UserMessageReceived>().Select(m => m.Content);

    static IEnumerable<string> Links(IEnumerable<CanonicalEvent> events) =>
        events.Select(e => e.Payload).OfType<SessionStarted>().Where(s => s.HasPreviousSessionId).Select(s => s.PreviousSessionId);

    [Test]
    public async Task A_moved_chat_keeps_its_own_turn_and_none_of_the_local_history() =>
        await Assert.That(Prompts(Project(Corpus()))).IsEquivalentTo(new[] { MovedTurn });

    // The same lines from another producer are the chat's own: the drop is keyed on the migration.
    [Test]
    public async Task A_transcript_not_moved_keeps_its_lines_before_a_resume() {
        var lines = Corpus();
        lines[0] = lines[0].Replace(CopilotTranscriptEvents.MovedFromLocalProducer, "copilot-agent", StringComparison.Ordinal);

        await Assert.That(Prompts(Project(lines))).IsEquivalentTo(new[] { LocalTurn, MovedTurn });
    }

    [Test]
    public async Task The_marker_links_the_moved_chat_to_its_local_session() {
        var events = Project(WithMarker(LocalId));
        var link   = events.Single(e => e.Payload is SessionStarted { HasPreviousSessionId: true });

        await Assert.That(Links(events).Single()).IsEqualTo(LocalId.Replace("-", ""));
        await Assert.That(link.VendorMetadata).IsEquivalentTo(new Dictionary<string, string> { [CopilotTranscriptEvents.MoveMarkerKey] = "true" });
    }

    [Test]
    [Arguments(MovedId)]
    [Arguments("")]
    public async Task A_marker_naming_no_other_session_links_nothing(string named) =>
        await Assert.That(Links(Project(WithMarker(named)))).IsEmpty();

    // A caller that stored the link and the history's end sends the file again from the line after the start.
    [Test]
    public async Task A_rebuilt_context_drops_the_history_sent_again() {
        var ctx = Context();
        ctx.Seed(isFork: false, transcriptStart: DateTimeOffset.Parse("2026-11-18T17:51:27.399Z"), movedFromLocal: true, moveMarked: true, movedHistoryEndsAtLine: FirstResume);

        await Assert.That(Prompts(Project(Corpus().Skip(1), ctx, first: 1))).IsEquivalentTo(new[] { MovedTurn });
    }

    // Without the stored end, the history runs to the next resume, as on the first read.
    [Test]
    public async Task A_rebuilt_context_with_no_recorded_end_holds_back_to_the_resume() {
        var ctx = Context();
        ctx.Seed(isFork: false, transcriptStart: DateTimeOffset.Parse("2026-11-18T17:51:27.399Z"), movedFromLocal: true);

        await Assert.That(Prompts(Project(Corpus().Skip(1), ctx, first: 1))).IsEquivalentTo(new[] { MovedTurn });
    }

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    [Test]
    public async Task A_marker_reaching_a_context_rebuilt_inside_the_history_ends_it() {
        var ctx = Context();
        ctx.Seed(isFork: false, transcriptStart: DateTimeOffset.Parse("2026-11-18T17:51:27.399Z"), movedFromLocal: true);

        var events = Project([Marker(LocalId), .. Corpus()[(FirstResume + 1)..]], ctx, first: FirstResume + 1);

        await Assert.That(Prompts(events)).IsEquivalentTo(new[] { MovedTurn });
        await Assert.That(Links(events).Single()).IsEqualTo(LocalId.Replace("-", ""));
    }
}
