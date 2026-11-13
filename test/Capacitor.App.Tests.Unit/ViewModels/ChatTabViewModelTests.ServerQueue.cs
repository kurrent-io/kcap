using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.WorkItems;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class ChatTabViewModelTests {
    /// <summary>One poll that reads what is on disk now. A tick that lands while an earlier read is still
    /// in flight starts nothing, and that earlier read may have missed the newest line, so it is drained
    /// first.</summary>
    static async Task PollAsync(ChatTabViewModel chat, FakeTimeProvider time) {
        await (chat.PendingReadForTesting ?? Task.CompletedTask);
        time.Advance(ChatTabViewModel.PollInterval);
        await (chat.PendingReadForTesting ?? Task.CompletedTask);
    }

    static IObservable<ViewerIdentityDto?> Viewer(string? id) =>
        Observable.Return<ViewerIdentityDto?>(id is null ? null : new() { UserId = id });

    static QueuedInputItem Item(string text, Guid id, string? sender = "u2") => new() { DispatchId = id, Text = text, SenderUserId = sender };

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Discard_removes_only_the_confirmed_attempt_and_preserves_failed_discards_and_the_draft() {
        await RunOnUiAsync(async () => {
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2"));
            var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
            var input = new ServerChatInput("a1", lane, access, session, hasTerminal: true, senderIdentity: Viewer("u2"));
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), input, new NoAttachmentUploader(), _ => new ScriptedFeed { ResetNext = true },
                new RecordingOpener(), new FakeTimeProvider(), new FakePermissionService(), new SessionRuns(new FakeTimeProvider()), serverQueue: queue);
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");
            var first = Item("same text", Guid.NewGuid());
            var second = Item("same text", Guid.NewGuid());
            queue.OnNext([first, second]);
            chat.ComposerText = "keep my draft";
            var row = chat.QueuedMessages[0];
            await Assert.That(row.CanDiscard).IsTrue();
            var response = new TaskCompletionSource<HubCallOutcome>();
            lane.DiscardHandler = id => {
                if (id != first.DispatchId) throw new InvalidOperationException("wrong dispatch");
                return response.Task;
            };
            var discard = chat.DiscardQueuedCommand.Execute(row).ToTask();
            await WaitUntilAsync(() => !row.CanDiscard, what: "discard in flight");
            await Assert.That(chat.QueuedMessages.Count).IsEqualTo(2);
            response.SetResult(HubCallOutcome.Ok);
            await discard;
            await Assert.That(chat.QueuedMessages.Single().DispatchId).IsEqualTo(second.DispatchId);
            queue.OnNext([first, second]);
            await Assert.That(chat.QueuedMessages.Count).IsEqualTo(1);
            queue.OnNext([second]);
            // A subscription response can arrive after the newer removal push.
            queue.OnNext([first, second]);
            await Assert.That(chat.QueuedMessages.Count).IsEqualTo(1);

            lane.DiscardHandler = _ => Task.FromResult(HubCallOutcome.Failed("old server"));
            await chat.DiscardQueuedCommand.Execute(chat.QueuedMessages.Single());
            await Assert.That(chat.QueuedMessages.Single().DispatchId).IsEqualTo(second.DispatchId);
            await Assert.That(chat.ComposerText).IsEqualTo("keep my draft");
            await Assert.That(lane.UserInputs).IsEmpty();
            await chat.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_held_send_is_discarded_through_the_server_and_an_unlisted_one_here() {
        await RunOnUiAsync(async () => {
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2"));
            var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
            var input = new ServerChatInput("a1", lane, access, session, hasTerminal: true, senderIdentity: Viewer("u2"));
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), input, new NoAttachmentUploader(), _ => new ScriptedFeed { ResetNext = true },
                new RecordingOpener(), new FakeTimeProvider(), new FakePermissionService(), new SessionRuns(new FakeTimeProvider()), serverQueue: queue);
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");
            var discarded = new List<Guid>();
            var reply = new TaskCompletionSource<HubCallOutcome>();
            lane.DiscardHandler = id => { discarded.Add(id); return reply.Task; };

            chat.ComposerText = "held";
            await chat.SendCommand.Execute();
            chat.ComposerText = "dropped";
            await chat.SendCommand.Execute();
            var held = chat.QueuedMessages.Single(q => q.Text == "held");
            var dropped = chat.QueuedMessages.Single(q => q.Text == "dropped");
            var heldId = Guid.NewGuid();
            queue.OnNext([Item("held", heldId), Item("dropped", Guid.NewGuid())]);
            queue.OnNext([Item("held", heldId)]);
            await Assert.That(held.CanSendNow).IsFalse();
            await Assert.That(held.CanDiscard).IsTrue();
            await Assert.That(dropped.CanSendNow).IsTrue();

            var pending = chat.DiscardQueuedCommand.Execute(held).ToTask();
            await WaitUntilAsync(() => !held.CanDiscard, what: "discard in flight");
            await Assert.That(dropped.CanSendNow).IsFalse();
            reply.SetResult(HubCallOutcome.Ok);
            await pending;
            await Assert.That(discarded).IsEquivalentTo([heldId]);
            await Assert.That(dropped.CanSendNow).IsTrue();

            await chat.DiscardQueuedCommand.Execute(dropped);
            await Assert.That(chat.QueuedMessages).IsEmpty();
            await Assert.That(discarded).IsEquivalentTo([heldId]);
            await Assert.That(lane.UserInputs).IsEquivalentTo([("a1", "held"), ("a1", "dropped")]);
            await chat.TeardownAsync();
        });
    }

    /// Snapshots carry no revision and can land out of order: a listing after an unlisting makes
    /// the row held again, and nothing brings back a dispatch that was discarded, here or on the
    /// server, whatever session the pane moved through meanwhile.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_late_snapshot_cannot_bring_back_a_discarded_dispatch_or_leave_a_held_one_resendable() {
        await RunOnUiAsync(async () => {
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2"));
            var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
            var input = new ServerChatInput("a1", lane, access, session, hasTerminal: true, senderIdentity: Viewer("u2"));
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), input, new NoAttachmentUploader(), _ => new ScriptedFeed { ResetNext = true },
                new RecordingOpener(), new FakeTimeProvider(), new FakePermissionService(), new SessionRuns(new FakeTimeProvider()), serverQueue: queue);
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");
            var serverDiscards = new List<Guid>();
            lane.DiscardHandler = id => { serverDiscards.Add(id); return Task.FromResult(HubCallOutcome.Ok); };

            chat.ComposerText = "mine";
            await chat.SendCommand.Execute();
            var own = chat.QueuedMessages.Single();
            var ownId = Item("mine", Guid.NewGuid());
            queue.OnNext([ownId]);
            queue.OnNext([]);
            await Assert.That(own.CanSendNow).IsTrue();
            queue.OnNext([ownId]);
            await Assert.That(own.CanSendNow).IsFalse();
            await Assert.That(own.IsUnconfirmed).IsFalse();

            queue.OnNext([]);
            await chat.DiscardQueuedCommand.Execute(own);
            await Assert.That(serverDiscards).IsEmpty();
            queue.OnNext([ownId]);
            await Assert.That(chat.QueuedMessages).IsEmpty();

            var theirs = Item("theirs", Guid.NewGuid());
            queue.OnNext([theirs]);
            var reply = new TaskCompletionSource<HubCallOutcome>();
            lane.DiscardHandler = id => { serverDiscards.Add(id); return reply.Task; };
            var pending = chat.DiscardQueuedCommand.Execute(chat.QueuedMessages.Single()).ToTask();
            await WaitUntilAsync(() => serverDiscards.Count == 1, what: "the discard request");
            queue.OnNext([]);
            queue.OnNext([theirs]);
            await Assert.That(chat.QueuedMessages.Count).IsEqualTo(1);
            reply.SetResult(HubCallOutcome.Ok);
            await pending;
            await Assert.That(chat.QueuedMessages).IsEmpty();

            session.OnNext(Session("s2"));
            session.OnNext(Session("s1"));
            queue.OnNext([theirs, ownId]);
            await Assert.That(chat.QueuedMessages).IsEmpty();
            await chat.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_remote_discard_waits_for_the_attempts_dispatch_identity() => RunOnUiAsync(async () => {
        var lane = new FakeServerLane();
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2"));
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
        using var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
        var time = new FakeTimeProvider();
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
            new ServerChatInput("a1", lane, access, session, hasTerminal: true, senderIdentity: Viewer("u2")), new NoAttachmentUploader(), _ => new ScriptedFeed { ResetNext = true },
            new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time), serverQueue: queue);
        try {
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");
            chat.ComposerText = "same prompt";
            await chat.SendCommand.Execute();
            var own = chat.QueuedMessages.Single();
            await Assert.That(own.CanDiscard).IsFalse();
            await chat.DiscardQueuedCommand.Execute(own);
            await Assert.That(chat.QueuedMessages.Single()).IsSameReferenceAs(own);
            var item = Item("same prompt", Guid.NewGuid());
            queue.OnNext([item]);
            await Assert.That(own.CanDiscard).IsTrue();
            lane.DiscardHandler = id => {
                if (id != item.DispatchId) throw new InvalidOperationException("Wrong dispatch");
                return Task.FromResult(HubCallOutcome.Ok);
            };
            await chat.DiscardQueuedCommand.Execute(own);
            queue.OnNext([item]);
            await Assert.That(chat.QueuedMessages).IsEmpty();
        } finally { await chat.TeardownAsync(); }
    });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public Task Same_text_from_another_sender_does_not_assign_their_dispatch_to_an_own_attempt(bool callerKnown) => RunOnUiAsync(async () => {
        var lane = new FakeServerLane();
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: callerKnown ? "u2" : null));
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
        using var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
        var time = new FakeTimeProvider();
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
            new ServerChatInput("a1", lane, access, session, hasTerminal: true, senderIdentity: Viewer(callerKnown ? "u2" : null)), new NoAttachmentUploader(), _ => new ScriptedFeed { ResetNext = true },
            new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time), serverQueue: queue);
        try {
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");
            chat.ComposerText = "same prompt";
            await chat.SendCommand.Execute();
            var own = chat.QueuedMessages.Single();
            var theirs = Item("same prompt", Guid.NewGuid(), "u1");
            var mine = Item("same prompt", Guid.NewGuid(), "u2");
            queue.OnNext([theirs, mine]);

            if (!callerKnown) {
                await Assert.That(own.DispatchId).IsNull();
                await Assert.That(own.CanDiscard).IsFalse();
                await Assert.That(chat.QueuedMessages.Count).IsEqualTo(3);
                return;
            }
            await Assert.That(own.DispatchId).IsEqualTo(mine.DispatchId);
            var discarded = new List<Guid>();
            lane.DiscardHandler = id => { discarded.Add(id); return Task.FromResult(HubCallOutcome.Ok); };
            await chat.DiscardQueuedCommand.Execute(own);
            await Assert.That(discarded).IsEquivalentTo([mine.DispatchId]);
            await Assert.That(chat.QueuedMessages.Single().DispatchId).IsEqualTo(theirs.DispatchId);
        } finally { await chat.TeardownAsync(); }
    });

    /// A failed discard says so; a later one that succeeds takes that hint away with the row.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_discard_that_succeeds_on_retry_clears_its_failure_hint() {
        await RunOnUiAsync(async () => {
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2"));
            var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
            var input = new ServerChatInput("a1", lane, access, session, hasTerminal: true, senderIdentity: Viewer("u2"));
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), input, new NoAttachmentUploader(), _ => new ScriptedFeed { ResetNext = true },
                new RecordingOpener(), new FakeTimeProvider(), new FakePermissionService(), new SessionRuns(new FakeTimeProvider()), serverQueue: queue);
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");
            queue.OnNext([Item("old attempt", Guid.NewGuid())]);
            var row = chat.QueuedMessages.Single();

            lane.DiscardHandler = _ => Task.FromResult(HubCallOutcome.Failed("not yet"));
            await chat.DiscardQueuedCommand.Execute(row);
            await Assert.That(chat.ComposerHint).IsEqualTo("Could not discard this attempt. Try again.");

            lane.DiscardHandler = _ => Task.FromResult(HubCallOutcome.Ok);
            await chat.DiscardQueuedCommand.Execute(row);
            await Assert.That(chat.QueuedMessages).IsEmpty();
            await Assert.That(chat.ComposerHint).IsNotEqualTo("Could not discard this attempt. Try again.");
            await chat.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Discarding_a_retry_keeps_its_channel_warning_when_the_original_echoes() => RunOnUiAsync(async () => {
        var lane = new FakeServerLane();
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
        using var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
        var time = new FakeTimeProvider();
        var path = Tmp.CreateFile("discard-retry-notice.jsonl", [AssistantLine]);
        var input = new ServerChatInput("a1", lane, access, session, hasTerminal: true, senderIdentity: Viewer("u2"));
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), input,
            new NoAttachmentUploader(),
            _ => new SeededLocalFeed(new LocalTranscriptFeed(path, TranscriptChat.For("claude")!, "a1", time, _ => { })),
            new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time), serverQueue: queue);
        try {
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");
            chat.ComposerText = "same prompt";
            await chat.SendCommand.Execute();
            var original = chat.QueuedMessages.Single();
            queue.OnNext([Item("same prompt", Guid.NewGuid())]);
            queue.OnNext([]);
            lane.UserInputHandler = _ => Task.FromResult(HubCallOutcome.Failed("reply lost"));
            await chat.SendQueuedCommand.Execute(original);
            var retry = chat.QueuedMessages.Single();
            queue.OnNext([Item("same prompt", Guid.NewGuid())]);
            queue.OnNext([]);
            var warning = input.Hint;
            await Assert.That(warning).Contains("delivery unconfirmed");
            chat.ComposerText = "keep my draft";
            await chat.DiscardQueuedCommand.Execute(retry);

            File.AppendAllText(path, User("same prompt") + "\n");
            await PollAsync(chat, time);
            await Assert.That(original.Acknowledged).IsTrue();
            await Assert.That(input.Hint).IsEqualTo(warning);
            await Assert.That(chat.ComposerText).IsEqualTo("keep my draft");
        } finally { await chat.TeardownAsync(); }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Discarding_a_retry_preserves_the_recall_order_of_distinct_prompts() => RunOnUiAsync(async () => {
        var input = new ScriptedInput();
        var h = new Harness(TranscriptChat.For("claude"), input: input);
        try {
            var path = Tmp.CreateFile("discard-retry-recall.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path) with { AwaitingInput = true });
            await SendAsync(h, input, "older prompt");
            var original = h.Chat.QueuedMessages.Single();
            var resend = h.Chat.SendQueuedCommand.Execute(original).ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await resend;
            var retry = h.Chat.QueuedMessages.Single();
            await SendAsync(h, input, "newer prompt");
            await h.Chat.DiscardQueuedCommand.Execute(retry);
            h.Chat.ComposerText = "";

            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("newer prompt");
            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("older prompt");
            await Assert.That(h.Chat.RecallNewer()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("newer prompt");
            await Assert.That(h.Chat.RecallNewer()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("");
        } finally { await h.TeardownAsync(); }
    });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public Task Discarding_a_retry_restores_its_original_before_later_matching_sends(bool laterSend) => RunOnUiAsync(async () => {
        var lane = new FakeServerLane();
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
        using var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
        var time = new FakeTimeProvider();
        var path = Tmp.CreateFile("discard-retry.jsonl", [AssistantLine]);
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
            new ServerChatInput("a1", lane, access, session, hasTerminal: true, senderIdentity: Viewer("u2")),
            new NoAttachmentUploader(),
            _ => new SeededLocalFeed(new LocalTranscriptFeed(path, TranscriptChat.For("claude")!, "a1", time, _ => { })),
            new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time), serverQueue: queue);
        try {
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");
            chat.ComposerText = "same prompt";
            await chat.SendCommand.Execute();
            var original = chat.QueuedMessages.Single();
            var first = Item("same prompt", Guid.NewGuid());
            queue.OnNext([first]);
            queue.OnNext([]);
            await chat.SendQueuedCommand.Execute(original);
            var retry = chat.QueuedMessages.Single();
            var second = Item("same prompt", Guid.NewGuid());
            queue.OnNext([second]);
            queue.OnNext([]);
            await Assert.That(retry.IsUnconfirmed).IsTrue();

            QueuedChatMessage? later = null;
            if (laterSend) {
                chat.ComposerText = "same prompt";
                await chat.SendCommand.Execute();
                later = chat.QueuedMessages.Single(q => !ReferenceEquals(q, retry));
                queue.OnNext([first]);
            }
            chat.ComposerText = "keep my draft";
            await chat.DiscardQueuedCommand.Execute(retry);
            await Assert.That(chat.ComposerText).IsEqualTo("keep my draft");

            if (later is null) {
                queue.OnNext([first, second]);
                await Assert.That(chat.QueuedMessages.Single()).IsSameReferenceAs(original);
                await Assert.That(original.IsForeign).IsFalse();
                await Assert.That(original.DispatchId).IsEqualTo(first.DispatchId);
            } else {
                File.AppendAllText(path, User("same prompt") + "\n");
                await PollAsync(chat, time);
                await Assert.That(original.Acknowledged).IsTrue();
                await Assert.That(later.Acknowledged).IsFalse();
                await Assert.That(chat.QueuedMessages.Single()).IsSameReferenceAs(later);
                queue.OnNext([second]);
            }
            await Assert.That(chat.QueuedMessages.Contains(retry)).IsFalse();
        } finally { await chat.TeardownAsync(); }
    });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task A_retry_listed_beside_its_original_hides_neither_attempt(bool listingBeforeAcceptance) {
        await RunOnUiAsync(async () => {
            using var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
            using var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var time = new FakeTimeProvider();
            var input = new ScriptedInput();
            var path = Tmp.CreateFile("retry-listing.jsonl", [AssistantLine]);
            var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
                input, new NoAttachmentUploader(),
                _ => new SeededLocalFeed(new LocalTranscriptFeed(path, TranscriptChat.For("claude")!, "a1", time, _ => { })),
                new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time), serverQueue: queue);
            try {
                await WaitUntilAsync(() => chat.HasSeed, what: "the seed");
                chat.ComposerText = "do it";
                var send = chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                var original = chat.QueuedMessages.Single();
                await Assert.That(original.CanSendNow).IsTrue();

                var resend = chat.SendQueuedCommand.Execute(original).ToTask();
                var first = Item("do it", Guid.NewGuid()) with { DispatchedAt = DateTimeOffset.UnixEpoch };
                var second = Item("do it", Guid.NewGuid()) with { DispatchedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) };
                if (listingBeforeAcceptance) queue.OnNext([second, first]);
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await resend;
                if (!listingBeforeAcceptance) queue.OnNext([second, first]);
                await Assert.That(chat.QueuedMessages.Count).IsEqualTo(2);
                await Assert.That(original.DispatchId).IsEqualTo(first.DispatchId);
                await Assert.That(chat.QueuedMessages.Contains(original)).IsTrue();
                var retry = chat.QueuedMessages.Single(q => !ReferenceEquals(q, original));
                await Assert.That(retry.IsForeign).IsFalse();
                await Assert.That(retry.DispatchId).IsEqualTo(second.DispatchId);
                await Assert.That(original.CanSendNow || retry.CanSendNow).IsFalse();

                File.AppendAllText(path, User("do it") + "\n");
                await PollAsync(chat, time);
                queue.OnNext([second]);
                await Assert.That(chat.QueuedMessages.Single()).IsSameReferenceAs(retry);
                queue.OnNext([]);
                File.AppendAllText(path, User("do it") + "\n");
                await PollAsync(chat, time);
                await Assert.That(chat.HasQueuedMessages).IsFalse();
            } finally { await chat.TeardownAsync(); }
        });
    }

    sealed class SeededLocalFeed(IChatTranscriptFeed inner) : IChatTranscriptFeed {
        bool _seeded;
        public long? CurrentOffset => inner.CurrentOffset;
        public FeedRead ReadAppended() {
            var read = inner.ReadAppended();
            if (_seeded) return read;
            _seeded = true;
            return read with { Status = FeedStatus.Reset, SnapshotOffset = inner.CurrentOffset };
        }
        public void Dispose() => inner.Dispose();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_late_original_echo_does_not_retire_a_server_held_retry() => RunOnUiAsync(async () => {
        using var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
        using var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
        var time = new FakeTimeProvider();
        var path = Tmp.CreateFile("late-echo.jsonl", [AssistantLine]);
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
            new AcceptingChatInput(), new NoAttachmentUploader(),
            _ => new SeededLocalFeed(new LocalTranscriptFeed(path, TranscriptChat.For("claude")!, "a1", time, _ => { })),
            new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time), serverQueue: queue);
        try {
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");
            chat.ComposerText = "look";
            await chat.SendCommand.Execute();
            var original = chat.QueuedMessages.Single();
            queue.OnNext([Item("look", Guid.NewGuid())]);
            queue.OnNext([]);
            await chat.SendQueuedCommand.Execute(original);
            var retry = chat.QueuedMessages.Single();
            queue.OnNext([Item("look", Guid.NewGuid())]);
            File.AppendAllText(path, User("look") + "\n");
            await PollAsync(chat, time);
            await Assert.That(chat.QueuedMessages.Single()).IsSameReferenceAs(retry);
            await Assert.That(retry.CanSendNow).IsFalse();
            queue.OnNext([]);
            File.AppendAllText(path, User("look") + "\n");
            await PollAsync(chat, time);
            await Assert.That(chat.HasQueuedMessages).IsFalse();
        } finally { await chat.TeardownAsync(); }
    });

    /// A local send nothing will ever echo — an interactive slash command — would otherwise sit in
    /// the strip for the life of the pane.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_local_send_with_no_receipt_can_be_discarded_and_keeps_the_draft() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
                await h.PushAsync(Dto(path) with { AwaitingInput = true });
                await SendAsync(h, input, "/status");
                var row = h.Chat.QueuedMessages.Single();
                await Assert.That(row.PickupNote).IsEqualTo(QueuedChatMessage.IdlePickupNote);
                await Assert.That(row.CanDiscard).IsTrue();
                h.Chat.ComposerText = "next thought";

                await h.Chat.DiscardQueuedCommand.Execute(row);
                await Assert.That(h.Chat.QueuedMessages).IsEmpty();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.ComposerText).IsEqualTo("next thought");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_servers_queue_confirms_an_own_send_and_lists_and_retires_the_others() {
        await RunOnUiAsync(async () => {
            var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), new AcceptingChatInput("u1"), new NoAttachmentUploader(), _ => new ScriptedFeed { ResetNext = true },
                new RecordingOpener(), new FakeTimeProvider(), new FakePermissionService(), new SessionRuns(new FakeTimeProvider()), serverQueue: queue);
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");

            chat.ComposerText = "do it";
            await chat.SendCommand.Execute();
            var own = chat.QueuedMessages.Single();
            await Assert.That(own.IsForeign).IsFalse();
            await Assert.That(own.IsAwaitingPickup).IsTrue();
            await Assert.That(own.CanSendNow).IsTrue();

            var mine = Guid.NewGuid();
            var theirs = Guid.NewGuid();
            queue.OnNext([Item("do it", mine, sender: "u1"), Item("and this", theirs)]);
            await Assert.That(chat.QueuedMessages.Count).IsEqualTo(2);
            await Assert.That(own.IsUnconfirmed).IsFalse();
            await Assert.That(own.IsAwaitingPickup).IsTrue();
            await Assert.That(own.CanSendNow).IsFalse();
            var foreign = chat.QueuedMessages.Single(q => q.IsForeign);
            await Assert.That(foreign.Text).IsEqualTo("and this");
            await Assert.That(foreign.IsAwaitingPickup).IsFalse();
            await Assert.That(chat.QueueSummary).IsEqualTo("1 message queued · 1 unconfirmed");

            queue.OnNext([Item("do it", mine, sender: "u1")]);
            await Assert.That(chat.QueuedMessages.Single()).IsSameReferenceAs(own);

            // The own message leaves with the transcript's echo, never with the queue alone — but a
            // dispatch the server stopped listing was delivered or withdrawn, and only the echo can
            // say which, so the row stops promising a pickup and is no longer the server's to retire.
            queue.OnNext([]);
            await Assert.That(chat.QueuedMessages.Single()).IsSameReferenceAs(own);
            await Assert.That(own.IsAwaitingPickup).IsFalse();
            await Assert.That(own.IsUnconfirmed).IsTrue();
            await Assert.That(own.CanSendNow).IsTrue();
            await Assert.That(own.CanDiscard).IsTrue();
            await Assert.That(chat.QueueSummary).IsEqualTo("1 message unconfirmed");

            // An item the server sent no id for is unkeyed: nothing here can retire it later, so
            // it is neither shown nor allowed to match a send of this pane's own.
            queue.OnNext([Item("do it", Guid.Empty, sender: "u1"), Item("and this", theirs)]);
            await Assert.That(chat.QueuedMessages.Count(q => q.IsForeign)).IsEqualTo(1);
            await Assert.That(chat.QueuedMessages.Single(q => q.IsForeign).Text).IsEqualTo("and this");
            await chat.TeardownAsync();
        });
    }

    /// A foreign row answers for one session. The pane moving to another — or to none, where no
    /// snapshot can ever arrive to retire it — drops it, while this pane's own send rides along.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_session_change_drops_the_foreign_rows_and_keeps_an_own_send() {
        await RunOnUiAsync(async () => {
            var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), new AcceptingChatInput(), new NoAttachmentUploader(), _ => new ScriptedFeed { ResetNext = true },
                new RecordingOpener(), new FakeTimeProvider(), new FakePermissionService(), new SessionRuns(new FakeTimeProvider()), serverQueue: queue);
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");

            chat.ComposerText = "do it";
            await chat.SendCommand.Execute();
            queue.OnNext([Item("and this", Guid.NewGuid())]);
            await Assert.That(chat.QueuedMessages.Count).IsEqualTo(2);

            session.OnNext(Session("s2"));
            await Assert.That(chat.QueuedMessages.Single().IsForeign).IsFalse();

            queue.OnNext([Item("and theirs again", Guid.NewGuid())]);
            await Assert.That(chat.QueuedMessages.Count).IsEqualTo(2);
            session.OnNext(Session(null));
            await Assert.That(chat.QueuedMessages.Single().IsForeign).IsFalse();
            await chat.TeardownAsync();
        });
    }

    /// A foreign row is the server's, not this pane's: neither an ended session nor a transcript
    /// reset may rebase one or cast doubt on a delivery this pane never made.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_foreign_row_is_never_rebased_nor_marked_unconfirmed() {
        await RunOnUiAsync(async () => {
            var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var feed = new ScriptedFeed { ResetNext = true };
            var time = new FakeTimeProvider();
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), new AcceptingChatInput(), new NoAttachmentUploader(), _ => feed,
                new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time), serverQueue: queue);
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");

            chat.ComposerText = "do it";
            await chat.SendCommand.Execute();
            queue.OnNext([Item("and this", Guid.NewGuid())]);
            var own = chat.QueuedMessages.Single(q => !q.IsForeign);
            var foreign = chat.QueuedMessages.Single(q => q.IsForeign);

            session.OnNext(Session("s1") with { Ended = true });
            await Assert.That(own.IsUnconfirmed).IsTrue();
            await Assert.That(foreign.IsUnconfirmed).IsFalse();

            await (chat.PendingReadForTesting ?? Task.CompletedTask);
            feed.ResetNext = true;
            await PollAsync(chat, time);
            await Assert.That(chat.QueuedMessages.Single(q => q.IsForeign)).IsSameReferenceAs(foreign);
            await Assert.That(foreign.IsUnconfirmed).IsFalse();
            await chat.TeardownAsync();
        });
    }

    /// A refusal with nothing on screen replaces the wait with its reason; the next read that is
    /// not one clears it.
    /// A remote prompt sent before the transcript's seed has nothing to be acknowledged against: its
    /// echo can land inside the replayed history. Send waits for the seed; a local pane does not.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(AgentOrigin.Remote, false)]
    [Arguments(AgentOrigin.Local, true)]
    public async Task Send_waits_for_the_transcript_seed_on_the_remote_lane(AgentOrigin origin, bool beforeSeed) {
        await RunOnUiAsync(async () => {
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var feed = new ScriptedFeed();
            var time = new FakeTimeProvider();
            var chat = new ChatTabViewModel(
                "a1", origin, session, Observable.Return<string[]?>(null), new AcceptingChatInput(), new NoAttachmentUploader(), _ => feed,
                new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time));
            await (chat.PendingReadForTesting ?? Task.CompletedTask);
            chat.ComposerText = "do it";

            await Assert.That(await chat.SendCommand.CanExecute.FirstAsync()).IsEqualTo(beforeSeed);

            feed.ResetNext = true;
            await PollAsync(chat, time);
            await Assert.That(await chat.SendCommand.CanExecute.FirstAsync()).IsTrue();
            await chat.TeardownAsync();
        });
    }

    /// A queued prompt's send-now waits for the new feed's seed too, after the pane moves to another session.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_queued_prompt_waits_for_a_new_feeds_seed_before_sending_now() {
        await RunOnUiAsync(async () => {
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var feeds = new Dictionary<string, ScriptedFeed> { ["s1"] = new() { ResetNext = true }, ["s2"] = new() { ResetNext = true } };
            var time = new FakeTimeProvider();
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), new AcceptingChatInput(), new NoAttachmentUploader(), key => feeds[key],
                new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time));
            await WaitUntilAsync(() => chat.HasSeed, what: "the first feed's seed");
            chat.ComposerText = "do it";
            await chat.SendCommand.Execute();
            var own = chat.QueuedMessages.Single();
            await Assert.That(own.CanSendNow).IsTrue();

            session.OnNext(Session("s2"));
            await Assert.That(chat.HasSeed).IsFalse();
            await Assert.That(own.CanSendNow).IsFalse();

            await WaitUntilAsync(() => chat.HasSeed, what: "the new feed's seed");
            await Assert.That(own.CanSendNow).IsTrue();
            await chat.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_refused_read_says_why_in_place_of_the_wait_until_a_read_is_not_refused() {
        await RunOnUiAsync(async () => {
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var feed = new ScriptedFeed { FailNext = "not signed in" };
            var time = new FakeTimeProvider();
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), new AcceptingChatInput(), new NoAttachmentUploader(), _ => feed,
                new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time));
            await (chat.PendingReadForTesting ?? Task.CompletedTask);
            await Assert.That(chat.Phase).IsEqualTo(ChatTabPhase.Failed);
            await Assert.That(chat.PhaseNote).IsEqualTo("The transcript could not be read: not signed in");
            await Assert.That(chat.ActivityNote).IsEqualTo("");

            // How long a refusal stands is the feed's to say: a read that is not one clears it.
            await PollAsync(chat, time);
            await Assert.That(chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(chat.PhaseNote).IsEqualTo("Waiting for chat…");
            await chat.TeardownAsync();
        });
    }
}
