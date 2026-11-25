using Avalonia.Collections;
using Capacitor.App.ViewModels;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit.ViewModels;

public class AssistantRepliesTests {
    static AvaloniaList<ChatItemViewModel> Feed() {
        var items = new AvaloniaList<ChatItemViewModel>();
        items.CollectionChanged += (_, e) => AssistantReplies.Refresh(items, e);
        return items;
    }

    static ToolGroupItem Group() => new();

    static ChatItemViewModel[] Ends(IEnumerable<ChatItemViewModel> items) => items.Where(static item => item.EndsReply).ToArray();

    [Test]
    public async Task A_reply_runs_to_the_next_prompt_ends_at_its_last_row_and_copies_only_its_messages() {
        var items = Feed();
        var first = new AssistantTextItem("First.", new ChatTimestamp("10:00", "first"));
        var group = Group();
        var second = new AssistantTextItem("Second.", new ChatTimestamp("10:01", "second"));
        var tail = Group();
        var answer = new AssistantTextItem("Answer.");
        items.AddRange([
            new UserTurnItem("go"), first, new SystemNoteItem("Agent finished"), group, new AssistantThinkingItem("hmm"), second, tail,
            new UserTurnItem("and?"), answer,
        ]);

        await Assert.That(Ends(items)).IsEquivalentTo(new ChatItemViewModel[] { tail, answer }, CollectionOrdering.Matching);
        await Assert.That(tail.Reply!.Text).IsEqualTo("First.\n\nSecond.");
        await Assert.That(tail.Reply.Time!.Tip).IsEqualTo("second");
        await Assert.That(first.Reply).IsSameReferenceAs(tail.Reply);
        await Assert.That(items[0].Reply).IsNull();
        await Assert.That(answer.Reply!.Text).IsEqualTo("Answer.");
    }

    [Test]
    public async Task A_turn_of_tool_calls_alone_has_nothing_to_copy() {
        var items = Feed();
        var group = Group();
        items.AddRange([new UserTurnItem("go"), group]);

        await Assert.That(group.Reply).IsNull();
        await Assert.That(group.EndsReply).IsFalse();
    }

    [Test]
    public async Task A_note_the_agent_answers_on_its_own_starts_a_new_reply_and_a_compaction_does_not() {
        var items = Feed();
        var first = new AssistantTextItem("First.");
        var answer = new AssistantTextItem("The task finished.");
        var resumed = new AssistantTextItem("Picking up.");
        items.AddRange([
            new UserTurnItem("go"), first,
            new SystemNoteItem("done", SystemNoteItem.TaskNotificationLabel) { StartsTurn = true }, answer,
            new SystemNoteItem("summary", SystemNoteItem.CompactSummaryLabel), resumed,
        ]);

        await Assert.That(Ends(items)).IsEquivalentTo(new ChatItemViewModel[] { first, resumed }, CollectionOrdering.Matching);
        await Assert.That(first.Reply!.Text).IsEqualTo("First.");
        await Assert.That(resumed.Reply!.Text).IsEqualTo("The task finished.\n\nPicking up.");
    }

    [Test]
    public async Task Streamed_rows_take_over_the_end_of_their_reply_and_give_it_back_when_removed() {
        var items = Feed();
        var first = new AssistantTextItem("First.");
        items.AddRange([new UserTurnItem("go"), first]);
        var group = Group();
        var next = new AssistantTextItem("Next.");

        items.Add(group);
        await Assert.That(Ends(items)).IsEquivalentTo(new ChatItemViewModel[] { group });
        items.Add(next);
        await Assert.That(Ends(items)).IsEquivalentTo(new ChatItemViewModel[] { next });
        await Assert.That(next.Reply!.Text).IsEqualTo("First.\n\nNext.");

        items.Remove(next);
        await Assert.That(Ends(items)).IsEquivalentTo(new ChatItemViewModel[] { group });
        await Assert.That(group.Reply!.Text).IsEqualTo("First.");
    }

    [Test]
    public async Task A_prompt_inserted_inside_a_reply_splits_it() {
        var items = Feed();
        var first = new AssistantTextItem("First.");
        var second = new AssistantTextItem("Second.");
        items.AddRange([new UserTurnItem("go"), first, second]);

        items.Insert(2, new UserTurnItem("wait"));

        await Assert.That(Ends(items)).IsEquivalentTo(new ChatItemViewModel[] { first, second }, CollectionOrdering.Matching);
        await Assert.That(first.Reply).IsNotSameReferenceAs(second.Reply);
        await Assert.That(first.Reply!.Text).IsEqualTo("First.");
    }

    /// Under the session constraint because topic commands are ReactiveCommands.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Folding_a_topic_inside_a_reply_keeps_what_it_copies_and_ends_it_at_its_last_row_shown() {
        await RunOnUiAsync(async () => {
            var items = Feed();
            var first = new AssistantTextItem("First.");
            var second = new AssistantTextItem("Second.");
            items.AddRange([new UserTurnItem("go"), first, second]);
            var topics = new ChatTopics(items);
            topics.ToggleChange(second);
            var topic = items.OfType<ChatTopicItem>().Single();

            topic.ToggleCommand.Execute().Subscribe();
            await Assert.That(items).DoesNotContain(second);
            await Assert.That(Ends(items)).IsEquivalentTo(new ChatItemViewModel[] { first });
            await Assert.That(first.Reply!.Text).IsEqualTo("First.\n\nSecond.");

            topic.ToggleCommand.Execute().Subscribe();
            await Assert.That(Ends(items)).IsEquivalentTo(new ChatItemViewModel[] { second });
            await Assert.That(second.Reply!.Text).IsEqualTo("First.\n\nSecond.");
        });
    }

    [Test]
    public async Task Blank_messages_add_nothing_to_the_copied_reply() {
        var items = Feed();
        var last = new AssistantTextItem("Done.");
        items.AddRange([new AssistantTextItem("Start."), new AssistantTextItem("  "), last]);

        await Assert.That(last.Reply!.Text).IsEqualTo("Start.\n\nDone.");
    }
}
