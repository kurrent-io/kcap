using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class ChatTabViewModelTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_remote_feed_switch_drains_after_the_previous_read_without_a_poll() => RunOnUiAsync(async () => {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var session = new BehaviorSubject<ChatSessionInfo>(Session("old"));
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        var time = new FakeTimeProvider();
        var lane = new FakeServerLane();
        var remote = new RemoteTranscriptFeed("new", "gemini", access,
            FakeSessionEvents.Paged(_ => Task.FromResult(new SessionDetailFetch(new SessionDetailDto { LastEventNumber = 0 }))), lane, time, _ => { });
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
            new AcceptingChatInput(), new NoAttachmentUploader(), key => key == "old" ? new HeldFeed(entered, release) : remote,
            new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time));
        try {
            await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)));
            await Assert.That(entered.IsSet).IsTrue();
            var oldRead = chat.PendingReadForTesting!;
            session.OnNext(Session("new"));
            await WaitUntilAsync(() => lane.Tails.Count == 1, what: "new remote feed subscribed");
            lane.PushStreamEvent(RemoteFixtures.Envelope("new", 1, CanonicalEventTypes.AssistantTextGenerated, "{\"content\":\"live\"}"));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            release.Set();
            await oldRead;
            await WaitUntilAsync(() => chat.Items.OfType<AssistantTextItem>().Any(item => item.Text == "live"),
                what: "new rows drained without advancing the poll clock");
        } finally { release.Set(); await chat.TeardownAsync(); }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task The_outcome_shows_in_a_newly_opened_live_view() => RunOnUiAsync(async () => {
        using var session = new BehaviorSubject<ChatSessionInfo>(Session("s2"));
        using var outcomes = new Subject<HandoffOutcomeViewModel?>();
        var time = new FakeTimeProvider();
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
            new AcceptingChatInput(), new NoAttachmentUploader(), _ => new HeldFeed(new ManualResetEventSlim(true), new ManualResetEventSlim(true)),
            new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time), handoffOutcome: outcomes);
        try {
            await Assert.That(chat.HandoffOutcome).IsNull();

            outcomes.OnNext(new HandoffOutcomeViewModel(new("s1", "Old session", "done", null,
                [new("plan", "Plan A", "carried", null)])));
            await WaitUntilAsync(() => chat.HandoffOutcome is not null, what: "the outcome");

            await Assert.That(chat.HandoffOutcome!.Header).IsEqualTo("Continued from Old session");
            await Assert.That(chat.HandoffOutcome.Rows[0].Text).IsEqualTo("Plan A — carried over");
        } finally { await chat.TeardownAsync(); }
    });

    /// Older rows go above the ones shown, and a call they leave open is settled by the result the window
    /// already held, rather than staying a running row forever.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Older_rows_go_above_and_a_call_in_them_takes_its_result_from_the_window() => RunOnUiAsync(async () => {
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        var time = new FakeTimeProvider();
        var lane = new FakeServerLane();
        var events = Enumerable.Range(0, 400).Select(i => i switch {
            99  => RemoteFixtures.Event(i, CanonicalEventTypes.AssistantToolCallsGenerated, RemoteFixtures.LsCall),
            100 => RemoteFixtures.Event(i, CanonicalEventTypes.ToolResultReceived, """{"call_id":"t1","result":"a.txt"}"""),
            _   => RemoteFixtures.Event(i, CanonicalEventTypes.AssistantTextGenerated, $$"""{"content":"m{{i}}"}"""),
        });
        var server = new FakeSessionEvents([.. events]);
        RemoteTranscriptFeed? feed = null;
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, Observable.Return(Session("s1")), Observable.Return<string[]?>(null),
            new AcceptingChatInput(), new NoAttachmentUploader(), key => feed = new RemoteTranscriptFeed(key, "gemini", access, server.Reader, lane, time, _ => { }),
            new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time));
        try {
            await WaitUntilAsync(() => chat.Items.Count > 0, what: "the seed");
            await Assert.That((chat.Items[0] as AssistantTextItem)?.Text).IsEqualTo("m101");

            await Assert.That(chat.RequestOlder()).IsTrue();
            await feed!.PendingOlderForTesting!;
            await WaitUntilAsync(() => chat.Items.Count > 390, what: "the older page");

            await Assert.That((chat.Items[0] as AssistantTextItem)?.Text).IsEqualTo("m0");
            var group = chat.Items.OfType<ToolGroupItem>().Single();
            await Assert.That(chat.Items[99]).IsSameReferenceAs(group);
            await Assert.That((chat.Items[100] as AssistantTextItem)?.Text).IsEqualTo("m101");
            var call = group.Calls.Single();
            await Assert.That(call.Outcome).IsEqualTo(ToolOutcome.Done);
            await Assert.That(call.Result).IsEqualTo("a.txt");
            await Assert.That(chat.RequestOlder()).IsFalse();
        } finally { await chat.TeardownAsync(); }
    });

    sealed class HeldFeed(ManualResetEventSlim entered, ManualResetEventSlim release) : IChatTranscriptFeed {
        public long? CurrentOffset => 0;
        public FeedRead ReadAppended() {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return new(FeedStatus.Ok, []);
        }
        public void Dispose() { }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Remote_arrivals_drain_without_advancing_the_poll_clock_and_idle_ticks_do_not_read() => RunOnUiAsync(async () => {
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Establishing);
        using var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
        var lane = new FakeServerLane { TailHandler = (_, _) => new Microsoft.AspNetCore.SignalR.HubException("not authorized") };
        var time = new FakeTimeProvider();
        var feed = new RemoteTranscriptFeed("s1", "gemini", access,
            FakeSessionEvents.Paged(_ => Task.FromResult(new SessionDetailFetch(new SessionDetailDto { LastEventNumber = 0 }))), lane, time, _ => { });
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
            new AcceptingChatInput(), new NoAttachmentUploader(), _ => feed, new RecordingOpener(), time,
            new FakePermissionService(), new SessionRuns(time));
        try {
            access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => chat.Phase == ChatTabPhase.Failed, what: "refusal after the empty seed");
            lane.TailHandler = (_, _) => null;
            access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => lane.Tails.Count == 2 && chat.Phase == ChatTabPhase.Reading, what: "refusal cleared without a poll");
            var lastRead = chat.PendingReadForTesting;
            time.Advance(TimeSpan.FromSeconds(60));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Assert.That(ReferenceEquals(lastRead, chat.PendingReadForTesting)).IsTrue();
            for (var i = 1; i <= 20; i++) lane.PushStreamEvent(RemoteFixtures.Envelope("s1", (ulong)i,
                CanonicalEventTypes.AssistantTextGenerated, "{\"content\":\"live\"}"));
            await WaitUntilAsync(() => chat.Items.Count == 20, what: "all live arrivals drained without a poll");
            await Assert.That(chat.Items.OfType<AssistantTextItem>().All(item => item.Text == "live")).IsTrue();
        } finally { await chat.TeardownAsync(); }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_send_without_a_baseline_during_an_idle_read_still_gets_rebased() => RunOnUiAsync(async () => {
        using var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
        var feed = new GatedIdleFeed();
        var time = new FakeTimeProvider();
        var chat = new ChatTabViewModel("a1", AgentOrigin.Local, session, Observable.Return<string[]?>(null),
            new AcceptingChatInput(), new NoAttachmentUploader(), _ => feed, new RecordingOpener(), time,
            new FakePermissionService(), new SessionRuns(time));
        try {
            await chat.PendingReadForTesting!;
            feed.BlockNext = true;
            time.Advance(ChatTabViewModel.PollInterval);
            await Task.Run(() => feed.Entered.Wait(TimeSpan.FromSeconds(5)));
            await Assert.That(feed.Entered.IsSet).IsTrue();
            feed.Offset = null;
            chat.ComposerText = "next";
            await chat.SendCommand.Execute();
            await Assert.That(chat.QueuedMessages.Single().HasBaseline).IsFalse();
            feed.Release.Set();
            await chat.PendingReadForTesting!;
            await Assert.That(chat.QueuedMessages.Single().HasBaseline).IsTrue();
        } finally {
            feed.Release.Set();
            await chat.PendingReadForTesting!;
            await chat.TeardownAsync();
        }
    });

    sealed class GatedIdleFeed : IChatTranscriptFeed {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public bool BlockNext;
        public long? Offset = 0;
        public long? CurrentOffset => Offset;
        public FeedRead ReadAppended() {
            if (BlockNext) {
                BlockNext = false;
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
            }
            return new(FeedStatus.Ok, [], SnapshotOffset: 10);
        }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
}
