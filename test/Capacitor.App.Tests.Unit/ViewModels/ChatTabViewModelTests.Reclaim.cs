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
    sealed class LongFeed(int lines) : IChatTranscriptFeed {
        bool _read;

        public FeedRead ReadAppended() {
            if (_read) return new(FeedStatus.Ok, []);
            _read = true;
            return new(FeedStatus.Reset, [.. Enumerable.Range(1, lines).Select(i => new ProjectedLine(new ChatProjectionResult([], [], []), i))]);
        }

        public long? CurrentOffset => lines;
        public void Dispose() { }
    }

    sealed class ReclaimHarness {
        public FakeTimeProvider Time { get; } = new();
        public BehaviorSubject<ChatSessionInfo> Session { get; } = new(ChatTabViewModelTests.Session("s1"));
        public int Collections;
        public ChatTabViewModel Chat { get; }

        public ReclaimHarness(Func<string, int> linesFor) =>
            Chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, Session, Observable.Return<string[]?>(null), new ScriptedInput(), new NoAttachmentUploader(),
                key => new LongFeed(linesFor(key)), new RecordingOpener(), Time, new FakePermissionService(), new SessionRuns(Time)) {
                Reclaimer = new HeapReclaimer(Time, () => Collections++),
            };

        public void Settle() => Time.Advance(HeapReclaimer.Settle);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Switching_away_from_a_long_transcript_reclaims_the_heap() {
        await RunOnUiAsync(async () => {
            var h = new ReclaimHarness(key => key == "s1" ? HeapReclaimer.LineThreshold : 1);
            await WaitUntilAsync(() => h.Chat.HasSeed, what: "the long seed");

            h.Session.OnNext(Session("s2"));
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
}
