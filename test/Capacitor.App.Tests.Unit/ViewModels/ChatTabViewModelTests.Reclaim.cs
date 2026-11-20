using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class ChatTabViewModelTests {
    sealed class LongFeed(int lines, int resets = 1) : IChatTranscriptFeed {
        int _reads;

        public FeedRead ReadAppended() {
            if (_reads++ >= resets) return new(FeedStatus.Ok, []);
            return new(FeedStatus.Reset, [.. Enumerable.Range(1, lines).Select(i => new ProjectedLine(new ChatProjectionResult([], [], []), i))]);
        }

        public long? CurrentOffset => lines;
        public void Dispose() { }
    }

    sealed class ReclaimHarness {
        readonly FakeTimeProvider _time = new();
        public BehaviorSubject<ChatSessionInfo> Sessions { get; } = new(Session("s1"));
        public int Collections;
        public ChatTabViewModel Chat { get; }

        public ReclaimHarness(Func<string, int> linesFor, int resets = 1) =>
            Chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, Sessions, Observable.Return<string[]?>(null), new ScriptedInput(), new NoAttachmentUploader(),
                key => new LongFeed(linesFor(key), resets), new RecordingOpener(), _time, new FakePermissionService(), new SessionRuns(_time)) {
                Reclaimer = new HeapReclaimer(_time, () => Collections++),
            };

        public void Settle() => _time.Advance(HeapReclaimer.Settle);

        public async Task PollAsync() {
            _time.Advance(ChatTabViewModel.PollInterval);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Switching_away_from_a_long_transcript_reclaims_the_heap() {
        await RunOnUiAsync(async () => {
            var h = new ReclaimHarness(key => key == "s1" ? HeapReclaimer.LineThreshold : 1);
            await WaitUntilAsync(() => h.Chat.HasSeed, what: "the long seed");

            h.Sessions.OnNext(Session("s2"));
            await WaitUntilAsync(() => h.Chat.HasSeed, what: "the short seed");
            h.Settle();
            await Assert.That(h.Collections).IsEqualTo(1);

            await h.Chat.TeardownAsync();
            h.Settle();
            await Assert.That(h.Collections).IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Closing_a_long_transcript_reclaims_the_heap() {
        await RunOnUiAsync(async () => {
            var h = new ReclaimHarness(_ => HeapReclaimer.LineThreshold);
            await WaitUntilAsync(() => h.Chat.HasSeed, what: "the seed");

            await h.Chat.TeardownAsync();
            h.Settle();
            await Assert.That(h.Collections).IsEqualTo(1);
        });
    }

    /// <summary>A replay replaces the rows on screen, so a transcript under the threshold stays under it however
    /// often it is replayed.</summary>
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_replayed_short_transcript_does_not_reclaim_the_heap() {
        await RunOnUiAsync(async () => {
            var half = HeapReclaimer.LineThreshold / 2 + 200;
            var h = new ReclaimHarness(_ => half, resets: 2);
            await WaitUntilAsync(() => h.Chat.HasSeed, what: "the seed");
            await h.PollAsync();
            await h.PollAsync();

            await h.Chat.TeardownAsync();
            h.Settle();
            await Assert.That(h.Collections).IsEqualTo(0);
        });
    }
}
