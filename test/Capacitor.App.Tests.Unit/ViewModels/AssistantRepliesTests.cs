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

    [Test]
    public async Task A_reply_runs_to_the_next_prompt_and_copies_only_its_messages() {
        var items = Feed();
        var first = new AssistantTextItem("First.");
        var second = new AssistantTextItem("Second.");
        var answer = new AssistantTextItem("Answer.");
        items.AddRange([
            new UserTurnItem("go"), first, new SystemNoteItem("Agent finished"), new AssistantThinkingItem("hmm"), second,
            new UserTurnItem("and?"), answer,
        ]);

        await Assert.That(new[] { first.EndsReply, second.EndsReply, answer.EndsReply }).IsEquivalentTo([false, true, true], CollectionOrdering.Matching);
        await Assert.That(second.ReplyText).IsEqualTo("First.\n\nSecond.");
        await Assert.That(answer.ReplyText).IsEqualTo("Answer.");
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

        await Assert.That(first.EndsReply).IsTrue();
        await Assert.That(first.ReplyText).IsEqualTo("First.");
        await Assert.That(answer.EndsReply).IsFalse();
        await Assert.That(resumed.ReplyText).IsEqualTo("The task finished.\n\nPicking up.");
    }

    [Test]
    public async Task A_streamed_message_takes_over_the_end_of_its_reply_and_gives_it_back_when_removed() {
        var items = Feed();
        var first = new AssistantTextItem("First.");
        items.AddRange([new UserTurnItem("go"), first]);
        var next = new AssistantTextItem("Next.");

        items.Add(next);
        await Assert.That(first.EndsReply).IsFalse();
        await Assert.That(next.ReplyText).IsEqualTo("First.\n\nNext.");

        items.Remove(next);
        await Assert.That(first.EndsReply).IsTrue();
        await Assert.That(first.ReplyText).IsEqualTo("First.");
    }

    [Test]
    public async Task A_prompt_inserted_inside_a_reply_splits_it() {
        var items = Feed();
        var first = new AssistantTextItem("First.");
        var second = new AssistantTextItem("Second.");
        items.AddRange([new UserTurnItem("go"), first, second]);

        items.Insert(2, new UserTurnItem("wait"));

        await Assert.That(first.EndsReply).IsTrue();
        await Assert.That(first.ReplyText).IsEqualTo("First.");
        await Assert.That(second.ReplyText).IsEqualTo("Second.");
    }

    /// Under the session constraint because topic commands are ReactiveCommands.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Folding_a_topic_inside_a_reply_keeps_what_the_reply_copies() {
        await RunOnUiAsync(async () => {
            var items = Feed();
            var first = new AssistantTextItem("First.");
            var second = new AssistantTextItem("Second.");
            items.AddRange([new UserTurnItem("go"), first, second]);
            var topics = new ChatTopics(items);
            topics.ToggleChange(second);
            var topic = items.OfType<ChatTopicItem>().Single();

            topic.ToggleCommand.Execute().Subscribe();
            await Assert.That(topic.IsExpanded).IsFalse();
            await Assert.That(items).DoesNotContain(second);
            await Assert.That(first.EndsReply).IsFalse();
            await Assert.That(second.ReplyText).IsEqualTo("First.\n\nSecond.");

            topic.ToggleCommand.Execute().Subscribe();
            await Assert.That(first.EndsReply).IsFalse();
            await Assert.That(second.ReplyText).IsEqualTo("First.\n\nSecond.");
        });
    }

    [Test]
    public async Task Blank_messages_add_nothing_to_the_copied_reply() {
        var items = Feed();
        var last = new AssistantTextItem("Done.");
        items.AddRange([new AssistantTextItem("Start."), new AssistantTextItem("  "), last]);

        await Assert.That(last.ReplyText).IsEqualTo("Start.\n\nDone.");
    }
}
