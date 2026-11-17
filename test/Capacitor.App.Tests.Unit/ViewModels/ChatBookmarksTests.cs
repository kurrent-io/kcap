using Avalonia.Collections;
using Capacitor.App.ViewModels;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit.ViewModels;

/// Bookmarks are text picked out of the chat, kept per transcript in the user's order and colour,
/// each leading back to the message it came from. Under the session constraint because the commands
/// are ReactiveCommands.
public class ChatBookmarksTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Bookmarks_keep_their_order_and_colour_in_a_new_tab_of_the_transcript() {
        await RunOnUiAsync(async () => {
            var store = new ChatMarks();
            var reply = new AssistantTextItem("Three points");
            var items = new AvaloniaList<ChatItemViewModel> { new UserTurnItem("Review this"), reply };
            var first = new ChatBookmarks(new ChatTopics(items), store);
            first.Reset("feed");

            first.Add(["Lock   order", " ", "Retry\njitter", "Logging"], reply);
            await Assert.That(first.All.Select(b => b.Text)).IsEquivalentTo(["Lock order", "Retry jitter", "Logging"]);
            await Assert.That(first.All.Select(b => b.Message)).IsEquivalentTo(new int?[] { 1, 1, 1 });
            first.Move(2, 0);
            first.All[1].Color = ChatBookmarkColor.Pink;
            first.RemoveCommand.Execute(first.All[2]).Subscribe();

            var second = new ChatBookmarks(new ChatTopics([]), store);
            await Assert.That(second.Any).IsFalse();
            second.Reset("feed");
            await Assert.That(second.Any).IsTrue();
            await Assert.That(second.All.Select(b => b.Text)).IsEquivalentTo(["Logging", "Lock order"]);
            await Assert.That(second.All[1].Color).IsEqualTo(ChatBookmarkColor.Pink);

            second.Reset("other");
            await Assert.That(second.All).IsEmpty();
            await Assert.That(second.Any).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Revealing_a_bookmark_in_a_folded_topic_unfolds_it_and_shows_its_message() {
        await RunOnUiAsync(async () => {
            var ask   = new UserTurnItem("Point 1");
            var reply = new AssistantTextItem("Done");
            var next  = new UserTurnItem("Point 2");
            var items = new AvaloniaList<ChatItemViewModel> { ask, reply, next };
            var topics = new ChatTopics(items);
            var bookmarks = new ChatBookmarks(topics);
            bookmarks.Add(["Done"], reply);
            topics.ToggleChange(ask);
            topics.ToggleChange(next);
            var folded = (ChatTopicItem)items[0];
            folded.ToggleCommand.Execute().Subscribe();
            await Assert.That(items.Contains(reply)).IsFalse();
            var raised = 0;
            topics.Revealing += () => raised++;

            bookmarks.RevealCommand.Execute(bookmarks.All.Single()).Subscribe();
            await Assert.That(folded.IsExpanded).IsTrue();
            await Assert.That(raised).IsEqualTo(1);
            await Assert.That(topics.TakeReveal()).IsSameReferenceAs(reply);
        });
    }
}
