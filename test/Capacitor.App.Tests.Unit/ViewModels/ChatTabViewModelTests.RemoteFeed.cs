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
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, Observable.Return(Ended("s1")), Observable.Return<string[]?>(null),
            new AcceptingChatInput(), new NoAttachmentUploader(), key => feed = new RemoteTranscriptFeed(key, "gemini", access, server.Reader, lane, time, _ => { }, prefetch: 0),
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

    static ChatSessionInfo Ended(string feedKey) => new("Ended", "Ended", "gemini", null, null, null, true, "", feedKey);

    /// With no prefetch an ended session pages back only when asked, which is what most of these tests drive.
    static ChatTabViewModel RemoteChat(FakeSessionEvents server, Action<RemoteTranscriptFeed> opened, FakeTimeProvider time,
            BehaviorSubject<SessionAccessState> access, PlanActivity? planActivity = null, FakePermissionService? permissions = null, bool live = false,
            string vendor = "gemini", int prefetch = 0) =>
        new("a1", AgentOrigin.Remote, Observable.Return(live ? Session("s1") : Ended("s1")), Observable.Return<string[]?>(null),
            new AcceptingChatInput(), new NoAttachmentUploader(), key => {
                var feed = new RemoteTranscriptFeed(key, vendor, access, server.Reader, new FakeServerLane(), time, _ => { }, prefetch: prefetch);
                opened(feed);
                return feed;
            },
            new RecordingOpener(), time, permissions ?? new FakePermissionService(), new SessionRuns(time), planActivity: planActivity,
            sessionId: Observable.Return<string?>("s1"));

    static SessionEventDto TextEvent(int i) =>
        RemoteFixtures.Event(i, CanonicalEventTypes.AssistantTextGenerated, $$"""{"content":"m{{i}}"}""");

    static SessionEventDto ResultEvent(int i, string callId, string result = "ok") =>
        RemoteFixtures.Event(i, CanonicalEventTypes.ToolResultReceived, $$"""{"call_id":"{{callId}}","result":{{System.Text.Json.JsonSerializer.Serialize(result)}}}""");

    /// A window whose events show no rows still pages back until older events do.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_window_that_shows_no_rows_pages_back_to_rows_that_do() => RunOnUiAsync(async () => {
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        var time = new FakeTimeProvider();
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 700).Select(i => i < 100 ? TextEvent(i) : ResultEvent(i, $"x{i}"))]);
        var chat = RemoteChat(server, _ => { }, time, access);
        try {
            await WaitUntilAsync(() => chat.Items.Count == 100, what: "rows from the oldest page");
            await Assert.That((chat.Items[0] as AssistantTextItem)?.Text).IsEqualTo("m0");
        } finally { await chat.TeardownAsync(); }
    });

    /// A result met on one older page settles its call when a still older page brings the call.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_result_on_one_older_page_settles_a_call_on_the_next() => RunOnUiAsync(async () => {
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        var time = new FakeTimeProvider();
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 700).Select(i => i switch {
            50  => RemoteFixtures.Event(i, CanonicalEventTypes.AssistantToolCallsGenerated, RemoteFixtures.LsCall),
            150 => ResultEvent(i, "t1", "a.txt"),
            _   => TextEvent(i),
        })]);
        RemoteTranscriptFeed? feed = null;
        var chat = RemoteChat(server, f => feed = f, time, access);
        try {
            await WaitUntilAsync(() => chat.Items.Count == 300, what: "the seed");
            await Assert.That(chat.RequestOlder()).IsTrue();
            await feed!.PendingOlderForTesting!;
            await WaitUntilAsync(() => chat.Items.Count > 590, what: "the first older page");
            await Assert.That(chat.RequestOlder()).IsTrue();
            await feed.PendingOlderForTesting!;
            await WaitUntilAsync(() => chat.Items.OfType<ToolGroupItem>().Any(), what: "the call");

            var call = chat.Items.OfType<ToolGroupItem>().Single().Calls.Single();
            await Assert.That(call.Outcome).IsEqualTo(ToolOutcome.Done);
            await Assert.That(call.Result).IsEqualTo("a.txt");
        } finally { await chat.TeardownAsync(); }
    });

    /// A page publish whose call is paged in after its result still names the page it published.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_publish_paged_in_after_its_result_still_names_its_page() => RunOnUiAsync(async () => {
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        var time = new FakeTimeProvider();
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 400).Select(i => i switch {
            99  => RemoteFixtures.Event(i, CanonicalEventTypes.AssistantToolCallsGenerated,
                """{"tool_calls":[{"call_id":"p1","tool_name":"publish_page","arguments":{"title":"Notes"}}]}"""),
            100 => ResultEvent(i, "p1", """{"page_id":"pg1"}"""),
            _   => TextEvent(i),
        })]);
        RemoteTranscriptFeed? feed = null;
        var activity = new PlanActivity();
        var chat = RemoteChat(server, f => feed = f, time, access, planActivity: activity);
        try {
            await WaitUntilAsync(() => chat.Items.Count > 0, what: "the seed");
            await Assert.That(activity.PublishedPageIds).IsEmpty();
            await Assert.That(chat.RequestOlder()).IsTrue();
            await feed!.PendingOlderForTesting!;
            await WaitUntilAsync(() => chat.Items.Count > 390, what: "the older page");

            await Assert.That(activity.PublishedPageIds).IsEquivalentTo(new[] { "pg1" });
        } finally { await chat.TeardownAsync(); }
    });

    /// A question card is retired once the ask it answers is paged in, its result having been loaded first.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_question_paged_in_after_its_answer_retires_its_card() => RunOnUiAsync(async () => {
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        var time = new FakeTimeProvider();
        var permissions = new FakePermissionService();
        permissions.Add(PermissionEntries.ServerQuestion());
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 400).Select(i => i switch {
            99  => RemoteFixtures.Event(i, CanonicalEventTypes.AssistantToolCallsGenerated,
                """{"tool_calls":[{"call_id":"q1","tool_name":"AskUserQuestion","arguments":{"questions":[{"question":"Pick","options":[{"label":"A"},{"label":"B"}]}]}}]}"""),
            100 => ResultEvent(i, "q1", "A"),
            _   => TextEvent(i),
        })]);
        RemoteTranscriptFeed? feed = null;
        var chat = RemoteChat(server, f => feed = f, time, access, permissions: permissions);
        try {
            await WaitUntilAsync(() => chat.Items.OfType<AssistantTextItem>().Any(), what: "the seed");
            await Assert.That(permissions.Withdrawn).IsEmpty();
            await Assert.That(chat.RequestOlder()).IsTrue();
            await feed!.PendingOlderForTesting!;

            await WaitUntilAsync(() => permissions.Withdrawn.Count > 0, what: "the card withdrawn");
        } finally { await chat.TeardownAsync(); }
    });

    /// An ended session pages back by itself while fewer events than the prefetch are loaded, then waits
    /// for the reader: the page that crosses it is the last one read unasked.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_ended_session_pages_back_unasked_up_to_the_prefetch() => RunOnUiAsync(async () => {
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        var time = new FakeTimeProvider();
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 1500).Select(TextEvent)]);
        RemoteTranscriptFeed? feed = null;
        var chat = RemoteChat(server, f => feed = f, time, access, prefetch: 700);
        try {
            await WaitUntilAsync(() => chat.Items.Count == 900, what: "pages read up to the prefetch");
            await feed!.PendingOlderForTesting!;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            await Assert.That(chat.Items.Count).IsEqualTo(900);
            await Assert.That((chat.Items[0] as AssistantTextItem)?.Text).IsEqualTo("m600");
            await Assert.That(feed.PrefetchesOlder).IsFalse();
            await Assert.That(chat.RequestOlder()).IsTrue();
            await feed.PendingOlderForTesting!;
            await WaitUntilAsync(() => chat.Items.Count == 1200, what: "the page asked for");
        } finally { await chat.TeardownAsync(); }
    });

    /// A card shown while no rows were loaded packs with the group an older page then puts in front of it:
    /// no newer line arrives to re-run the card sync after that page.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_card_packs_with_a_group_paged_in_ahead_of_it() => RunOnUiAsync(async () => {
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        var time = new FakeTimeProvider();
        var permissions = new FakePermissionService();
        permissions.Add(PermissionEntries.ServerEntry());
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 700).Select(i => i switch {
            < 50 => TextEvent(i),
            50   => RemoteFixtures.Event(i, CanonicalEventTypes.AssistantToolCallsGenerated, RemoteFixtures.LsCall),
            _    => ResultEvent(i, $"x{i}"),
        })]);
        var chat = RemoteChat(server, _ => { }, time, access, permissions: permissions, live: true);
        try {
            await WaitUntilAsync(() => chat.Items.OfType<ToolGroupItem>().Any(), what: "the paged-in group");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var group = chat.Items.OfType<ToolGroupItem>().Single();
            await Assert.That(chat.Items[^2]).IsSameReferenceAs(group);
            await Assert.That(group.PacksWithCard).IsTrue();
            await Assert.That(CardRows(chat).Single().PacksWithPrevious).IsTrue();
        } finally { await chat.TeardownAsync(); }
    });

    /// A live session pages back to its start by itself, so a subagent started before the window and still
    /// running shows as running without the reader scrolling.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_live_session_pages_back_to_a_run_started_before_the_window() => RunOnUiAsync(async () => {
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        var time = new FakeTimeProvider();
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 700).Select(i => i == 10
            ? RemoteFixtures.Event(i, CanonicalEventTypes.AssistantToolCallsGenerated,
                """{"tool_calls":[{"call_id":"bg1","tool_name":"Bash","arguments":{"command":"sleep 999","run_in_background":true}}]}""")
            : TextEvent(i))]);
        var chat = RemoteChat(server, _ => { }, time, access, live: true);
        try {
            await WaitUntilAsync(() => chat.Items.OfType<AssistantTextItem>().Any(item => item.Text == "m0"), what: "the session's start");
            await Assert.That(chat.Items.OfType<AssistantTextItem>().Count()).IsEqualTo(699);
        } finally { await chat.TeardownAsync(); }
    });

    /// A `!` command at the end of an older page takes the output row that opened the rows already shown.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_shell_command_paged_in_takes_its_output_row() => RunOnUiAsync(async () => {
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        var time = new FakeTimeProvider();
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 400).Select(i => i switch {
            99  => RemoteFixtures.Event(i, CanonicalEventTypes.UserMessageReceived, """{"content":"<bash-input>ls</bash-input>"}"""),
            100 => RemoteFixtures.Event(i, CanonicalEventTypes.UserMessageReceived, """{"content":"<bash-stdout>a.txt</bash-stdout><bash-stderr></bash-stderr>"}"""),
            _   => TextEvent(i),
        })]);
        RemoteTranscriptFeed? feed = null;
        var chat = RemoteChat(server, f => feed = f, time, access, vendor: "claude");
        try {
            await WaitUntilAsync(() => chat.Items.OfType<AssistantTextItem>().Any(), what: "the seed");
            await Assert.That(chat.Items[0] is ShellCommandItem { HasCommand: false }).IsTrue();
            await Assert.That(chat.RequestOlder()).IsTrue();
            await feed!.PendingOlderForTesting!;
            await WaitUntilAsync(() => chat.Items.Count > 390, what: "the older page");

            var shell = chat.Items.OfType<ShellCommandItem>().Single();
            await Assert.That(shell.Command).Contains("ls");
            await Assert.That(shell.Output).Contains("a.txt");
        } finally { await chat.TeardownAsync(); }
    });

    /// With no rows shown, a `!` command on an older page is still the row live output lands in.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Live_output_joins_a_shell_command_paged_in_behind_rowless_events() => RunOnUiAsync(async () => {
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        var time = new FakeTimeProvider();
        var lane = new FakeServerLane();
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 400).Select(i => i switch {
            99 => RemoteFixtures.Event(i, CanonicalEventTypes.UserMessageReceived, """{"content":"<bash-input>ls</bash-input>"}"""),
            < 99 => TextEvent(i),
            _ => ResultEvent(i, $"x{i}"),
        })]);
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, Observable.Return(Ended("s1")), Observable.Return<string[]?>(null),
            new AcceptingChatInput(), new NoAttachmentUploader(), key => new RemoteTranscriptFeed(key, "claude", access, server.Reader, lane, time, _ => { }),
            new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time));
        try {
            await WaitUntilAsync(() => chat.Items.OfType<ShellCommandItem>().Any(), what: "the paged-in command");
            await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the live tail");
            lane.PushStreamEvent(RemoteFixtures.Envelope("s1", 400, CanonicalEventTypes.UserMessageReceived,
                """{"content":"<bash-stdout>a.txt</bash-stdout><bash-stderr></bash-stderr>"}"""));
            await WaitUntilAsync(() => chat.Items.OfType<ShellCommandItem>().Single().HasOutput, what: "the output on the command");

            await Assert.That(chat.Items.OfType<ShellCommandItem>().Count()).IsEqualTo(1);
        } finally { await chat.TeardownAsync(); }
    });

    /// A pending card is not a transcript row: with only a card shown, the command paged in ahead of it
    /// is still the row live output lands in.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Live_output_joins_a_command_paged_in_ahead_of_a_pending_card() => RunOnUiAsync(async () => {
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        var time = new FakeTimeProvider();
        var lane = new FakeServerLane();
        var permissions = new FakePermissionService();
        permissions.Add(PermissionEntries.ServerEntry());
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 400).Select(i => i switch {
            99 => RemoteFixtures.Event(i, CanonicalEventTypes.UserMessageReceived, """{"content":"<bash-input>ls</bash-input>"}"""),
            < 99 => TextEvent(i),
            _ => ResultEvent(i, $"x{i}"),
        })]);
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, Observable.Return(Session("s1")), Observable.Return<string[]?>(null),
            new AcceptingChatInput(), new NoAttachmentUploader(),
            key => new RemoteTranscriptFeed(key, "claude", access, server.Reader, lane, time, _ => { }, prefetch: 0),
            new RecordingOpener(), time, permissions, new SessionRuns(time), sessionId: Observable.Return<string?>("s1"));
        try {
            await WaitUntilAsync(() => chat.Items.OfType<ShellCommandItem>().Any(), what: "the paged-in command");
            await Assert.That(chat.Items[^1] is PendingCardItem).IsTrue();
            await WaitUntilAsync(() => lane.Tails.Count == 1, what: "the live tail");
            lane.PushStreamEvent(RemoteFixtures.Envelope("s1", 400, CanonicalEventTypes.UserMessageReceived,
                """{"content":"<bash-stdout>a.txt</bash-stdout><bash-stderr></bash-stderr>"}"""));
            await WaitUntilAsync(() => chat.Items.OfType<ShellCommandItem>().Any(item => item.HasOutput), what: "the output");

            await Assert.That(chat.Items.OfType<ShellCommandItem>().Single().Command).Contains("ls");
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
