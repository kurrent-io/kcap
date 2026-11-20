using System.Globalization;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Remote.Models;
using Eventuous.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.RemoteFixtures;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.ViewModels;

/// The remote feed: one seed of the session's last events, a tail from the seed's last event number,
/// resumption from the last position on re-establishment, older pages on request, and the two refusals.
public class RemoteTranscriptFeedTests {
    sealed class Harness : IDisposable {
        public readonly FakeServerLane Lane = new();
        public readonly BehaviorSubject<SessionAccessState> Access = new(SessionAccessState.Establishing);
        public readonly FakeTimeProvider Time = new();
        public readonly List<string> Logged = [];
        public SessionDetailFetch NextDetail = new(Detail(
            Event(0, CanonicalEventTypes.UserMessageReceived, Hello),
            Event(1, CanonicalEventTypes.AssistantTextGenerated, HiThere)));
        public int Reads;
        public readonly RemoteTranscriptFeed Feed;
        readonly string _sessionId = "s1";

        public Harness(string vendor = "gemini") =>
            Feed = new RemoteTranscriptFeed(_sessionId, vendor, Access, FakeSessionEvents.Paged(_ => { Interlocked.Increment(ref Reads); return Task.FromResult(NextDetail); }), Lane, Time, Logged.Add);

        public string Stream => StreamNames.AgentSession(_sessionId);
        public void Dispose() => Feed.Dispose();
    }

