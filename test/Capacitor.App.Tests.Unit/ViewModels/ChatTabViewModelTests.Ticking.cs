using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class ChatTabViewModelTests {
    /// An ended status alone does not stop the poll: the daemon reports it before the agent has
    /// exited, and a journal is completed only once the runtime is gone. The poll stops after a read
    /// that follows the daemon dropping the agent, and a resume starts it again.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_poll_outlives_an_ended_status_until_the_daemon_drops_the_agent_and_restarts_on_resume() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            try {
                var path = Tmp.CreateFile("t.jsonl", [UserLine]);
                await h.PushAsync(Dto(path));
                await WaitUntilAsync(() => h.Chat.Items.Count == 1, what: "the initial load");
                await Assert.That(h.Chat.IsTickingForTesting).IsTrue();

                await h.PushAsync(Dto(path) with { Status = "Completed" });
                for (var i = 0; i < 4; i++) await h.TickAsync();
                await Assert.That(h.Chat.IsTickingForTesting).IsTrue();

                await File.AppendAllTextAsync(path, AssistantLine + "\n");
                h.Daemon.Agents.Remove("a1");
                await WaitUntilAsync(async () => {
                    await h.TickAsync();
                    return !h.Chat.IsTickingForTesting;
                }, what: "the poll to stop");
                await Assert.That(h.Chat.Items.Count).IsEqualTo(2);

                await h.PushAsync(Dto(path));
                await WaitUntilAsync(() => h.Chat.IsTickingForTesting, what: "the poll to restart on resume");
            } finally {
                await h.TeardownAsync();
            }
        });
    }

    /// A subagent pane's feed is polled on its own cadence after the end, because nothing reports
    /// when its last events have landed.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_ended_feed_with_no_writer_verdict_keeps_being_polled() {
        await RunOnUiAsync(async () => {
            var time = new FakeTimeProvider();
            var feed = new CountingFeed();
            var session = new BehaviorSubject<ChatSessionInfo>(Session("k") with { Status = "Exited", Ended = true });
            var chat = new ChatTabViewModel(
                "a1/sub", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), new ReadOnlyChatInput(),
                new NoAttachmentUploader(), _ => feed, new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time));
            try {
                await WaitUntilAsync(() => feed.Reads > 0, what: "the first read");
                for (var i = 0; i < 4; i++) {
                    time.Advance(ChatTabViewModel.PollInterval);
                    await (chat.PendingReadForTesting ?? Task.CompletedTask);
                }
                await WaitUntilAsync(() => feed.Reads >= 4, what: "reads after the end");
                await Assert.That(chat.IsTickingForTesting).IsTrue();
            } finally {
                await chat.TeardownAsync();
            }
        });
    }

    /// The tick that finds the poll settled stops it without one more read: a failure there would
    /// stand on screen with no tick left to retry it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_settled_poll_stops_without_reading_again() {
        await RunOnUiAsync(async () => {
            var time = new FakeTimeProvider();
            var feed = new CountingFeed(failAfter: 1);
            var session = new BehaviorSubject<ChatSessionInfo>(Session("k") with { Status = "Exited", Ended = true, WriterDone = true });
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Local, session, Observable.Return<string[]?>(null), new ReadOnlyChatInput(),
                new NoAttachmentUploader(), _ => feed, new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time));
            try {
                await WaitUntilAsync(() => feed.Reads > 0, what: "the settling read");
                await (chat.PendingReadForTesting ?? Task.CompletedTask);
                for (var i = 0; i < 3; i++) {
                    time.Advance(ChatTabViewModel.PollInterval);
                    Dispatcher.UIThread.RunJobs();
                    await (chat.PendingReadForTesting ?? Task.CompletedTask);
                }
                await Assert.That(chat.IsTickingForTesting).IsFalse();
                await Assert.That(feed.Reads).IsEqualTo(1);
                await Assert.That(chat.ShowsActivityNote).IsFalse();
            } finally {
                await chat.TeardownAsync();
            }
        });
    }

    sealed class CountingFeed(int failAfter = int.MaxValue) : IChatTranscriptFeed {
        int _reads;
        public int Reads => Volatile.Read(ref _reads);

        public FeedRead ReadAppended() =>
            Interlocked.Increment(ref _reads) > failAfter ? new(FeedStatus.Failed, [], Failure: "disk gone") : new(FeedStatus.Ok, []);

        public long? CurrentOffset => 0;
        public void Dispose() { }
    }
}
