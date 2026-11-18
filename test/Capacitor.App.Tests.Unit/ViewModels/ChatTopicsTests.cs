using Avalonia.Collections;
using Capacitor.App.ViewModels;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit.ViewModels;

/// Topics split the chat at marked messages and fold each stretch into its header. Under the session
/// constraint because the commands are ReactiveCommands.
public class ChatTopicsTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_marked_message_starts_a_topic_that_runs_to_the_next_mark() {
        await RunOnUiAsync(async () => {
            var review = new AssistantTextItem("Five points");
            var one    = new UserTurnItem("Fix point 1\nthe lock order");
            var done   = new AssistantTextItem("Done");
            var two    = new UserTurnItem("Point 2");
            var items  = new AvaloniaList<ChatItemViewModel> { review, one, done, two };
            var topics = new ChatTopics(items);

            topics.ToggleChangeCommand.Execute(one).Subscribe();
            topics.ToggleChange(two);

            var first  = (ChatTopicItem)items[1];
            var second = (ChatTopicItem)items[4];
            await Assert.That(first.Label).IsEqualTo("Fix point 1");
            await Assert.That(one.StartsTopic).IsTrue();
            await Assert.That(done.StartsTopic).IsFalse();
            await Assert.That(review.Topic).IsNull();
            await Assert.That(done.Topic).IsSameReferenceAs(first);
            await Assert.That(two.Topic).IsSameReferenceAs(second);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Folding_a_topic_stops_at_the_next_one_and_unfolding_restores_it() {
        await RunOnUiAsync(async () => {
            var one   = new UserTurnItem("Point 1");
            var done  = new AssistantTextItem("Done");
            var two   = new UserTurnItem("Point 2");
            var items = new AvaloniaList<ChatItemViewModel> { one, done, two };
            var topics = new ChatTopics(items);
            topics.ToggleChange(one);
            topics.ToggleChange(two);
            var first  = (ChatTopicItem)items[0];
            var second = (ChatTopicItem)items[3];

            first.ToggleCommand.Execute().Subscribe();
            await Assert.That(items).IsEquivalentTo(new ChatItemViewModel[] { first, second, two }, CollectionOrdering.Matching);
            await Assert.That(first.Summary).IsEqualTo("2 messages");
            await Assert.That(topics.Everything()).IsEquivalentTo(new ChatItemViewModel[] { first, one, done, second, two }, CollectionOrdering.Matching);

            first.ToggleCommand.Execute().Subscribe();
            await Assert.That(items).IsEquivalentTo(new ChatItemViewModel[] { first, one, done, second, two }, CollectionOrdering.Matching);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Unmarking_a_message_joins_its_topic_to_the_one_before() {
        await RunOnUiAsync(async () => {
            var one   = new UserTurnItem("Point 1");
            var two   = new UserTurnItem("Point 2");
            var items = new AvaloniaList<ChatItemViewModel> { one, two };
            var topics = new ChatTopics(items);
            topics.ToggleChange(one);
            topics.ToggleChange(two);
            var first = (ChatTopicItem)items[0];
            first.ToggleCommand.Execute().Subscribe();

            topics.ToggleChange(two);
            await Assert.That(items).IsEquivalentTo(new ChatItemViewModel[] { first, one, two }, CollectionOrdering.Matching);
            await Assert.That(first.IsExpanded).IsTrue();
            await Assert.That(two.StartsTopic).IsFalse();
            await Assert.That(two.Topic).IsSameReferenceAs(first);

            topics.ToggleChange(one);
            await Assert.That(items).IsEquivalentTo(new ChatItemViewModel[] { one, two }, CollectionOrdering.Matching);
            await Assert.That(two.Topic).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_topic_started_for_a_send_sits_above_pending_cards_and_waits_for_its_message() {
        await RunOnUiAsync(async () => {
            var card   = new PendingCardItem(null!);
            var items  = new AvaloniaList<ChatItemViewModel> { new AssistantTextItem("Before"), card };
            var topics = new ChatTopics(items);

            var topic = topics.Start();
            await Assert.That(topics.Start()).IsSameReferenceAs(topic);
            await Assert.That(items[1]).IsSameReferenceAs(topic);
            await Assert.That(topic.Label).IsEqualTo("Topic 1");

            topics.Discard(topic);
            await Assert.That(items.Contains(topic)).IsFalse();

            topic = topics.Start();
            var ask = new UserTurnItem("Point 2");
            items.Insert(2, ask);
            topics.Discard(topic);
            await Assert.That(items[1]).IsSameReferenceAs(topic);
            await Assert.That(topic.Label).IsEqualTo("Point 2");
            await Assert.That(ask.StartsTopic).IsTrue();
            await Assert.That(card.Topic).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_topic_without_a_user_message_is_named_by_the_agent() {
        await RunOnUiAsync(async () => {
            var reply  = new AssistantTextItem("\nLooking at the retry policy");
            var items  = new AvaloniaList<ChatItemViewModel> { reply };
            var topics = new ChatTopics(items);

            topics.ToggleChange(reply);
            var topic = (ChatTopicItem)items[0];
            await Assert.That(topic.Label).IsEqualTo("Looking at the retry policy");

            topic.Rename("Retries");
            await Assert.That(topic.Label).IsEqualTo("Retries");
            topic.Rename(" ");
            await Assert.That(topic.Label).IsEqualTo("Looking at the retry policy");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Topics_come_back_when_the_transcript_replays_into_a_new_tab() {
        await RunOnUiAsync(async () => {
            var store = new ChatMarks();
            var items = new AvaloniaList<ChatItemViewModel>(Transcript());
            var first = new ChatTopics(items, store);
            first.Reset("feed");
            var (one, two) = (items[1], items[4]);
            first.ToggleChange(one);
            first.ToggleChange(two);
            ((ChatTopicItem)items[1]).ToggleCommand.Execute().Subscribe();
            ((ChatTopicItem)items[1]).Rename("  Jitter  ");

            var replayed = new AvaloniaList<ChatItemViewModel>();
            var second = new ChatTopics(replayed, store);
            second.Reset("feed");
            var rows = Transcript();
            replayed.AddRange(rows.Take(3));
            second.Restore();
            await Assert.That(replayed[1]).IsTypeOf<ChatTopicItem>();
            await Assert.That(((ChatTopicItem)replayed[1]).IsExpanded).IsTrue();

            replayed.AddRange(rows.Skip(3));
            second.Restore();
            var folded = (ChatTopicItem)replayed[1];
            await Assert.That(folded.IsExpanded).IsFalse();
            await Assert.That(folded.Label).IsEqualTo("Jitter");
            await Assert.That(folded.Hidden).IsEquivalentTo(rows.Skip(1).Take(3), CollectionOrdering.Matching);
            await Assert.That(replayed[2]).IsTypeOf<ChatTopicItem>();
            await Assert.That(replayed.Skip(3)).IsEquivalentTo(rows.Skip(4), CollectionOrdering.Matching);
            await Assert.That(rows[4].StartsTopic).IsTrue();
        });

        static List<ChatItemViewModel> Transcript() => [
            new AssistantTextItem("Five points"), new UserTurnItem("Point 1"), new AssistantTextItem("Done"),
            new AssistantThinkingItem("Hmm"), new UserTurnItem("Point 2"), new AssistantTextItem("Done too"),
        ];
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_topic_list_follows_the_marks_and_holds_a_reveal_for_the_view() {
        await RunOnUiAsync(async () => {
            var one    = new UserTurnItem("Point 1");
            var two    = new UserTurnItem("Point 2");
            var items  = new AvaloniaList<ChatItemViewModel> { one, two };
            var topics = new ChatTopics(items);
            var raised = 0;
            topics.Revealing += () => raised++;

            topics.ToggleChange(two);
            topics.ToggleChange(one);
            var headers = items.OfType<ChatTopicItem>().ToList();
            await Assert.That(topics.All).IsEquivalentTo(headers, CollectionOrdering.Matching);
            await Assert.That(topics.Any).IsTrue();

            topics.RevealCommand.Execute(headers[1]).Subscribe();
            await Assert.That(raised).IsEqualTo(1);
            await Assert.That(topics.TakeReveal()?.Row).IsSameReferenceAs(headers[1]);
            await Assert.That(topics.TakeReveal()).IsNull();

            topics.ToggleChange(two);
            topics.ToggleChange(one);
            await Assert.That(topics.All).IsEmpty();
            await Assert.That(topics.Any).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Removing_a_folded_topic_joins_its_rows_to_the_topic_before() {
        await RunOnUiAsync(async () => {
            var one   = new UserTurnItem("Point 1");
            var done  = new AssistantTextItem("Done");
            var two   = new UserTurnItem("Point 2");
            var items = new AvaloniaList<ChatItemViewModel> { one, done, two };
            var topics = new ChatTopics(items);
            topics.ToggleChange(one);
            topics.ToggleChange(two);
            var first  = (ChatTopicItem)items[0];
            var second = (ChatTopicItem)items[3];
            first.ToggleCommand.Execute().Subscribe();
            second.ToggleCommand.Execute().Subscribe();

            topics.RemoveCommand.Execute(second).Subscribe();
            await Assert.That(items).IsEquivalentTo(new ChatItemViewModel[] { first, one, done, two }, CollectionOrdering.Matching);
            await Assert.That(first.IsExpanded).IsTrue();
            await Assert.That(two.Topic).IsSameReferenceAs(first);
            await Assert.That(topics.All).IsEquivalentTo(new[] { first }, CollectionOrdering.Matching);
        });
    }

    /// An end closes the topic before the next message: folding takes it in and stops there, the rows
    /// after it sit in no topic, it comes back with the transcript, and it goes when the topic it ended does.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_end_closes_its_topic_and_goes_with_it() {
        await RunOnUiAsync(async () => {
            var store = new ChatMarks();
            var one   = new UserTurnItem("Point 1");
            var done  = new AssistantTextItem("Done");
            var items = new AvaloniaList<ChatItemViewModel> { one, done };
            var topics = new ChatTopics(items, store);
            topics.Reset("feed");
            await Assert.That(topics.End()).IsNull();

            topics.ToggleChange(one);
            await Assert.That(topics.CanEnd).IsTrue();
            var end = topics.End()!;
            var aside = new UserTurnItem("Unrelated");
            items.Add(aside);
            var topic = (ChatTopicItem)items[0];
            await Assert.That(topics.CanEnd).IsFalse();
            await Assert.That(done.Topic).IsSameReferenceAs(topic);
            await Assert.That(end.Topic).IsSameReferenceAs(topic);
            await Assert.That(aside.Topic).IsNull();

            topic.ToggleCommand.Execute().Subscribe();
            await Assert.That(items).IsEquivalentTo(new ChatItemViewModel[] { topic, aside }, CollectionOrdering.Matching);
            await Assert.That(topic.Hidden[^1]).IsSameReferenceAs(end);
            await Assert.That(aside.Topic).IsNull();
            await Assert.That(topics.CanEnd).IsFalse();

            var replayed = new AvaloniaList<ChatItemViewModel>();
            var again = new ChatTopics(replayed, store);
            again.Reset("feed");
            replayed.AddRange([new UserTurnItem("Point 1"), new AssistantTextItem("Done"), new UserTurnItem("Unrelated")]);
            again.Restore();
            await Assert.That(replayed.Select(static row => row.GetType().Name)).IsEquivalentTo(
                ["ChatTopicItem", "UserTurnItem"], CollectionOrdering.Matching);
            await Assert.That(((ChatTopicItem)replayed[0]).Hidden[^1]).IsTypeOf<ChatTopicEndItem>();

            topics.Remove(topic);
            await Assert.That(items).IsEquivalentTo(new ChatItemViewModel[] { one, done, aside }, CollectionOrdering.Matching);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Removing_an_end_runs_its_topic_on_and_a_new_topic_replaces_a_waiting_end() {
        await RunOnUiAsync(async () => {
            var one   = new UserTurnItem("Point 1");
            var items = new AvaloniaList<ChatItemViewModel> { one };
            var topics = new ChatTopics(items);
            topics.ToggleChange(one);
            var topic = (ChatTopicItem)items[0];

            var end = topics.End()!;
            var started = topics.Start();
            await Assert.That(items).IsEquivalentTo(new ChatItemViewModel[] { topic, one, started }, CollectionOrdering.Matching);
            topics.Discard(started);

            end = topics.End()!;
            var aside = new UserTurnItem("Aside");
            items.Add(aside);
            topics.RemoveEndCommand.Execute(end).Subscribe();
            await Assert.That(items).IsEquivalentTo(new ChatItemViewModel[] { topic, one, aside }, CollectionOrdering.Matching);
            await Assert.That(aside.Topic).IsSameReferenceAs(topic);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_topic_gone_to_is_current_until_a_scroll_then_the_one_at_the_top() {
        await RunOnUiAsync(async () => {
            var intro = new AssistantTextItem("Intro");
            var one   = new UserTurnItem("Point 1");
            var done  = new AssistantTextItem("Done");
            var items = new AvaloniaList<ChatItemViewModel> { intro, one, done };
            var topics = new ChatTopics(items);
            topics.ToggleChange(one);
            var topic = (ChatTopicItem)items[1];

            topics.ShowingAtTop(done);
            await Assert.That(topic.IsCurrent).IsTrue();
            await Assert.That(topic.ToggleTip).IsEqualTo("Collapse");
            topics.ShowingAtTop(intro);
            await Assert.That(topic.IsCurrent).IsFalse();
            topics.ShowingAtTop(topic);
            await Assert.That(topic.IsCurrent).IsTrue();

            topics.RevealCommand.Execute(topic).Subscribe();
            topics.ShowingAtTop(intro);
            await Assert.That(topic.IsCurrent).IsTrue();
            topics.ReleaseCurrent();
            topics.ShowingAtTop(intro);
            await Assert.That(topic.IsCurrent).IsFalse();
        });
    }
}
