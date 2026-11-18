using System.Text.Json;
using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit.Services;

public class SessionInterruptLogTests {
    /// One session's stream, served in pages like the events route: from is a count of events
    /// already received, and next_from is set while more follow the page.
    internal sealed class Stream {
        readonly List<SessionEventDto> _events = [];
        public readonly List<long> Froms = [];
        public int PageSize = 2;
        public bool Missing;
        public bool Failing;
        public DateTimeOffset? EndedAt;
        public int Summaries;

        public Stream Append(params string[] events) {
            foreach (var json in events)
                _events.Add(JsonSerializer.Deserialize(json.Replace("\"event_number\":0", $"\"event_number\":{_events.Count}"), RemoteModelsJsonContext.Default.SessionEventDto)!);
            return this;
        }

        public Task<SubagentEventsFetch> Read(string sessionId, long from, CancellationToken ct) {
            lock (Froms) Froms.Add(from);
            if (Missing) return Task.FromResult(new SubagentEventsFetch(null, NotFound: true));
            if (Failing) return Task.FromResult(new SubagentEventsFetch(null, Failure: "down"));
            var page = _events.Skip((int)from).Take(PageSize).ToArray();
            int? next = from + page.Length < _events.Count ? (int)from + page.Length : null;
            return Task.FromResult(new SubagentEventsFetch(new SubagentEventsDto { Events = page, NextFrom = next, LastEventNumber = _events.Count - 1 }));
        }

        public Task<SummaryFetch> Summary(string sessionId, CancellationToken ct) {
            Summaries++;
            return Task.FromResult(new SummaryFetch(ServerReadOutcome.Ok, new ActivitySessionDto {
                SessionId = sessionId, Status = EndedAt is null ? "active" : "ended", StartedAt = DateTimeOffset.UnixEpoch, EndedAt = EndedAt,
            }));
        }

        public SessionInterruptLog Log() => new(Read, Summary);
    }

    internal static string Issued(string id) =>
        $$$"""{"event_type":"InterruptIssued","event_number":0,"payload":{"request_id":"{{{id}}}","kind":"permission","tool_name":"Bash"}}""";

    internal static string Resolved(string id) =>
        $$$"""{"event_type":"InterruptResolved","event_number":0,"payload":{"request_id":"{{{id}}}","outcome":"allow"}}""";

    internal const string Line = """{"event_type":"AssistantMessage","event_number":0,"payload":{"text":"working"}}""";
    internal const string Ended = """{"event_type":"SessionEnded","event_number":0,"payload":{}}""";

    static async Task<string[]> PendingIds(SessionInterruptLog log) =>
        [.. (await log.ReadAsync("s1", CancellationToken.None)).Interrupts!.Pending.Select(p => p.RequestId)];

    /// The first read folds the whole stream, so an interrupt issued long before the tail still counts.
    [Test]
    public async Task A_first_read_folds_every_page_from_the_first_event() {
        var stream = new Stream().Append(Issued("old"), Line, Line, Line, Line);
        var log = stream.Log();

        await Assert.That(await PendingIds(log)).IsEquivalentTo(new[] { "old" });
        await Assert.That(stream.Froms).IsEquivalentTo(new long[] { 0, 2, 4 });
        await Assert.That(stream.Summaries).IsEqualTo(1);
    }

    [Test]
    public async Task Later_reads_start_from_the_last_event_seen() {
        var stream = new Stream().Append(Issued("r1"), Line, Line);
        var log = stream.Log();
        await PendingIds(log);
        stream.Froms.Clear();

        stream.Append(Issued("r2"), Resolved("r1"));
        await Assert.That(await PendingIds(log)).IsEquivalentTo(new[] { "r2" });
        await Assert.That(stream.Froms[0]).IsEqualTo(3);

        stream.Froms.Clear();
        await Assert.That(await PendingIds(log)).IsEquivalentTo(new[] { "r2" });
        await Assert.That(stream.Froms).IsEquivalentTo(new long[] { 5 });
        await Assert.That(stream.Summaries).IsEqualTo(1);
    }

    [Test]
    public async Task An_ended_session_is_answered_by_its_summary_and_never_read_again() {
        var stream = new Stream { EndedAt = DateTimeOffset.UnixEpoch }.Append(Issued("r1"));
        var log = stream.Log();

        var first = await log.ReadAsync("s1", CancellationToken.None);
        var second = await log.ReadAsync("s1", CancellationToken.None);

        await Assert.That(first.Interrupts!.Ended).IsTrue();
        await Assert.That(second.Interrupts!.Pending).IsEmpty();
        await Assert.That(stream.Froms).IsEmpty();
        await Assert.That(stream.Summaries).IsEqualTo(1);
    }

    [Test]
    public async Task An_end_in_the_stream_clears_everything_and_stops_further_reads() {
        var stream = new Stream().Append(Issued("r1"));
        var log = stream.Log();
        await PendingIds(log);
        stream.Append(Ended);

        await Assert.That(await PendingIds(log)).IsEmpty();
        stream.Froms.Clear();
        await Assert.That(await PendingIds(log)).IsEmpty();
        await Assert.That(stream.Froms).IsEmpty();
    }

    /// A failed page says nothing about the session, and a retry continues from what was folded.
    [Test]
    public async Task A_failed_read_is_a_failure_and_keeps_what_was_folded() {
        var stream = new Stream().Append(Issued("r1"), Line);
        var log = stream.Log();
        await PendingIds(log);
        stream.Failing = true;

        var failed = await log.ReadAsync("s1", CancellationToken.None);
        await Assert.That(failed.Interrupts).IsNull();
        await Assert.That(failed.NotFound).IsFalse();

        stream.Failing = false;
        stream.Froms.Clear();
        await Assert.That(await PendingIds(log)).IsEquivalentTo(new[] { "r1" });
        await Assert.That(stream.Froms).IsEquivalentTo(new long[] { 2 });
    }

    [Test]
    public async Task A_missing_session_is_not_found_and_is_read_from_the_start_when_it_returns() {
        var stream = new Stream().Append(Issued("r1"));
        var log = stream.Log();
        await PendingIds(log);
        stream.Missing = true;

        await Assert.That((await log.ReadAsync("s1", CancellationToken.None)).NotFound).IsTrue();

        stream.Missing = false;
        stream.Froms.Clear();
        await Assert.That(await PendingIds(log)).IsEquivalentTo(new[] { "r1" });
        await Assert.That(stream.Froms[0]).IsEqualTo(0);
    }

    [Test]
    public async Task Forget_starts_every_session_over() {
        var stream = new Stream().Append(Issued("r1"), Line);
        var log = stream.Log();
        await PendingIds(log);

        log.Forget();
        stream.Froms.Clear();
        await Assert.That(await PendingIds(log)).IsEquivalentTo(new[] { "r1" });
        await Assert.That(stream.Froms[0]).IsEqualTo(0);
        await Assert.That(stream.Summaries).IsEqualTo(2);
    }
}