    [Test]
    public async Task A_hidden_local_command_receipt_survives_the_remote_feed() {
        using var h = new Harness(vendor: "claude");
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        h.Feed.ReadAppended();
        h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.UserMessageReceived,
            """{"content":"<command-name>/rename</command-name><command-args>a &lt; b</command-args>","extensions":{"claude_code":{"local_command":"/rename a < b"}}}"""));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the receipt");
        var line = h.Feed.ReadAppended().Lines.Single();
        await Assert.That(line.Offset).IsEqualTo(2);
        await Assert.That(line.Projection.SubmittedInputs).IsEquivalentTo(new[] { "/rename a < b" });
        await Assert.That(line.Projection.Envelopes).IsEmpty();
        await Assert.That(line.Projection.LiteralInputs).IsEmpty();
    }

    [Test]
    public async Task Seeds_from_the_events_route_then_tails_from_the_last_event_number() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        var seedReads = h.Reads;
        await Assert.That(h.Lane.Tails[0]).IsEqualTo((h.Stream, (ulong?)1));

        var seed = h.Feed.ReadAppended();
        await Assert.That(seed.Status).IsEqualTo(FeedStatus.Reset);
        await Assert.That(seed.Lines.Select(l => l.Offset)).IsEquivalentTo(new long[] { 0, 1 });
        await Assert.That(seed.Lines[0].Projection.SubmittedInputs).IsEquivalentTo(new[] { "hello" });
        await Assert.That(seed.SnapshotOffset).IsEqualTo(2);
        await Assert.That(h.Feed.CurrentOffset).IsEqualTo(2);

        h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.AssistantToolCallsGenerated, LsCall));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the live event");
        var live = h.Feed.ReadAppended();
        await Assert.That(live.Status).IsEqualTo(FeedStatus.Ok);
        await Assert.That(live.Lines.Single().Offset).IsEqualTo(2);
        await Assert.That(live.Lines.Single().Projection.Envelopes[0].ToolCallId).IsEqualTo("t1");
        await Assert.That(h.Reads).IsEqualTo(seedReads);
        await Assert.That(h.Feed.ReadAppended().Lines).IsEmpty();
    }

    /// The seed's boundary is where its replayed history ends. An event that beats the drain is
    /// not part of that history — folded into the Reset, it could no longer acknowledge a send.
    [Test]
    public async Task A_live_event_that_lands_before_the_seed_is_drained_follows_it_in_the_next_read() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.AssistantTextGenerated, """{"content":"more"}"""));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the live event");

        var seed = h.Feed.ReadAppended();
        await Assert.That(seed.Status).IsEqualTo(FeedStatus.Reset);
        await Assert.That(seed.Lines.Select(l => l.Offset)).IsEquivalentTo(new long[] { 0, 1 });
        await Assert.That(seed.SnapshotOffset).IsEqualTo(2);

        var live = h.Feed.ReadAppended();
        await Assert.That(live.Status).IsEqualTo(FeedStatus.Ok);
        await Assert.That(live.Lines.Single().Offset).IsEqualTo(2);
    }

    /// What the seed saw scheduled carries into the tail, where the wake-up's prompt comes back.
    [Test]
    public async Task A_wake_up_scheduled_in_the_seed_hides_its_prompt_in_the_tail() {
        using var h = new Harness(vendor: "claude") {
            NextDetail = new(Detail(Event(0, CanonicalEventTypes.AssistantToolCallsGenerated,
                """{"tool_calls":[{"call_id":"t1","tool_name":"ScheduleWakeup","arguments":{"prompt":"check CI"}}]}"""))),
        };
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        h.Feed.ReadAppended();
        h.Lane.PushStreamEvent(Envelope("s1", 1, CanonicalEventTypes.UserMessageReceived, """{"content":"check CI"}"""));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 2, what: "the prompt");
        var prompt = h.Feed.ReadAppended().Lines.Single().Projection;
        await Assert.That(prompt.Envelopes).IsEmpty();
        await Assert.That(prompt.SubmittedInputs).IsEmpty();
    }

    /// A re-tail after a reconnect can overlap a subscription the server still holds, so an event at or before the
    /// position already read is not a second row.
    [Test]
    public async Task An_event_at_a_position_already_read_adds_no_row() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        h.Feed.ReadAppended();

        h.Lane.PushStreamEvent(Envelope("s1", 1, CanonicalEventTypes.AssistantTextGenerated, """{"content":"again"}"""));
        h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.AssistantToolCallsGenerated, LsCall));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the live event");

        await Assert.That(h.Feed.ReadAppended().Lines.Select(l => l.Offset)).IsEquivalentTo(new long[] { 2 });
    }

    [Test]
    public async Task An_event_the_chat_does_not_render_moves_the_position_without_a_row() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        h.Feed.ReadAppended();
        h.Lane.PushStreamEvent(Envelope("s1", 2, "InterruptIssued", """{"request_id":"r1"}"""));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the position");
        await Assert.That(h.Feed.ReadAppended().Lines).IsEmpty();
    }

    [Test]
    public async Task A_session_the_server_hides_reads_as_missing() {
        using var h = new Harness { NextDetail = new(null, NotFound: true) };
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Feed.PendingRunForTesting is { IsCompleted: true }, what: "the run");
        await Assert.That(h.Feed.ReadAppended().Status).IsEqualTo(FeedStatus.Missing);
        await Assert.That(h.Lane.Tails).IsEmpty();
    }

    [Test]
    public async Task A_denied_stream_reads_as_failed_and_is_not_retried() {
        using var h = new Harness();
        h.Lane.TailHandler = (_, _) => new HubException(WireTokens.StreamNotAuthorized);
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Feed.PendingRunForTesting is { IsCompleted: true }, what: "the run");
        h.Feed.ReadAppended(); // drains the seed the run committed before the tail was refused
        var read = h.Feed.ReadAppended();
        await Assert.That(read.Status).IsEqualTo(FeedStatus.Failed);
        await Assert.That(read.Failure).Contains("not authorized");
        await Assert.That(h.Feed.WaitingToRetryForTesting).IsFalse();
    }

    [Test]
    public async Task A_refused_stream_keeps_the_seed_rows_then_reports_the_failure() {
        using var h = new Harness();
        h.Lane.TailHandler = (_, _) => new HubException(WireTokens.StreamNotAuthorized);
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Feed.PendingRunForTesting is { IsCompleted: true }, what: "the run");

        var seed = h.Feed.ReadAppended();
        await Assert.That(seed.Status).IsEqualTo(FeedStatus.Reset);
        await Assert.That(seed.Lines.Count).IsEqualTo(2);
        await Assert.That(seed.SnapshotOffset).IsEqualTo(2);

        var failed = h.Feed.ReadAppended();
        await Assert.That(failed.Status).IsEqualTo(FeedStatus.Failed);
        await Assert.That(failed.Lines).IsEmpty();
        await Assert.That(failed.Failure).Contains("not authorized");
    }

    /// An unauthorized seed is a refusal like any other: without it the pane shows an empty
    /// transcript and nothing says the sign-in lapsed.
    [Test]
    public async Task An_unauthorized_seed_reads_as_failed_and_tails_nothing() {
        using var h = new Harness { NextDetail = new(null, Unauthorized: true) };
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Feed.PendingRunForTesting is { IsCompleted: true }, what: "the run");

        var read = h.Feed.ReadAppended();
        await Assert.That(read.Status).IsEqualTo(FeedStatus.Failed);
        await Assert.That(read.Failure).Contains("not signed in");
        await Assert.That(h.Lane.Tails).IsEmpty();
    }

    /// A hub that answers the subscribe with anything else — a method it does not have, say — is
    /// refusing too, and retrying it forever only hides that.
    [Test]
    public async Task A_stream_the_hub_refuses_for_any_other_reason_is_not_retried_either() {
        using var h = new Harness();
        h.Lane.TailHandler = (_, _) => new HubException("Method does not exist");
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Feed.PendingRunForTesting is { IsCompleted: true }, what: "the run");
        h.Feed.ReadAppended(); // drains the seed the run committed before the tail was refused

        var read = h.Feed.ReadAppended();
        await Assert.That(read.Status).IsEqualTo(FeedStatus.Failed);
        await Assert.That(read.Failure).Contains("Method does not exist");
        await Assert.That(h.Feed.WaitingToRetryForTesting).IsFalse();
        await Assert.That(h.Lane.Tails.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_re_established_access_resumes_the_tail_from_its_position_without_a_second_seed() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the first tail");
        var seedReads = h.Reads;
        h.Feed.ReadAppended();
        h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.AssistantTextGenerated, """{"content":"more"}"""));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the live event");

        h.Access.OnNext(SessionAccessState.Unavailable);
        h.Access.OnNext(SessionAccessState.Establishing);
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 2, what: "the second tail");
        await Assert.That(h.Lane.Tails[1].From).IsEqualTo((ulong?)2);
        await Assert.That(h.Reads).IsEqualTo(seedReads);
        await Assert.That(h.Feed.ReadAppended().Status).IsEqualTo(FeedStatus.Ok);
    }

    /// A failure is logged once per outage: a repeat stays quiet until the stream delivers again.
    [Test]
    public async Task A_repeated_failure_is_logged_once_per_outage() {
        using var h = new Harness();
        h.Lane.TailHandler = (_, _) => new InvalidOperationException("dropped");
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Feed.WaitingToRetryForTesting, what: "the first retry");
        h.Time.Advance(RemoteTranscriptFeed.Retry[0]);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 2 && h.Feed.WaitingToRetryForTesting, what: "the second retry");
        await Assert.That(h.Logged.Count).IsEqualTo(1);

        h.Lane.TailHandler = (_, _) => null;
        h.Time.Advance(RemoteTranscriptFeed.Retry[1]);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 3, what: "the stream back");
        h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.AssistantTextGenerated, """{"content":"more"}"""));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the live event");

        h.Lane.TailHandler = (_, _) => new InvalidOperationException("dropped");
        h.Lane.CloseTail(h.Stream);
        await WaitUntilAsync(() => h.Feed.WaitingToRetryForTesting, what: "the retry after the stream delivered");
        h.Time.Advance(RemoteTranscriptFeed.Retry[0]);
        await WaitUntilAsync(() => h.Logged.Count == 2, what: "the reason logged again");
    }

    [Test]
    public async Task A_tail_that_ends_while_access_stands_resumes_after_the_retry_gap() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the first tail");
        h.Lane.CloseTail(h.Stream);
        await WaitUntilAsync(() => h.Feed.WaitingToRetryForTesting, what: "the retry armed");
        h.Time.Advance(RemoteTranscriptFeed.Retry[0]);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 2, what: "the second tail");
        await Assert.That(h.Lane.Tails[1].From).IsEqualTo((ulong?)1);

        h.Lane.CloseTail(h.Stream);
        await WaitUntilAsync(() => h.Feed.WaitingToRetryForTesting, what: "the retry armed again");
        h.Time.Advance(RemoteTranscriptFeed.Retry[0]);
        await Assert.That(h.Lane.Tails.Count).IsEqualTo(2);
        h.Time.Advance(RemoteTranscriptFeed.Retry[1] - RemoteTranscriptFeed.Retry[0]);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 3, what: "the third tail");
    }

    [Test]
    public async Task A_lost_access_stops_the_tail_and_a_disposed_feed_tails_nothing_more() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        h.Access.OnNext(SessionAccessState.Denied);
        await WaitUntilAsync(() => h.Feed.PendingRunForTesting is { IsCompleted: true }, what: "the run stopped");
        h.Feed.Dispose();
        h.Access.OnNext(SessionAccessState.Established);
        await Task.Delay(50);
        await Assert.That(h.Lane.Tails.Count).IsEqualTo(1);
    }

    /// A refusal that lands after the run it belongs to was stopped is that run's verdict, not
    /// the current one's.
    [Test]
    public async Task A_refusal_from_a_superseded_run_is_not_reported() {
        using var h = new Harness();
        var gate = new TaskCompletionSource();
        h.Lane.TailHandler = (_, _) => {
            gate.Task.Wait(TimeSpan.FromSeconds(5));
            return new HubException(WireTokens.StreamNotAuthorized);
        };
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");

        h.Access.OnNext(SessionAccessState.Unavailable);
        gate.SetResult();
        await WaitUntilAsync(() => h.Feed.PendingRunForTesting is { IsCompleted: true }, what: "the run");

        await Assert.That(h.Feed.ReadAppended().Status).IsEqualTo(FeedStatus.Reset);
        await Assert.That(h.Feed.ReadAppended().Status).IsEqualTo(FeedStatus.Ok);
    }

    /// The pane polls, so a verdict answered once would be gone by the next poll; it stands until
    /// a new attempt seeds, and a verdict the previous attempt left behind never outlives that.
    [Test]
    public async Task A_verdict_answers_every_read_until_a_new_attempt_seeds() {
        using var h = new Harness { NextDetail = new(null, Unauthorized: true) };
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Feed.PendingRunForTesting is { IsCompleted: true }, what: "the run");
        await Assert.That(h.Feed.ReadAppended().Status).IsEqualTo(FeedStatus.Failed);
        await Assert.That(h.Feed.ReadAppended().Status).IsEqualTo(FeedStatus.Failed);

        h.NextDetail = new(Detail(Event(0, CanonicalEventTypes.UserMessageReceived, Hello)));
        h.Access.OnNext(SessionAccessState.Establishing);
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");

        var seed = h.Feed.ReadAppended();
        await Assert.That(seed.Status).IsEqualTo(FeedStatus.Reset);
        await Assert.That(seed.Lines.Count).IsEqualTo(1);
        await Assert.That(h.Feed.ReadAppended().Status).IsEqualTo(FeedStatus.Ok);
    }

    /// A notification marked meta yields no row and no input, only its finish; the feed must keep it.
    [Test]
    public async Task A_signal_only_projection_survives_the_feed() {
        using var h = new Harness(vendor: "claude");
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        h.Feed.ReadAppended();
        h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.UserMessageReceived,
            """{"content":"<task-notification>\n<task-id>a9f262478e032f427</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<status>completed</status>\n<summary>done</summary>\n</task-notification>","extensions":{"claude_code":{"is_meta":true}}}"""));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the position");

        var line = h.Feed.ReadAppended().Lines.Single();
        await Assert.That(line.Offset).IsEqualTo(2);
        await Assert.That(line.Projection.Envelopes).IsEmpty();
        await Assert.That(line.Projection.SubmittedInputs).IsEmpty();
        var finished = (RunSignal.Finished)line.Projection.Runs.Single();
        await Assert.That(finished.CallId).IsEqualTo("toolu_A");
        await Assert.That(finished.AgentId).IsEqualTo("a9f262478e032f427");
        await Assert.That(finished.Outcome).IsEqualTo(RunOutcome.Done);
    }

    /// A filtered user message with no ordinary display row still carries its literal candidate so
    /// the chat can restore it when a matching send is queued.
    [Test]
    public async Task A_literal_only_projection_survives_the_feed() {
        using var h = new Harness(vendor: "codex");
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        h.Feed.ReadAppended();
        h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.UserMessageReceived,
            """{"content":"# AGENTS.md instructions\nLiteral example"}"""));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the position");

        var line = h.Feed.ReadAppended().Lines.Single();
        await Assert.That(line.Offset).IsEqualTo(2);
        await Assert.That(line.Projection.Envelopes).IsEmpty();
        await Assert.That(line.Projection.LiteralInputs).IsEquivalentTo(new[] { "# AGENTS.md instructions\nLiteral example" });
    }

    /// The server's lifecycle event ends the row with no notification on the stream at all.
    [Test]
    public async Task The_servers_subagent_completion_ends_a_seeded_background_row() {
        using var h = new Harness(vendor: "claude") {
            NextDetail = new(Detail(
                Event(0, CanonicalEventTypes.AssistantToolCallsGenerated,
                    """{"tool_calls":[{"call_id":"toolu_A","tool_name":"Agent","arguments":{"subagent_type":"Explore","description":"look"}}],"timestamp":"2026-09-17T10:00:00Z"}"""),
                Event(1, CanonicalEventTypes.ToolResultReceived,
                    """{"call_id":"toolu_A","result":"launched","timestamp":"2026-09-17T10:00:01Z","extensions":{"claude_code":{"tool_use_result":{"status":"async_launched","agentId":"a9f262478e032f427"}}}}"""))),
        };
        var subagents = new SessionRuns(h.Time);
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        foreach (var line in h.Feed.ReadAppended().Lines) subagents.Apply(line.Projection);
        await Assert.That(subagents.RunningCount).IsEqualTo(1);

        h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.SubagentCompleted,
            """{"agent_id":"a9f262478e032f427","timestamp":"2026-09-17T10:02:05Z"}"""));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the completion");
        foreach (var line in h.Feed.ReadAppended().Lines) subagents.Apply(line.Projection);
        await Assert.That(subagents.RunningCount).IsEqualTo(0);
        await Assert.That(subagents.Rows.Single().State).IsEqualTo(RunState.Done);
        await Assert.That(subagents.Rows.Single().EndedAt).IsEqualTo(DateTimeOffset.Parse("2026-09-17T10:02:05Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    /// A resumed session stays open, so the session-start event is all the stream says about a run
    /// the old process took with it, and it is dated by the resume rather than by when it was stored.
    [Test]
    public async Task A_resumed_session_start_stops_a_seeded_background_row() {
        using var h = new Harness(vendor: "claude") {
            NextDetail = new(Detail(
                Event(0, CanonicalEventTypes.AssistantToolCallsGenerated,
                    """{"tool_calls":[{"call_id":"toolu_A","tool_name":"Agent","arguments":{"subagent_type":"Explore","description":"look"}}],"timestamp":"2026-09-17T10:00:00Z"}"""),
                Event(1, CanonicalEventTypes.ToolResultReceived,
                    """{"call_id":"toolu_A","result":"launched","timestamp":"2026-09-17T10:00:01Z","extensions":{"claude_code":{"tool_use_result":{"status":"async_launched","agentId":"a9f262478e032f427"}}}}"""))),
        };
        var subagents = new SessionRuns(h.Time);
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        foreach (var line in h.Feed.ReadAppended().Lines) subagents.Apply(line.Projection);
        await Assert.That(subagents.RunningCount).IsEqualTo(1);

        h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.SessionStarted,
            """{"timestamp":"2026-09-17T10:02:05Z","extensions":{"claude_code":{"source":"Resume"}}}"""));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the resume");
        foreach (var line in h.Feed.ReadAppended().Lines) subagents.Apply(line.Projection);

        await Assert.That(subagents.RunningCount).IsEqualTo(0);
        await Assert.That(subagents.Rows.Single().State).IsEqualTo(RunState.Stopped);
        await Assert.That(subagents.Rows.Single().EndedAt).IsEqualTo(DateTimeOffset.Parse("2026-09-17T10:02:05Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    /// The payload's own time is transcript-authoritative; the stream envelope only carries when
    /// the server happened to store it.
    [Test]
    public async Task A_live_event_dated_by_its_own_payload_keeps_that_time_not_the_storage_time() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        h.Feed.ReadAppended();

        h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.UserMessageReceived,
            """{"content":"hello","timestamp":"2026-09-17T10:01:00Z"}"""));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the live event");

        var envelope = h.Feed.ReadAppended().Lines.Single().Projection.Envelopes.Single();
        var at = DateTimeOffset.Parse(envelope.TimestampIso!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        await Assert.That(at).IsEqualTo(DateTimeOffset.Parse("2026-09-17T10:01:00Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    [Test]
    public async Task A_live_event_with_no_payload_timestamp_keeps_the_envelope_storage_time() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        h.Feed.ReadAppended();

        var storedAt = new DateTime(2026, 9, 17, 9, 0, 0, DateTimeKind.Utc);
        h.Lane.PushStreamEvent(new StreamEventEnvelope {
            EventId = Guid.NewGuid(), Stream = h.Stream, EventType = CanonicalEventTypes.UserMessageReceived,
            StreamPosition = 2, GlobalPosition = 2, Timestamp = storedAt, JsonPayload = """{"content":"hello"}""",
        });
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the live event");

        var envelope = h.Feed.ReadAppended().Lines.Single().Projection.Envelopes.Single();
        var at = DateTimeOffset.Parse(envelope.TimestampIso!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        await Assert.That(at).IsEqualTo(storedAt);
    }

    const string TagLed = """{"content":"<environment_context> is what I mean by context"}""";

    /// A reload reads the content item kinds off the detail read's vendor block; without them the text alone decides.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_codex_line_the_kinds_call_the_persons_shows_in_the_seed(bool kinds) {
        var evt = Event(0, CanonicalEventTypes.UserMessageReceived, TagLed);
        if (kinds) evt = evt with { Vendors = System.Text.Json.JsonDocument.Parse("""{"codex":{"turn_id":"t1","content_item_kinds":["user.text"]}}""").RootElement.Clone() };
        using var h = new Harness(vendor: "codex") { NextDetail = new(Detail(evt)) };
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");

        var rows = h.Feed.ReadAppended().Lines.SelectMany(l => l.Projection.Envelopes).Select(e => e.Text).ToList();
        await Assert.That(rows).IsEquivalentTo(kinds ? new string?[] { "<environment_context> is what I mean by context" } : []);
    }

    /// A live event carries the same kinds in its metadata.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_codex_line_the_kinds_call_the_persons_shows_live(bool kinds) {
        using var h = new Harness(vendor: "codex");
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        h.Feed.ReadAppended();
        h.Lane.PushStreamEvent(new StreamEventEnvelope {
            EventId = Guid.NewGuid(), Stream = h.Stream, EventType = CanonicalEventTypes.UserMessageReceived,
            StreamPosition = 2, GlobalPosition = 2, Timestamp = DateTime.UtcNow, JsonPayload = TagLed,
            JsonMetadata = kinds ? """{"$vendor":"codex","$codexTurnId":"t1","$codexContentItemKinds":"user.text"}""" : """{"$vendor":"codex"}""",
        });
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the live event");

        var rows = h.Feed.ReadAppended().Lines.SelectMany(l => l.Projection.Envelopes).Select(e => e.Text).ToList();
        await Assert.That(rows).IsEquivalentTo(kinds ? new string?[] { "<environment_context> is what I mean by context" } : []);
    }

    static SessionEventDto Text(long number, string text) =>
        Event(number, CanonicalEventTypes.AssistantTextGenerated, $$"""{"content":"{{text}}"}""");

    static RemoteTranscriptFeed Open(FakeSessionEvents server, FakeServerLane lane, BehaviorSubject<SessionAccessState> access, string vendor = "gemini") =>
        new("s1", vendor, access, server.Reader, lane, new FakeTimeProvider(), _ => { });

    static async Task<FeedRead> OlderAsync(RemoteTranscriptFeed feed) {
        await Assert.That(feed.RequestOlder()).IsTrue();
        await feed.PendingOlderForTesting!;
        return feed.ReadAppended();
    }

    /// The seed reads the last window after a one-event probe, and older pages walk back to event zero.
    [Test]
    public async Task Seeding_reads_a_window_and_older_pages_reach_event_zero() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 700).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var feed = Open(server, lane, access);
        await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");
        await Assert.That(lane.Tails[0].From).IsEqualTo((ulong?)699);

        var seed = feed.ReadAppended();
        await Assert.That(seed.Status).IsEqualTo(FeedStatus.Reset);
        await Assert.That(seed.Lines.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(400, 300).Select(i => (long)i));
        await Assert.That(server.Requests).IsEquivalentTo(new[] { (0L, 1), (400L, 1000) });
        await Assert.That(feed.HasOlder).IsTrue();

        var older = await OlderAsync(feed);
        await Assert.That(older.Status).IsEqualTo(FeedStatus.Ok);
        await Assert.That(older.Lines).IsEmpty();
        await Assert.That(older.Older.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(100, 300).Select(i => (long)i));
        await Assert.That(server.Requests[^1]).IsEqualTo((100L, 300));

        var oldest = await OlderAsync(feed);
        await Assert.That(oldest.Older.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(0, 100).Select(i => (long)i));
        await Assert.That(server.Requests[^1]).IsEqualTo((0L, 100));
        await Assert.That(feed.HasOlder).IsFalse();
        await Assert.That(feed.RequestOlder()).IsFalse();
    }

    /// `from` counts from the oldest event the stream still holds, so a stream whose first events aged out
    /// is windowed by its length, not by its last event number.
    [Test]
    public async Task A_stream_whose_oldest_events_aged_out_still_seeds_its_last_window() {
        var server = new FakeSessionEvents([.. Enumerable.Range(1000, 400).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var feed = Open(server, lane, access);
        await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");

        var seed = feed.ReadAppended();
        await Assert.That(seed.Lines.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(1100, 300).Select(i => (long)i));
        await Assert.That(server.Requests[^1]).IsEqualTo((100L, 1000));
        var older = await OlderAsync(feed);
        await Assert.That(older.Older.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(1000, 100).Select(i => (long)i));
    }

    /// A wake-up scheduled before the window makes a loaded line its prompt: the older page that holds the
    /// schedule reads everything loaded again as a seed, and the prompt's row goes.
    [Test]
    public async Task An_older_page_that_schedules_a_loaded_prompt_reads_the_loaded_range_again() {
        var events = new List<SessionEventDto> {
            Event(0, CanonicalEventTypes.AssistantToolCallsGenerated,
                """{"tool_calls":[{"call_id":"t1","tool_name":"ScheduleWakeup","arguments":{"prompt":"check CI"}}]}"""),
        };
        events.AddRange(Enumerable.Range(1, 398).Select(i => Text(i, $"m{i}")));
        events.Add(Event(399, CanonicalEventTypes.UserMessageReceived, """{"content":"check CI"}"""));
        var server = new FakeSessionEvents([.. events]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var feed = Open(server, lane, access, vendor: "claude");
        await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");
        var seed = feed.ReadAppended();
        await Assert.That(seed.Lines[^1].Projection.Envelopes.Select(e => e.Kind)).Contains(AcpEventKind.UserMessage);

        var reread = await OlderAsync(feed);

        await Assert.That(reread.Status).IsEqualTo(FeedStatus.Reset);
        await Assert.That(reread.Lines[0].Offset).IsEqualTo(0);
        await Assert.That(reread.SnapshotOffset).IsEqualTo(400);
        await Assert.That(reread.Lines.Where(l => l.Offset == 399).SelectMany(l => l.Projection.Envelopes)).IsEmpty();
        await Assert.That(feed.HasOlder).IsFalse();
    }

    /// An older page that schedules nothing a loaded line asked about arrives as Older, not as a new seed.
    [Test]
    public async Task An_older_page_whose_schedule_touches_no_loaded_line_is_prepended() {
        var events = new List<SessionEventDto> {
            Event(0, CanonicalEventTypes.AssistantToolCallsGenerated,
                """{"tool_calls":[{"call_id":"t1","tool_name":"ScheduleWakeup","arguments":{"prompt":"check CI"}}]}"""),
        };
        events.AddRange(Enumerable.Range(1, 399).Select(i => Text(i, $"m{i}")));
        var server = new FakeSessionEvents([.. events]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var feed = Open(server, lane, access, vendor: "claude");
        await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");
        feed.ReadAppended();

        var older = await OlderAsync(feed);

        await Assert.That(older.Status).IsEqualTo(FeedStatus.Ok);
        await Assert.That(older.Older[0].Offset).IsEqualTo(0);
    }

    /// Events that aged out of the stream after the seed move the server's count: the older page is read
    /// again from the corrected count, so no loaded event comes back a second time.
    [Test]
    public async Task An_older_page_after_events_aged_out_neither_repeats_nor_skips_events() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 700).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var feed = Open(server, lane, access);
        await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");
        feed.ReadAppended();
        server.Events.RemoveRange(0, 50);

        var older = await OlderAsync(feed);
        await Assert.That(older.Older.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(100, 300).Select(i => (long)i));
        var oldest = await OlderAsync(feed);
        await Assert.That(oldest.Older.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(50, 50).Select(i => (long)i));
        await Assert.That(feed.HasOlder).IsFalse();
    }

    /// A re-read replays only what the pane has already drained; a live line it has not drained follows the
    /// replay, where it can still acknowledge a send.
    [Test]
    public async Task A_reread_keeps_an_undrained_live_line_out_of_its_replayed_history() {
        var events = new List<SessionEventDto> {
            Event(0, CanonicalEventTypes.AssistantToolCallsGenerated,
                """{"tool_calls":[{"call_id":"t1","tool_name":"ScheduleWakeup","arguments":{"prompt":"check CI"}}]}"""),
        };
        events.AddRange(Enumerable.Range(1, 398).Select(i => Text(i, $"m{i}")));
        events.Add(Event(399, CanonicalEventTypes.UserMessageReceived, """{"content":"check CI"}"""));
        var server = new FakeSessionEvents([.. events]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var feed = Open(server, lane, access, vendor: "claude");
        await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");
        feed.ReadAppended();
        server.Events.Add(Event(400, CanonicalEventTypes.UserMessageReceived, """{"content":"sent while reading"}"""));
        lane.PushStreamEvent(Envelope("s1", 400, CanonicalEventTypes.UserMessageReceived, """{"content":"sent while reading"}"""));
        await WaitUntilAsync(() => feed.CurrentOffset == 401, what: "the live line");

        var reread = await OlderAsync(feed);
        await Assert.That(reread.Status).IsEqualTo(FeedStatus.Reset);
        await Assert.That(reread.SnapshotOffset).IsEqualTo(400);
        await Assert.That(reread.Lines[^1].Offset).IsEqualTo(399);
        var live = feed.ReadAppended();
        await Assert.That(live.Lines.Select(l => l.Offset)).IsEquivalentTo(new long[] { 400 });
    }

    /// A whole-session seed longer than one page projects every page it reads.
    [Test]
    public async Task A_whole_session_seed_projects_every_page() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 2500).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var feed = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, window: null);
        await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");

        await Assert.That(feed.ReadAppended().Lines.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(0, 2500).Select(i => (long)i));
    }

    /// An older page whose events project nothing is passed over for the one behind it in the same read.
    [Test]
    public async Task An_older_page_that_projects_nothing_is_passed_over() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 700).Select(i => i is >= 100 and < 400
            ? Event(i, "x-unknown-event", "{}") : Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var feed = Open(server, lane, access);
        await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");
        feed.ReadAppended();

        await Assert.That(feed.RequestOlder()).IsTrue();
        await feed.PendingOlderForTesting!;
        await WaitUntilAsync(() => feed.HasPendingChanges, what: "the page behind the empty one");

        await Assert.That(feed.ReadAppended().Older.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(0, 100).Select(i => (long)i));
    }

    /// A live line the pane drained while a re-read was in flight stays in the re-read's history.
    [Test]
    public async Task A_live_line_drained_during_a_reread_stays_in_its_history() {
        var events = new List<SessionEventDto> {
            Event(0, CanonicalEventTypes.AssistantToolCallsGenerated,
                """{"tool_calls":[{"call_id":"t1","tool_name":"ScheduleWakeup","arguments":{"prompt":"check CI"}}]}"""),
        };
        events.AddRange(Enumerable.Range(1, 398).Select(i => Text(i, $"m{i}")));
        events.Add(Event(399, CanonicalEventTypes.UserMessageReceived, """{"content":"check CI"}"""));
        var server = new FakeSessionEvents([.. events]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var feed = Open(server, lane, access, vendor: "claude");
        await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");
        feed.ReadAppended();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        server.Gate = () => {
            if (Interlocked.Increment(ref calls) < 2) return Task.CompletedTask;
            entered.TrySetResult();
            return held.Task;
        };
        try {
            await Assert.That(feed.RequestOlder()).IsTrue();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lane.PushStreamEvent(Envelope("s1", 400, CanonicalEventTypes.AssistantTextGenerated, """{"content":"live"}"""));
            await WaitUntilAsync(() => feed.CurrentOffset == 401, what: "the live line");
            await Assert.That(feed.ReadAppended().Lines.Single().Offset).IsEqualTo(400);
        } finally {
            held.TrySetResult();
        }
        await feed.PendingOlderForTesting!;

        var reread = feed.ReadAppended();
        await Assert.That(reread.Status).IsEqualTo(FeedStatus.Reset);
        await Assert.That(reread.Lines[^1].Offset).IsEqualTo(400);
        await Assert.That(reread.SnapshotOffset).IsEqualTo(401);
    }

    static List<SessionEventDto> WakeUpBeforeWindow() {
        var events = new List<SessionEventDto> {
            Event(0, CanonicalEventTypes.AssistantToolCallsGenerated,
                """{"tool_calls":[{"call_id":"t1","tool_name":"ScheduleWakeup","arguments":{"prompt":"check CI"}}]}"""),
        };
        events.AddRange(Enumerable.Range(1, 398).Select(i => Text(i, $"m{i}")));
        events.Add(Event(399, CanonicalEventTypes.UserMessageReceived, """{"content":"check CI"}"""));
        return events;
    }

    /// A wake-up a live event scheduled while a re-read was out still hides the prompt it schedules.
    [Test]
    public async Task A_wake_up_scheduled_live_during_a_reread_still_hides_its_prompt() {
        var server = new FakeSessionEvents([.. WakeUpBeforeWindow()]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var feed = Open(server, lane, access, vendor: "claude");
        await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");
        feed.ReadAppended();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        server.Gate = () => {
            if (Interlocked.Increment(ref calls) < 2) return Task.CompletedTask;
            entered.TrySetResult();
            return held.Task;
        };
        try {
            await Assert.That(feed.RequestOlder()).IsTrue();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lane.PushStreamEvent(Envelope("s1", 400, CanonicalEventTypes.AssistantToolCallsGenerated,
                """{"tool_calls":[{"call_id":"t2","tool_name":"ScheduleWakeup","arguments":{"prompt":"recheck"}}]}"""));
            await WaitUntilAsync(() => feed.CurrentOffset == 401, what: "the live schedule");
        } finally {
            held.TrySetResult();
        }
        await feed.PendingOlderForTesting!;
        feed.ReadAppended();
        feed.ReadAppended();

        lane.PushStreamEvent(Envelope("s1", 401, CanonicalEventTypes.UserMessageReceived, """{"content":"recheck"}"""));
        await WaitUntilAsync(() => feed.CurrentOffset == 402, what: "the prompt");
        await Assert.That(feed.ReadAppended().Lines.Single().Projection.Envelopes).IsEmpty();
    }

    /// Events that age out between two pages of a forward read move the count; the next page is asked for
    /// again so none of the events after the first page is skipped.
    [Test]
    public async Task A_forward_read_across_events_aging_out_skips_nothing() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 2500).Select(i => Text(i, $"m{i}"))]);
        var calls = 0;
        server.Gate = () => {
            if (Interlocked.Increment(ref calls) == 2) server.Events.RemoveRange(0, 50);
            return Task.CompletedTask;
        };
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var feed = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, window: null);
        await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");

        await Assert.That(feed.ReadAppended().Lines.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(0, 2500).Select(i => (long)i));
    }

    /// Events aging out again while a corrected page is asked for are corrected too: nothing after the first page is skipped.
    [Test]
    public async Task A_forward_read_across_two_agings_of_one_page_skips_nothing() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 2500).Select(i => Text(i, $"m{i}"))]);
        var calls = 0;
        server.Gate = () => {
            if (Interlocked.Increment(ref calls) is 2 or 3) server.Events.RemoveRange(0, 5);
            return Task.CompletedTask;
        };
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var feed = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, window: null);
        await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");

        await Assert.That(feed.ReadAppended().Lines.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(0, 2500).Select(i => (long)i));
    }

    /// Enough events aging out between two pages leaves the next page's count past the end. The read places the
    /// count again from the oldest event held and reads every event still held after the first page.
    [Test]
    public async Task A_forward_read_whose_count_ages_past_the_end_reads_what_is_left() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 1500).Select(i => Text(i, $"m{i}"))]);
        var calls = 0;
        server.Gate = () => {
            if (Interlocked.Increment(ref calls) == 2) server.Events.RemoveRange(0, 1001);
            return Task.CompletedTask;
        };
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var feed = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, window: null);
        await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");

        await Assert.That(feed.ReadAppended().Lines.Select(l => l.Offset))
            .IsEquivalentTo(Enumerable.Range(0, 1000).Concat(Enumerable.Range(1001, 499)).Select(i => (long)i));
        await Assert.That(lane.Tails[0].From).IsEqualTo((ulong?)1499);
    }

    /// The pane's request for the next page, made while it handles the one that just landed, is taken.
    [Test]
    public async Task A_request_made_while_handling_a_page_is_taken() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 1000).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var feed = Open(server, lane, access);
        await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");
        feed.ReadAppended();
        var taken = new List<bool>();
        feed.Changed += () => { lock (taken) if (taken.Count == 0) taken.Add(feed.RequestOlder()); };

        await Assert.That(feed.RequestOlder()).IsTrue();
        await WaitUntilAsync(() => { lock (taken) return taken.Count == 1; }, what: "the handler's request");

        await Assert.That(taken[0]).IsTrue();
    }

    /// A refused older page stops the paging, so scrolling does not ask again, until access is granted anew.
    [Test]
    public async Task A_refused_older_page_stops_paging_until_access_is_granted_again() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 700).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var feed = Open(server, lane, access);
        await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");
        feed.ReadAppended();
        server.NotFound = true;
        await Assert.That(feed.RequestOlder()).IsTrue();
        await feed.PendingOlderForTesting!;
        var asked = server.RequestCount;

        await Assert.That(feed.HasOlder).IsFalse();
        await Assert.That(feed.RequestOlder()).IsFalse();
        await Assert.That(server.RequestCount).IsEqualTo(asked);

        server.NotFound = false;
        access.OnNext(SessionAccessState.Denied);
        access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => lane.Tails.Count == 2, what: "the tail after the new grant");
        await Assert.That(feed.HasOlder).IsTrue();
        await Assert.That(feed.RequestOlder()).IsTrue();
        await feed.PendingOlderForTesting!;
        await Assert.That(feed.ReadAppended().Older.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(100, 300).Select(i => (long)i));
    }

    /// A refusal answering an older page asked for under an earlier grant of access leaves paging on under the next one.
    [Test]
    public async Task A_refusal_from_an_earlier_grant_leaves_paging_on() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 700).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var feed = Open(server, lane, access);
        await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");
        feed.ReadAppended();
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Gate = () => answer.Task;
        await Assert.That(feed.RequestOlder()).IsTrue();
        await WaitUntilAsync(() => server.RequestCount == 3, what: "the older request");

        access.OnNext(SessionAccessState.Denied);
        access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => lane.Tails.Count == 2, what: "the tail under the new grant");
        server.NotFound = true;
        answer.SetResult();
        await feed.PendingOlderForTesting!;

        server.NotFound = false;
        server.Gate = null;
        await Assert.That(feed.HasOlder).IsTrue();
        await Assert.That(feed.RequestOlder()).IsTrue();
        await feed.PendingOlderForTesting!;
        await Assert.That(feed.ReadAppended().Older.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(100, 300).Select(i => (long)i));
    }

    /// An older page that fails as an unreachable server would is read again after the retry gap.
    [Test]
    public async Task A_failed_older_page_is_read_again() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 700).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        var time = new FakeTimeProvider();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var feed = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, time, _ => { });
        await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");
        feed.ReadAppended();
        server.FailNext = 1;

        await Assert.That(feed.RequestOlder()).IsTrue();
        // The retry gap is a timer on the fake clock, which may not be registered yet when the failure lands.
        await WaitUntilAsync(() => {
            time.Advance(RemoteTranscriptFeed.Retry[0]);
            return feed.PendingOlderForTesting!.IsCompleted;
        }, what: "the retried page");

        await Assert.That(feed.ReadAppended().Older.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(100, 300).Select(i => (long)i));
    }

    static RemoteTranscriptCache Cache() =>
        new(new BehaviorSubject<string?>("u1"), new Subject<(string, SessionAccessState)>());

    /// A re-opened session shows its kept lines with the events after them, read from the last kept one on.
    [Test]
    public async Task A_reopened_session_shows_its_kept_lines_and_reads_only_what_followed() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 700).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var cache = Cache();
        using (var first = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, cache: cache)) {
            await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the first tail");
            first.ReadAppended();
            lane.PushStreamEvent(Envelope("s1", 700, CanonicalEventTypes.AssistantTextGenerated, """{"content":"live"}"""));
            await WaitUntilAsync(() => first.CurrentOffset == 701, what: "the live event");
        }
        await Assert.That(cache.Contains("s1")).IsTrue();
        server.Events.Add(Text(700, "live"));
        server.Events.AddRange(Enumerable.Range(701, 5).Select(i => Text(i, $"m{i}")));
        server.Requests.Clear();

        using var second = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, cache: cache);
        await WaitUntilAsync(() => lane.Tails.Count == 2, what: "the second tail");

        var seed = second.ReadAppended();
        await Assert.That(seed.Status).IsEqualTo(FeedStatus.Reset);
        await Assert.That(seed.Lines.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(400, 306).Select(i => (long)i));
        await Assert.That(seed.SnapshotOffset).IsEqualTo(706);
        await Assert.That(second.ReadAppended().Lines).IsEmpty();
        await Assert.That(server.Requests).IsEquivalentTo(new[] { (700L, 1000) });
        await Assert.That(lane.Tails[1].From).IsEqualTo((ulong?)705);
        await Assert.That(second.HasOlder).IsTrue();
    }

    /// A feed that reads its whole session is kept too, and counts from the oldest event the stream still holds:
    /// the stream starts past 0, so a re-open counting from event 0 would ask for the wrong events.
    [Test]
    public async Task A_whole_session_read_is_kept_and_reopens_from_its_last_event() {
        var server = new FakeSessionEvents([.. Enumerable.Range(100, 50).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var cache = Cache();
        using (var first = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, window: null, cache: cache)) {
            await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the first tail");
            first.ReadAppended();
        }
        await Assert.That(cache.Contains("s1")).IsTrue();
        server.Events.AddRange(Enumerable.Range(150, 2).Select(i => Text(i, $"m{i}")));
        server.Requests.Clear();

        using var second = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, window: null, cache: cache);
        await WaitUntilAsync(() => lane.Tails.Count == 2, what: "the second tail");

        var seed = second.ReadAppended();
        await Assert.That(seed.Lines.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(100, 52).Select(i => (long)i));
        await Assert.That(server.Requests).IsEquivalentTo(new[] { (49L, 1000) });
    }

    /// The cache is keyed by session alone, so a whole-session feed can find the lines a windowed feed kept. Those
    /// start part way into the stream, so it reads the whole session instead, and leaves them for the windowed feed.
    [Test]
    public async Task A_whole_session_feed_does_not_resume_from_a_windowed_feeds_lines() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 700).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var cache = Cache();
        using (var windowed = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, cache: cache, prefetch: 0)) {
            await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the windowed tail");
            windowed.ReadAppended();
        }
        await Assert.That(cache.Contains("s1")).IsTrue();

        using var whole = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, window: null, cache: cache);
        await WaitUntilAsync(() => lane.Tails.Count == 2, what: "the whole-session tail");

        await Assert.That(whole.ReadAppended().Lines.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(0, 700).Select(i => (long)i));
        await Assert.That(cache.Contains("s1")).IsTrue();
    }

    /// The cache holds only the newest lines, so a whole-session feed longer than that is not kept: reopening it
    /// from the kept lines would show part of the session as the whole of it.
    [Test]
    public async Task A_whole_session_longer_than_the_cache_holds_is_read_again_in_full() {
        var count = RemoteTranscriptCache.MaxLines + 50;
        var server = new FakeSessionEvents([.. Enumerable.Range(0, count).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var cache = Cache();
        using (var first = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, window: null, cache: cache)) {
            await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the first tail");
            first.ReadAppended();
        }
        await Assert.That(cache.Contains("s1")).IsFalse();

        using var second = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, window: null, cache: cache);
        await WaitUntilAsync(() => lane.Tails.Count == 2, what: "the second tail");

        await Assert.That(second.ReadAppended().Lines.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(0, count).Select(i => (long)i));
    }

    /// A session the server no longer shows this reader is not kept, and its kept lines go.
    [Test]
    public async Task A_refused_reopen_drops_the_kept_lines() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 10).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var cache = Cache();
        using (new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, cache: cache)) {
            await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");
        }
        server.NotFound = true;

        using (var second = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, cache: cache)) {
            await WaitUntilAsync(() => second.PendingRunForTesting is { IsCompleted: true }, what: "the run");
            second.ReadAppended();
            await Assert.That(second.ReadAppended().Status).IsEqualTo(FeedStatus.Missing);
        }

        await Assert.That(cache.Contains("s1")).IsFalse();
    }

    /// Access refused while the transcript is open keeps nothing when it closes.
    [Test]
    public async Task A_transcript_whose_access_is_refused_while_open_is_not_kept() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 10).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var cache = Cache();
        using (new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, cache: cache)) {
            await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");
            access.OnNext(SessionAccessState.Denied);
        }

        await Assert.That(cache.Contains("s1")).IsFalse();
    }

    static async Task<RemoteTranscriptCache> CachedAsync(FakeSessionEvents server, FakeServerLane lane, BehaviorSubject<SessionAccessState> access) {
        var cache = Cache();
        using var first = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, cache: cache);
        await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the first tail");
        first.ReadAppended();
        return cache;
    }

    /// A transcript open across a change of account is not kept under the next one.
    [Test]
    public async Task A_transcript_open_across_a_change_of_account_is_not_kept() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 10).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var subject = new BehaviorSubject<string?>("u1");
        using var cache = new RemoteTranscriptCache(subject, new Subject<(string, SessionAccessState)>());
        using (new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, cache: cache)) {
            await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");
            subject.OnNext("u2");
        }

        await Assert.That(cache.Contains("s1")).IsFalse();
    }

    /// Kept lines wait for the server's answer: a refused read shows none of them.
    [Test]
    public async Task A_refused_reopen_shows_none_of_the_kept_lines() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 10).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var cache = await CachedAsync(server, lane, access);
        server.Unauthorized = true;

        using var second = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, cache: cache);
        await WaitUntilAsync(() => second.PendingRunForTesting is { IsCompleted: true }, what: "the run");

        var read = second.ReadAppended();
        await Assert.That(read.Status).IsEqualTo(FeedStatus.Failed);
        await Assert.That(read.Lines).IsEmpty();
    }

    /// Events recorded while the transcript was closed are part of the replayed history, so a send made
    /// after re-opening cannot be acknowledged by one of them.
    [Test]
    public async Task Events_recorded_while_closed_are_inside_the_reopened_seed() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 10).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var cache = await CachedAsync(server, lane, access);
        server.Events.AddRange(Enumerable.Range(10, 3).Select(i => Text(i, $"m{i}")));

        using var second = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, cache: cache);
        await WaitUntilAsync(() => lane.Tails.Count == 2, what: "the second tail");

        var seed = second.ReadAppended();
        await Assert.That(seed.Lines.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(0, 13).Select(i => (long)i));
        await Assert.That(seed.SnapshotOffset).IsEqualTo(13);
        await Assert.That(second.ReadAppended().Lines).IsEmpty();
    }

    /// Events that aged out while the transcript was closed move the server's count: the kept lines are
    /// dropped for a fresh seed rather than skipping what was recorded meanwhile.
    [Test]
    public async Task A_reopen_after_events_aged_out_reads_a_fresh_seed() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 10).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var cache = await CachedAsync(server, lane, access);
        server.Events.RemoveRange(0, 3);
        server.Events.AddRange(Enumerable.Range(10, 3).Select(i => Text(i, $"m{i}")));

        using var second = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, cache: cache);
        await WaitUntilAsync(() => lane.Tails.Count == 2, what: "the second tail");

        var seed = second.ReadAppended();
        await Assert.That(seed.Lines.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(3, 10).Select(i => (long)i));
    }

    /// A session opened before its first event learns where its stream starts from the live tail, so it is
    /// kept when it closes.
    [Test]
    public async Task A_session_whose_first_events_came_live_is_kept() {
        var server = new FakeSessionEvents();
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var cache = Cache();
        using (var feed = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, cache: cache)) {
            await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");
            lane.PushStreamEvent(Envelope("s1", 0, CanonicalEventTypes.AssistantTextGenerated, """{"content":"first"}"""));
            await WaitUntilAsync(() => feed.CurrentOffset == 1, what: "the live event");
        }

        await Assert.That(cache.Contains("s1")).IsTrue();
    }

    /// A re-open after more than a page of new events reads every page of them.
    [Test]
    public async Task A_reopen_reads_every_page_recorded_while_closed() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 10).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var cache = await CachedAsync(server, lane, access);
        server.Events.AddRange(Enumerable.Range(10, 2500).Select(i => Text(i, $"m{i}")));

        using var second = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, cache: cache);
        await WaitUntilAsync(() => lane.Tails.Count == 2, what: "the second tail");

        await Assert.That(second.ReadAppended().Lines.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(0, 2510).Select(i => (long)i));
    }

    /// Events aging out between a reopen's first page and the next move the count; the next page is asked for
    /// again, so none of the events recorded while closed is skipped.
    [Test]
    public async Task A_reopen_across_events_aging_out_skips_nothing() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 10).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var cache = await CachedAsync(server, lane, access);
        server.Events.AddRange(Enumerable.Range(10, 2500).Select(i => Text(i, $"m{i}")));
        var calls = 0;
        server.Gate = () => {
            if (Interlocked.Increment(ref calls) == 2) server.Events.RemoveRange(0, 5);
            return Task.CompletedTask;
        };

        using var second = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, cache: cache);
        await WaitUntilAsync(() => lane.Tails.Count == 2, what: "the second tail");

        await Assert.That(second.ReadAppended().Lines.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(0, 2510).Select(i => (long)i));
    }

    /// A reopen whose catch-up read fails as an unreachable server would resumes from the kept lines on the
    /// retry, rather than reading a fresh seed.
    [Test]
    public async Task A_failed_catch_up_read_resumes_from_the_kept_lines_on_the_retry() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 700).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        var time = new FakeTimeProvider();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var cache = await CachedAsync(server, lane, access);
        server.Events.AddRange(Enumerable.Range(700, 5).Select(i => Text(i, $"m{i}")));
        server.Requests.Clear();
        server.FailNext = 1;

        using var second = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, time, _ => { }, cache: cache);
        // The retry gap is a timer on the fake clock, which may not be registered yet when the failure lands.
        await WaitUntilAsync(() => {
            time.Advance(RemoteTranscriptFeed.Retry[0]);
            return lane.Tails.Count == 2;
        }, what: "the retried reopen");

        await Assert.That(second.ReadAppended().Lines.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(400, 305).Select(i => (long)i));
        await Assert.That(server.Requests).IsEquivalentTo(new[] { (699L, 1000), (699L, 1000) });
    }

    /// Events that aged out with nothing new after them still move the count: the kept lines go.
    [Test]
    public async Task A_reopen_after_events_aged_out_with_nothing_new_reads_a_fresh_seed() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 10).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var cache = await CachedAsync(server, lane, access);
        server.Events.RemoveRange(0, 3);

        using var second = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, cache: cache);
        await WaitUntilAsync(() => lane.Tails.Count == 2, what: "the second tail");

        await Assert.That(second.ReadAppended().Lines.Select(l => l.Offset)).IsEquivalentTo(Enumerable.Range(3, 7).Select(i => (long)i));
    }

    /// A change of account while the re-open read is out drops the kept lines for a fresh seed.
    [Test]
    public async Task A_change_of_account_during_a_reopen_reads_a_fresh_seed() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 10).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var subject = new BehaviorSubject<string?>("u1");
        using var cache = new RemoteTranscriptCache(subject, new Subject<(string, SessionAccessState)>());
        using (var first = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, cache: cache)) {
            await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the first tail");
        }
        server.Requests.Clear();
        var calls = 0;
        server.Gate = () => {
            if (Interlocked.Increment(ref calls) == 1) subject.OnNext("u2");
            return Task.CompletedTask;
        };

        using var second = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, cache: cache);
        await WaitUntilAsync(() => lane.Tails.Count == 2, what: "the second tail");

        await Assert.That(server.Requests).Contains((0L, 1));
    }

    /// An older page the server refuses refuses the session, so its lines are not kept.
    [Test]
    public async Task A_refused_older_page_keeps_nothing() {
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 700).Select(i => Text(i, $"m{i}"))]);
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var cache = Cache();
        using (var feed = new RemoteTranscriptFeed("s1", "gemini", access, server.Reader, lane, new FakeTimeProvider(), _ => { }, cache: cache)) {
            await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the tail");
            feed.ReadAppended();
            server.NotFound = true;
            await Assert.That(feed.RequestOlder()).IsTrue();
            await feed.PendingOlderForTesting!;
        }

        await Assert.That(cache.Contains("s1")).IsFalse();
    }
}
