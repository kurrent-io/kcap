using System.Text.Json;
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

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
        /// Positions the server counts but withholds from this viewer, as it does a hidden continuation link.
        public readonly HashSet<int> Withheld = [];
        /// When set, every page waits on it.
        public TaskCompletionSource? Hold;

        public Stream Append(params string[] events) {
            foreach (var json in events)
                _events.Add(JsonSerializer.Deserialize(json.Replace("\"event_number\":0", $"\"event_number\":{_events.Count}"), RemoteModelsJsonContext.Default.SessionEventDto)!);
            return this;
        }

        public async Task<StreamEventsFetch> Read(string sessionId, long from, int? limit, int? tail, CancellationToken ct) {
            lock (Froms) Froms.Add(from);
            if (Hold is { } hold) await hold.Task.WaitAsync(ct);
            if (Missing) return new StreamEventsFetch(null, NotFound: true);
            if (Failing) return new StreamEventsFetch(null, Failure: "down");
            var raw = Enumerable.Range((int)from, Math.Max(0, Math.Min(PageSize, _events.Count - (int)from))).ToArray();
            var page = raw.Where(i => !Withheld.Contains(i)).Select(i => _events[i]).ToArray();
            int? next = from + raw.Length < _events.Count ? (int)from + raw.Length : null;
            return new StreamEventsFetch(new StreamEventsPage { Events = page, NextFrom = next, LastEventNumber = _events.Count - 1 });
        }

        public int PendingReads;
        /// Answers the pending route as a server without it would.
        public bool NoPendingRoute;

        /// The server's own fold of the whole stream, with the count to continue from.
        public Task<PendingInterruptsFetch> Pending(string sessionId, CancellationToken ct) {
            PendingReads++;
            if (Missing || NoPendingRoute) return Task.FromResult(new PendingInterruptsFetch(null, NotFound: true));
            if (Failing) return Task.FromResult(new PendingInterruptsFetch(null, Failure: "down"));
            var folded = InterruptReconciliation.Fold(null, _events);
            var open = _events.Where(e => e.EventType == "InterruptIssued" && folded.Pending.Any(p => p.RequestId == e.Body?.GetProperty("request_id").GetString()));
            return Task.FromResult(new PendingInterruptsFetch(new PendingInterruptsPage {
                LastEventNumber = _events.Count - 1, NextFrom = _events.Count, Ended = folded.Ended,
                Interrupts = [.. open.Select(e => new PendingInterruptDto { RequestId = e.Body?.GetProperty("request_id").GetString(), Event = e })],
            }));
        }

        public Task<SummaryFetch> Summary(string sessionId, CancellationToken ct) {
            Summaries++;
            return Task.FromResult(new SummaryFetch(ServerReadOutcome.Ok, new ActivitySessionDto {
                SessionId = sessionId, Status = EndedAt is null ? "active" : "ended", StartedAt = DateTimeOffset.UnixEpoch, EndedAt = EndedAt,
            }));
        }

        public SessionInterruptLog Log(IServerLane? lane = null, bool summary = true) => new(Read, lane, summary ? Summary : null);

        public SessionInterruptLog SeededLog(IServerLane? lane = null) => new(Read, lane, readPending: Pending);
    }

    internal static string Issued(string id) =>
        $$$"""{"event_type":"InterruptIssued","event_number":0,"payload":{"request_id":"{{{id}}}","kind":"permission","tool_name":"Bash"}}""";

    internal static string Resolved(string id) =>
        $$$"""{"event_type":"InterruptResolved","event_number":0,"payload":{"request_id":"{{{id}}}","outcome":"allow"}}""";

    internal const string Line = """{"event_type":"AssistantMessage","event_number":0,"payload":{"text":"working"}}""";
    internal const string Ended = """{"event_type":"SessionEnded","event_number":0,"payload":{}}""";
    internal const string Resumed = """{"event_type":"SessionStarted","event_number":0,"payload":{}}""";

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

    /// The server's pending set stands in for the stream before it: an interrupt raised long before the
    /// tail still counts, and the fold continues from the position the set holds for.
    [Test]
    public async Task A_first_read_starts_from_the_servers_pending_set() {
        var stream = new Stream().Append(Issued("old"), Issued("done"), Line, Resolved("done"), Line, Line);
        var log = stream.SeededLog();

        await Assert.That(await PendingIds(log)).IsEquivalentTo(new[] { "old" });
        await Assert.That(stream.Froms).IsEquivalentTo(new long[] { 6 });

        stream.Froms.Clear();
        stream.Append(Resolved("old"), Issued("new"));
        await Assert.That(await PendingIds(log)).IsEquivalentTo(new[] { "new" });
        await Assert.That(stream.Froms[0]).IsEqualTo(6);
        await Assert.That(stream.PendingReads).IsEqualTo(1);
    }

    /// An ended session in the pending set reopens when the events after it resume it.
    [Test]
    public async Task A_pending_set_that_says_ended_is_continued_through_a_resume() {
        var stream = new Stream().Append(Issued("r1"), Ended);
        var log = stream.SeededLog();
        await Assert.That((await log.ReadAsync("s1", CancellationToken.None)).Interrupts!.Ended).IsTrue();

        stream.Append(Resumed, Issued("r2"));
        await Assert.That(await PendingIds(log)).IsEquivalentTo(new[] { "r2" });
    }

    /// A server without the route answers 404, as it would for a missing session: the stream is folded from
    /// its first event, and that read answers for the session.
    [Test]
    public async Task Without_a_pending_route_the_stream_is_folded_from_its_first_event() {
        var stream = new Stream { NoPendingRoute = true }.Append(Issued("old"), Line, Line);
        var log = stream.SeededLog();

        await Assert.That(await PendingIds(log)).IsEquivalentTo(new[] { "old" });
        await Assert.That(stream.Froms).IsEquivalentTo(new long[] { 0, 2 });
    }

    [Test]
    public async Task A_missing_session_is_not_found_when_seeded() {
        var stream = new Stream { Missing = true }.Append(Issued("r1"));
        var log = stream.SeededLog();

        await Assert.That((await log.ReadAsync("s1", CancellationToken.None)).NotFound).IsTrue();
    }

    /// A failed pending read falls back to the stream rather than failing the reconcile.
    [Test]
    public async Task A_failed_pending_read_folds_the_stream_instead() {
        var stream = new Stream().Append(Issued("r1"), Line);
        var log = new SessionInterruptLog(stream.Read, readPending: (_, _) => Task.FromResult(new PendingInterruptsFetch(null, Failure: "down")));

        await Assert.That(await PendingIds(log)).IsEquivalentTo(new[] { "r1" });
        await Assert.That(stream.Froms[0]).IsEqualTo(0);
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

    /// A summary's end is not kept: the session can resume under the same id.
    [Test]
    public async Task An_ended_session_is_answered_by_its_summary_without_reading_the_stream() {
        var stream = new Stream { EndedAt = DateTimeOffset.UnixEpoch }.Append(Issued("r1"));
        var log = stream.Log();

        await Assert.That((await log.ReadAsync("s1", CancellationToken.None)).Interrupts!.Ended).IsTrue();
        await Assert.That(stream.Froms).IsEmpty();

        stream.EndedAt = null;
        await Assert.That(await PendingIds(log)).IsEquivalentTo(new[] { "r1" });
        await Assert.That(stream.Summaries).IsEqualTo(2);
    }

    [Test]
    public async Task An_end_clears_what_was_pending_and_a_resume_under_the_same_id_opens_it_again() {
        var stream = new Stream().Append(Issued("r1"));
        var log = stream.Log();
        await PendingIds(log);

        stream.Append(Ended);
        var ended = await log.ReadAsync("s1", CancellationToken.None);
        await Assert.That(ended.Interrupts!.Ended).IsTrue();
        await Assert.That(ended.Interrupts.Pending).IsEmpty();

        stream.Froms.Clear();
        stream.Append(Resumed, Issued("r2"));
        await Assert.That(await PendingIds(log)).IsEquivalentTo(new[] { "r2" });
        await Assert.That(stream.Froms[0]).IsEqualTo(2);
    }

    /// A read that arrives during a first scan waits for it and then reads only what follows it,
    /// rather than scanning the whole stream a second time beside it.
    [Test]
    public async Task A_read_during_a_first_scan_continues_from_where_the_scan_stopped() {
        var stream = new Stream { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) }.Append(Issued("r1"), Line, Line, Line);
        var log = stream.Log();
        var first = log.ReadAsync("s1", CancellationToken.None);
        await WaitUntilAsync(() => { lock (stream.Froms) return stream.Froms.Count == 1; }, what: "scan started");
        var second = log.ReadAsync("s1", CancellationToken.None);
        await Task.Delay(50);
        lock (stream.Froms) stream.Froms.Add(-1);
        stream.Hold.SetResult();

        await Task.WhenAll(first, second);
        await Assert.That((await second).Interrupts!.Pending.Select(p => p.RequestId)).IsEquivalentTo(new[] { "r1" });
        await Assert.That(string.Join(",", stream.Froms)).IsEqualTo("0,-1,2,4");
    }

    /// The server counts events it withholds, so its cursor is followed even past a page that came back empty.
    [Test]
    public async Task The_servers_cursor_is_followed_past_withheld_events() {
        var stream = new Stream().Append(Line, Line, Issued("r1"));
        stream.Withheld.UnionWith([0, 1]);
        var log = stream.Log();

        await Assert.That(await PendingIds(log)).IsEquivalentTo(new[] { "r1" });
        await Assert.That(stream.Froms).IsEquivalentTo(new long[] { 0, 2 });

        stream.Froms.Clear();
        stream.Append(Resolved("r1"));
        await Assert.That(await PendingIds(log)).IsEmpty();
        await Assert.That(stream.Froms[0]).IsEqualTo(3);
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

    /// A session the listing shows ended needs neither its summary nor its stream, and the answer is
    /// not kept: the next read asks the listing again.
    [Test]
    public async Task A_session_listed_ended_is_answered_without_a_request() {
        var stream = new Stream().Append(Issued("r1"));
        bool? listed = true;
        var log = new SessionInterruptLog(stream.Read, readSummary: stream.Summary, listedEnded: _ => listed);

        await Assert.That((await log.ReadAsync("s1", CancellationToken.None)).Interrupts!.Ended).IsTrue();
        await Assert.That(stream.Summaries).IsEqualTo(0);
        await Assert.That(stream.Froms).IsEmpty();

        listed = null;
        await Assert.That(await PendingIds(log)).IsEquivalentTo(new[] { "r1" });
        await Assert.That(stream.Summaries).IsEqualTo(1);
    }

    /// The listing outranks a fold already kept, which may not yet have read the end the listing reflects.
    [Test]
    public async Task A_folded_session_later_listed_ended_is_answered_ended_without_a_request() {
        var stream = new Stream().Append(Issued("r1"));
        bool? listed = false;
        var log = new SessionInterruptLog(stream.Read, readSummary: stream.Summary, listedEnded: _ => listed);
        await Assert.That(await PendingIds(log)).IsEquivalentTo(new[] { "r1" });

        listed = true;
        stream.Froms.Clear();
        var answer = (await log.ReadAsync("s1", CancellationToken.None)).Interrupts!;
        await Assert.That(answer.Ended).IsTrue();
        await Assert.That(answer.Pending).IsEmpty();
        await Assert.That(stream.Froms).IsEmpty();
    }

    [Test]
    public async Task A_session_listed_live_skips_the_summary_and_folds_the_stream() {
        var stream = new Stream().Append(Issued("r1"));
        var log = new SessionInterruptLog(stream.Read, readSummary: stream.Summary, listedEnded: _ => false);

        await Assert.That(await PendingIds(log)).IsEquivalentTo(new[] { "r1" });
        await Assert.That(stream.Summaries).IsEqualTo(0);
    }

    /// The events route counts from the first surviving event, so a cursor kept across a deletion
    /// could skip a recreated stream's events.
    [Test]
    public async Task A_deleted_session_is_folded_again_from_its_first_event() {
        var lane = new FakeServerLane();
        var stream = new Stream().Append(Issued("r1"), Line);
        using var log = stream.Log(lane);
        await PendingIds(log);

        lane.SessionPushesSubject.OnNext(new SessionPush(SessionPushKind.Deleted, "s1"));
        stream.Froms.Clear();
        await PendingIds(log);
        await Assert.That(stream.Froms[0]).IsEqualTo(0);
    }

    /// A deletion pushed while the lane was down is never seen, so a reconnect starts every session over.
    [Test]
    public async Task A_reconnect_folds_every_session_again_from_its_first_event() {
        var lane = new FakeServerLane();
        lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 1));
        var stream = new Stream().Append(Issued("r1"), Line);
        using var log = stream.Log(lane);
        await PendingIds(log);

        stream.Froms.Clear();
        await PendingIds(log);
        await Assert.That(stream.Froms[0]).IsEqualTo(2);

        lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
        lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 2));
        stream.Froms.Clear();
        await PendingIds(log);
        await Assert.That(stream.Froms[0]).IsEqualTo(0);
    }

    [Test]
    public async Task Sessions_past_the_capacity_are_evicted_least_recently_read_first() {
        var stream = new Stream().Append(Issued("r1"), Line);
        using var log = stream.Log();
        for (var i = 0; i < SessionInterruptLog.Capacity; i++) await log.ReadAsync($"s{i}", CancellationToken.None);
        // Rereading the oldest makes s1 the least recently read, so a FIFO cache would evict s0 instead.
        await log.ReadAsync("s0", CancellationToken.None);
        await log.ReadAsync($"s{SessionInterruptLog.Capacity}", CancellationToken.None);

        stream.Froms.Clear();
        await log.ReadAsync("s0", CancellationToken.None);
        await Assert.That(stream.Froms[0]).IsEqualTo(2);
        stream.Froms.Clear();
        await log.ReadAsync("s1", CancellationToken.None);
        await Assert.That(stream.Froms[0]).IsEqualTo(0);
    }
}
