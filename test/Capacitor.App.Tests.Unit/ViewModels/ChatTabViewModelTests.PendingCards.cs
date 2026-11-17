using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using ReactiveUI.Reactive;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class ChatTabViewModelTests {
    const string MatchingQuestion = """{"questions":[{"question":"Run payload?","header":"Run payload","options":[{"label":"Yes"},{"label":"No"}]}]}""";
    const string OtherQuestion = """{"questions":[{"question":"Something else?","options":[{"label":"A"}]}]}""";
    const string QuestionCallLine = """{"type":"assistant","timestamp":"2026-08-28T10:00:00Z","message":{"content":[{"type":"tool_use","id":"qtool","name":"AskUserQuestion","input":{"questions":[{"question":"Run payload?","header":"Run payload","options":[{"label":"Yes"},{"label":"No"}]}]}}]}}""";
    const string QuestionResultLine = """{"type":"user","timestamp":"2026-08-28T10:00:01Z","message":{"content":[{"type":"tool_result","tool_use_id":"qtool","content":"The user did not answer the questions."}]}}""";
    const string QuestionAgainLine = """{"type":"assistant","timestamp":"2026-08-28T11:00:00Z","message":{"content":[{"type":"tool_use","id":"qtool2","name":"AskUserQuestion","input":{"questions":[{"question":"Run payload?","header":"Run payload","options":[{"label":"Yes"},{"label":"No"}]}]}}]}}""";
    const string QuestionAgainResultLine = """{"type":"user","timestamp":"2026-08-28T11:00:01Z","message":{"content":[{"type":"tool_result","tool_use_id":"qtool2","content":"The user did not answer the questions."}]}}""";
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Cards_are_filtered_to_the_agent_ordered_by_request_time_and_removed_on_resolve() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", requestedAt: "2026-08-28T10:00:02.0000000+00:00"));
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", requestedAt: "2026-08-28T10:00:01.0000000+00:00"));
            h.Permissions.Add(PermissionEntries.Entry("rX", "other", requestedAt: "2026-08-28T10:00:00.0000000+00:00"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 2, what: "two cards");
            await Assert.That(h.Chat.PendingCards.Select(c => c.RequestId).ToArray()).IsEquivalentTo(new[] { "r1", "r2" }, CollectionOrdering.Matching);
            await Assert.That(h.Chat.HasPendingCards).IsTrue();
            await Assert.That(CardRows(h.Chat).Select(c => c.Card.RequestId)).IsEquivalentTo(new[] { "r1", "r2" }, CollectionOrdering.Matching);

            h.Permissions.Remove("r1");
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "one card left");
            await Assert.That(h.Chat.PendingCards[0].RequestId).IsEqualTo("r2");
            await Assert.That(CardRows(h.Chat).Select(c => c.Card.RequestId)).IsEquivalentTo(new[] { "r2" });
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pending_cards_sit_at_the_end_of_the_item_list_and_return_after_a_path_switch() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine, AssistantLine]);
            await h.PushAsync(Dto(path));
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1"));
            await WaitUntilAsync(() => CardRows(h.Chat).Length == 1, what: "card in the list");
            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(
                new[] { nameof(UserTurnItem), nameof(AssistantTextItem), nameof(PendingCardItem) }, CollectionOrdering.Matching);
            await Assert.That(CardRows(h.Chat)[0].Card.RequestId).IsEqualTo("r1");

            var other = Tmp.CreateFile("u.jsonl", [UserLine]);
            await h.PushAsync(Dto(other));
            await Assert.That(h.Chat.Items.OfType<PendingCardItem>().Select(c => c.Card.RequestId)).IsEquivalentTo(new[] { "r1" });
            await Assert.That(h.Chat.Items[^1]).IsTypeOf<PendingCardItem>();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_card_packs_against_the_tool_group_it_follows_and_unpacks_when_cleared() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("ask.jsonl", [
                """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}]}}""",
            ]);
            await h.PushAsync(Dto(path));
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1"));
            await WaitUntilAsync(() => CardRows(h.Chat).Length == 1, what: "the card");
            var group = (ToolGroupItem)h.Chat.Items[0];
            await Assert.That(group.PacksWithCard).IsTrue();
            await Assert.That(CardRows(h.Chat)[0].PacksWithPrevious).IsTrue();

            h.Permissions.Remove("r1");
            await WaitUntilAsync(() => CardRows(h.Chat).Length == 0, what: "cleared");
            await Assert.That(group.PacksWithCard).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// The centered card owns a live question, so the group hides the moment the call is read —
    /// before the card exists. Waiting for the card would show the row for the round trip and
    /// then take it away. It comes back when the card retires, because until the transcript
    /// carries the result the row is the only record of the call.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_question_call_hides_its_group_before_the_card_arrives() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("ask.jsonl", [
                """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"AskUserQuestion","input":{"questions":[{"question":"declare this"}]}}]}}""",
            ]);
            await h.PushAsync(Dto(path));
            var group = (ToolGroupItem)h.Chat.Items[0];
            await Assert.That(CardRows(h.Chat)).IsEmpty();
            await Assert.That(group.SuppressedForPendingQuestion).IsTrue();

            h.Permissions.Add(PermissionEntries.Question("q1"));
            await WaitUntilAsync(() => CardRows(h.Chat).Length == 1, what: "the card");
            await Assert.That(group.SuppressedForPendingQuestion).IsTrue();
            await Assert.That(CardRows(h.Chat)[0].PacksWithPrevious).IsFalse();

            h.Permissions.Remove("q1");
            await WaitUntilAsync(() => CardRows(h.Chat).Length == 0, what: "cleared");
            await Assert.That(group.SuppressedForPendingQuestion).IsFalse();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_suppressed_question_group_returns_when_the_session_ends() {
        await RunOnUiAsync(async () => {
            var input = new AvailabilityInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("ask.jsonl", [
                """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"AskUserQuestion","input":{"questions":[{"question":"declare this"}]}}]}}""",
            ]);
            await h.PushAsync(Dto(path));
            var group = (ToolGroupItem)h.Chat.Items[0];
            await Assert.That(group.SuppressedForPendingQuestion).IsTrue();

            input.SetAvailability(SendAvailability.Ended);
            await Assert.That(group.SuppressedForPendingQuestion).IsFalse();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_later_transcript_row_unpacks_the_group_the_card_left() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("ask.jsonl", [
                """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}]}}""",
            ]);
            await h.PushAsync(Dto(path));
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1"));
            await WaitUntilAsync(() => CardRows(h.Chat).Length == 1, what: "the card");
            var group = (ToolGroupItem)h.Chat.Items[0];
            await Assert.That(group.PacksWithCard).IsTrue();

            File.AppendAllText(path, AssistantLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(
                new[] { nameof(ToolGroupItem), nameof(AssistantTextItem), nameof(PendingCardItem) }, CollectionOrdering.Matching);
            await Assert.That(group.PacksWithCard).IsFalse();
            await Assert.That(CardRows(h.Chat)[0].PacksWithPrevious).IsFalse();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_card_after_prose_does_not_pack() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine, AssistantLine]);
            await h.PushAsync(Dto(path));
            h.Permissions.Add(PermissionEntries.Question("q1"));
            await WaitUntilAsync(() => CardRows(h.Chat).Length == 1, what: "the card");
            await Assert.That(h.Chat.Items.OfType<ToolGroupItem>().Any(g => g.PacksWithCard)).IsFalse();
            await Assert.That(CardRows(h.Chat)[0].PacksWithPrevious).IsFalse();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_permission_replayed_before_the_agent_dto_ends_up_with_a_relative_detail() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolName: "Read", toolInputJson: """{"file_path":"/repo/x/src/a.cs"}"""));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the card");
            await Assert.That(((PermissionCardViewModel)h.Chat.PendingCards[0]).Detail).IsEqualTo("/repo/x/src/a.cs");
            await h.PushAsync(Dto(transcriptPath: null));
            await WaitUntilAsync(() => ((PermissionCardViewModel)h.Chat.PendingCards[0]).Detail == "src/a.cs", what: "relative once the root lands");
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Question_entries_become_question_cards_beside_permission_cards() {
        await RunOnUiAsync(async () => {
            var h = Claude(p => {
                p.Add(PermissionEntries.Entry("r1", requestedAt: "2026-08-28T10:00:00.0000000+00:00"));
                p.Add(PermissionEntries.Question("q1", requestedAt: "2026-08-28T10:00:01.0000000+00:00"));
            });
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 2, what: "both cards");
            await Assert.That(h.Chat.PendingCards[0]).IsTypeOf<PermissionCardViewModel>();
            await Assert.That(h.Chat.PendingCards[1]).IsTypeOf<QuestionCardViewModel>();
            await Assert.That(CardRows(h.Chat).Select(c => c.Card.GetType().Name)).IsEquivalentTo(
                new[] { nameof(PermissionCardViewModel), nameof(QuestionCardViewModel) }, CollectionOrdering.Matching);

            h.Permissions.Remove("q1");
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "question card removed");
            await Assert.That(h.Chat.PendingCards[0].RequestId).IsEqualTo("r1");
            await h.TeardownAsync();
        });
    }

    /// The header status reads the card type. Replacing a question with a permission leaves the
    /// collection nonempty, so the word has to move without waiting for the poll.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Resolving_a_question_beside_a_permission_updates_the_header_status() {
        await RunOnUiAsync(async () => {
            var h = Claude(p => p.Add(PermissionEntries.Entry("r1", "a1")));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the permission");
            await Assert.That(h.Chat.AgentStatus.Kind).IsEqualTo(AgentStatusKind.NeedsYou);

            h.Permissions.Add(PermissionEntries.Question("q1"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 2, what: "the question");
            await Assert.That(h.Chat.AgentStatus.Kind).IsEqualTo(AgentStatusKind.Answer);

            h.Permissions.Remove("q1");
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the question resolved");
            await Assert.That(h.Chat.AgentStatus.Kind).IsEqualTo(AgentStatusKind.NeedsYou);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_request_with_an_id_marks_its_row_in_either_order_and_clears_on_resolve() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, ReadCallLine]);
            await h.PushAsync(Dto(path));
            var bash = Group(h.Chat, 0).Calls[0];
            var read = Group(h.Chat, 0).Calls[1];
            await WaitUntilAsync(() => bash.IsAwaitingPermission, what: "the card-first mark");
            await Assert.That(read.IsAwaitingPermission).IsFalse();

            h.Permissions.Remove("r1");
            await WaitUntilAsync(() => !bash.IsAwaitingPermission, what: "cleared on resolve");

            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", toolUseId: "t2"));
            await WaitUntilAsync(() => read.IsAwaitingPermission, what: "the row-first mark");
            await Assert.That(bash.IsAwaitingPermission).IsFalse();

            h.Permissions.Add(PermissionEntries.Entry("r3", "a1", toolUseId: "nope"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 2, what: "the unmatched card");
            await Assert.That(bash.IsAwaitingPermission).IsFalse();
            await Assert.That(read.IsAwaitingPermission).IsTrue();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Two_requests_on_one_row_keep_the_mark_until_both_go_and_a_settled_row_withdraws_the_survivor() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine]);
            await h.PushAsync(Dto(path));
            var bash = Group(h.Chat, 0).Calls[0];
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", toolUseId: "t1"));
            await WaitUntilAsync(() => bash.IsAwaitingPermission, what: "marked");

            h.Permissions.Remove("r1");
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "one card left");
            await Assert.That(bash.IsAwaitingPermission).IsTrue();

            h.Permissions.Queue(PermissionResolveKind.Applied);
            File.AppendAllText(path, ToolResultLine + "\n");
            await h.TickAsync();
            await Assert.That(bash.Outcome).IsEqualTo(ToolOutcome.Done);
            await Assert.That(bash.IsAwaitingPermission).IsFalse();
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 0, what: "the survivor withdrawn");
            await Assert.That(h.Permissions.Withdrawn).IsEquivalentTo(["r2"]);
            await h.TeardownAsync();
        });
    }

    /// The daemon never sees a terminal answer, so a result for a pending request's tool is the
    /// app's cue to retire it — whichever of the two arrives first, since a replayed request can
    /// land after the transcript's initial load. A request for another tool, or without an id,
    /// is left alone.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_result_for_a_pending_requests_tool_withdraws_it_in_either_order() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, ReadCallLine]);
            await h.PushAsync(Dto(path));

            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", toolUseId: "t2"));
            h.Permissions.Add(PermissionEntries.Entry("r3", "a1", vendor: "codex"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 3, what: "three cards");

            h.Permissions.Queue(PermissionResolveKind.Applied);
            File.AppendAllText(path, ToolResultLine + "\n");
            await h.TickAsync();
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 2, what: "the result's request withdrawn");
            await Assert.That(h.Permissions.Withdrawn).IsEquivalentTo(["r1"]);

            // Result already on file when the request arrives: withdrawn on arrival.
            h.Permissions.Queue(PermissionResolveKind.Applied);
            h.Permissions.Add(PermissionEntries.Entry("r4", "a1", toolUseId: "t1"));
            await WaitUntilAsync(() => h.Permissions.Withdrawn.Count == 2, what: "the late request withdrawn");
            await Assert.That(h.Permissions.Withdrawn[1]).IsEqualTo("r4");
            await Assert.That(h.Chat.PendingCards.Count).IsEqualTo(2);
            await h.TeardownAsync();
        });
    }

    /// AskUserQuestion's hook has no tool-use id, so the card is retired from the question text
    /// once that ask has a result. A different question, a plain prompt with no id, and a card
    /// requested after the result stay.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_settled_question_withdraws_matching_cards_that_have_no_tool_id() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("q.jsonl", [QuestionCallLine]);
            await h.PushAsync(Dto(path));

            h.Permissions.Add(PermissionEntries.Question("q-match", toolInputJson: MatchingQuestion));
            h.Permissions.Add(PermissionEntries.Question("q-dup", toolInputJson: MatchingQuestion));
            h.Permissions.Add(PermissionEntries.Question("q-other", toolInputJson: OtherQuestion));
            h.Permissions.Add(PermissionEntries.Question("q-later", toolInputJson: MatchingQuestion, requestedAt: "2026-08-28T12:00:00.0000000+00:00"));
            h.Permissions.Add(PermissionEntries.Entry("r-plain", "a1", vendor: "codex"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 5, what: "five cards");

            h.Permissions.Queue(PermissionResolveKind.Applied);
            h.Permissions.Queue(PermissionResolveKind.Applied);
            File.AppendAllText(path, QuestionResultLine + "\n");
            await h.TickAsync();
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 3, what: "the answered question withdrawn");
            await Assert.That(h.Permissions.Withdrawn).IsEquivalentTo(["q-match", "q-dup"]);

            h.Permissions.Queue(PermissionResolveKind.Applied);
            h.Permissions.Add(PermissionEntries.Question("q-late", toolInputJson: MatchingQuestion));
            await WaitUntilAsync(() => h.Permissions.Withdrawn.Count == 3, what: "the late card withdrawn");
            await Assert.That(h.Permissions.Withdrawn[2]).IsEqualTo("q-late");
            await Assert.That(h.Chat.PendingCards.Count).IsEqualTo(3);
            await h.TeardownAsync();
        });
    }

    /// A second ask of the same question is still open, so a card for it is not retired by the
    /// earlier result. The result of that second ask is what retires it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_open_question_is_not_retired_by_an_earlier_ask_of_the_same_text() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("q.jsonl", [QuestionCallLine, QuestionResultLine, QuestionAgainLine]);
            await h.PushAsync(Dto(path));

            h.Permissions.Add(PermissionEntries.Question("q-live", toolInputJson: MatchingQuestion, requestedAt: "2026-08-28T09:00:00.0000000+00:00"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the open question");
            await Assert.That(h.Permissions.Withdrawn).IsEmpty();

            h.Permissions.Queue(PermissionResolveKind.Applied);
            File.AppendAllText(path, QuestionAgainResultLine + "\n");
            await h.TickAsync();
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 0, what: "the second ask withdrawn");
            await Assert.That(h.Permissions.Withdrawn).IsEquivalentTo(["q-live"]);
            await h.TeardownAsync();
        });
    }

    /// An unparsed requested_at is the minimum timestamp, which is older than every settled ask.
    /// That is not evidence the card predates the result.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_question_whose_time_did_not_parse_is_not_retired_by_a_settled_ask() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("q.jsonl", [QuestionCallLine, QuestionResultLine]);
            await h.PushAsync(Dto(path));

            h.Permissions.Add(PermissionEntries.Question("q-bad", toolInputJson: MatchingQuestion, requestedAt: "not-a-timestamp"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the untimed question");
            await Assert.That(h.Permissions.Withdrawn).IsEmpty();
            await h.TeardownAsync();
        });
    }

    /// A withdraw the daemon could not be reached for is not final: it retries on its own after a
    /// backoff, with no other event needed, and a withdraw in flight is never sent twice.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_withdraw_retries_on_its_own_after_a_backoff_and_is_never_doubled() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, ToolResultLine]);
            await h.PushAsync(Dto(path));

            var gate = h.Permissions.Arm();
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            await WaitUntilAsync(() => h.Permissions.Withdrawn.Count == 1, what: "the first withdraw");
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", toolUseId: "t9"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 2, what: "a reconcile while in flight");
            await Assert.That(h.Permissions.Withdrawn.Count).IsEqualTo(1);

            gate.SetResult(new PermissionResolveOutcome(PermissionResolveKind.TransportFailure, "daemon_unreachable"));
            await WaitUntilAsync(() => h.Chat.WithdrawsInFlightForTesting == 0, what: "the failure reopened it");
            h.Permissions.Queue(PermissionResolveKind.Applied);
            h.Time.Advance(ChatTabViewModel.WithdrawRetryDelay);
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the retry withdrew it");
            await Assert.That(h.Permissions.Withdrawn).IsEquivalentTo(["r1", "r1"]);
            await h.TeardownAsync();
        });
    }

    /// The backoff doubles and stops at the cap; after that only a fresh reconcile retries.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Withdraw_retries_are_capped_and_a_later_reconcile_tries_again() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, ToolResultLine]);
            await h.PushAsync(Dto(path));

            h.Permissions.Queue(PermissionResolveKind.TransportFailure, "daemon_unreachable");
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            await WaitUntilAsync(() => h.Chat.WithdrawsInFlightForTesting == 0, what: "the first failure");
            for (var retry = 1; retry <= ChatTabViewModel.MaxWithdrawRetries; retry++) {
                h.Permissions.Queue(PermissionResolveKind.TransportFailure, "daemon_unreachable");
                h.Time.Advance(ChatTabViewModel.WithdrawRetryDelay * (1 << (retry - 1)) - TimeSpan.FromMilliseconds(1));
                await Task.Delay(20);
                await Assert.That(h.Permissions.Withdrawn.Count).IsEqualTo(retry);
                h.Time.Advance(TimeSpan.FromMilliseconds(1));
                await WaitUntilAsync(() => h.Permissions.Withdrawn.Count == retry + 1, what: $"retry {retry}");
                await WaitUntilAsync(() => h.Chat.WithdrawsInFlightForTesting == 0, what: $"retry {retry} failed");
            }

            h.Time.Advance(TimeSpan.FromMinutes(5));
            await Task.Delay(20);
            await Assert.That(h.Permissions.Withdrawn.Count).IsEqualTo(ChatTabViewModel.MaxWithdrawRetries + 1);

            h.Permissions.Queue(PermissionResolveKind.Applied);
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", toolUseId: "t9"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the reconcile's retry withdrew it");
            await Assert.That(h.Permissions.Withdrawn.Count).IsEqualTo(ChatTabViewModel.MaxWithdrawRetries + 2);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_request_without_an_id_marks_the_sole_running_call_and_abstains_on_two() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", vendor: "codex"));
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine]);
            await h.PushAsync(Dto(path));
            var first = Group(h.Chat, 0).Calls[0];
            await WaitUntilAsync(() => first.IsAwaitingPermission, what: "the sole running call, row after card");

            File.AppendAllText(path, ReadCallLine + "\n");
            await h.TickAsync();
            var second = Group(h.Chat, 0).Calls[1];
            await Assert.That(first.IsAwaitingPermission).IsFalse();
            await Assert.That(second.IsAwaitingPermission).IsFalse();

            File.AppendAllText(path, ToolResultLine + "\n");
            await h.TickAsync();
            await Assert.That(first.IsAwaitingPermission).IsFalse();
            await Assert.That(second.IsAwaitingPermission).IsTrue();

            h.Permissions.Remove("r1");
            await WaitUntilAsync(() => !second.IsAwaitingPermission, what: "cleared on resolve");

            File.AppendAllText(path, ToolErrorLine.Replace("t1", "t2") + "\n");
            await h.TickAsync();
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", vendor: "codex"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "a card with nothing running");
            await Assert.That(second.IsAwaitingPermission).IsFalse();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pending_request_marks_the_rebuilt_row_after_a_reset_and_a_path_switch() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine, ToolCallLine]);
            await h.PushAsync(Dto(path));
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", vendor: "codex"));
            await WaitUntilAsync(() => Group(h.Chat, 1).Calls[0].IsAwaitingPermission, what: "marked before the reset");

            File.WriteAllLines(path, [ToolCallLine]);
            await h.TickAsync();
            await Assert.That(Group(h.Chat, 0).Calls[0].IsAwaitingPermission).IsTrue();

            var other = Tmp.CreateFile("o.jsonl", [ToolCallLine.Replace("t1", "t9")]);
            await h.PushAsync(Dto(other));
            await Assert.That(Group(h.Chat, 0).Calls[0].IsAwaitingPermission).IsTrue();

            h.Permissions.Remove("r2");
            await WaitUntilAsync(() => !Group(h.Chat, 0).Calls[0].IsAwaitingPermission, what: "only the id-less request fitted t9");
            await h.TeardownAsync();
        });
    }

    sealed class AvailabilityInput : ChatInput {
        SendAvailability _availability = SendAvailability.Ready;

        public void SetAvailability(SendAvailability availability) {
            _availability = availability;
            this.RaisePropertyChanged(nameof(Availability));
            this.RaisePropertyChanged(nameof(CanAcceptText));
        }

        public override SendAvailability Availability => _availability;
        public override bool CanAcceptText => _availability == SendAvailability.Ready;
        public override string Hint => "";
        public override bool CanAttach => false;
        public override string? AttachHint => null;
        public override Task<ChatSendOutcome> SendAsync(string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) =>
            Task.FromResult(ChatSendOutcome.Accepted);
        public override void Dispose() { }
    }
}
