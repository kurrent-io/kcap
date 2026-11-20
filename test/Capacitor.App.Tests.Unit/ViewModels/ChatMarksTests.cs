using System.Text.Json;
using Avalonia.Collections;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit.ViewModels;

/// Topics and bookmarks are saved per transcript and come back after a restart or as older pages load,
/// each found by its message's place in the feed and checked against the start of that message.
public class ChatMarksTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static List<ChatItemViewModel> Transcript(string edit = "") => [
        new AssistantTextItem("Five points" + edit) { Key = new(10, 0) }, new UserTurnItem("Point 1" + edit) { Key = new(11, 0) },
        new AssistantTextItem("Done" + edit) { Key = new(12, 0) }, new UserTurnItem("Point 2" + edit) { Key = new(12, 1) },
    ];

    async Task<string> MarkAsync(ChatMarksFiles files) {
        var rows = Transcript();
        var items = new AvaloniaList<ChatItemViewModel>(rows);
        var store = new ChatMarks(files);
        var topics = new ChatTopics(items, store);
        var bookmarks = new ChatBookmarks(topics, store);
        topics.Reset("feed");
        bookmarks.Reset("feed");
        topics.ToggleChange(rows[1]);
        ((ChatTopicItem)items[1]).Rename("Locks");
        bookmarks.Add(["Retry\njitter"], rows[3]);
        bookmarks.ToggleMessageCommand.Execute(rows[2]).Subscribe();
        bookmarks.All[0].Color = ChatBookmarkColor.Pink;
        await files.FlushAsync();
        return Directory.GetFiles(Tmp.PathTo("marks")).Single();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Marks_are_saved_per_transcript_and_come_back_in_a_new_app() {
        await RunOnUiAsync(async () => {
            var path = await MarkAsync(new ChatMarksFiles(Tmp.PathTo("marks")));
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var topicJson = saved.RootElement.GetProperty("topics")[0];
            await Assert.That(topicJson.EnumerateObject().Select(static p => p.Name)).IsEquivalentTo(["message", "anchor", "name"], CollectionOrdering.Matching);
            await Assert.That(topicJson.GetProperty("message").GetString()).IsEqualTo("11.0");
            await Assert.That(topicJson.GetProperty("anchor").GetString()).IsEqualTo("Point 1");
            await Assert.That(saved.RootElement.GetProperty("bookmarks")[0].GetProperty("color").GetString()).IsEqualTo("pink");

            var files = new ChatMarksFiles(Tmp.PathTo("marks"));
            var store = new ChatMarks(files);
            var items = new AvaloniaList<ChatItemViewModel>();
            var topics = new ChatTopics(items, store);
            var bookmarks = new ChatBookmarks(topics, store);
            topics.Reset("feed");
            bookmarks.Reset("feed");
            items.AddRange(Transcript());
            topics.Restore();

            var topic = (ChatTopicItem)items[1];
            await Assert.That(topic.Label).IsEqualTo("Locks");
            await Assert.That(bookmarks.All.Select(static b => (b.Text, b.Quote, b.Color))).IsEquivalentTo(
                [("Retry jitter", "Retry\njitter", ChatBookmarkColor.Pink), ("Done", (string?)null, ChatBookmarkColor.Blue)], CollectionOrdering.Matching);
            await Assert.That(items[3].Bookmark).IsSameReferenceAs(bookmarks.All[1]);
            await Assert.That(bookmarks.Sections.Single().Topic).IsSameReferenceAs(topic);

            bookmarks.All.Clear();
            topics.RemoveCommand.Execute(topic).Subscribe();
            await files.FlushAsync();
            await Assert.That(File.Exists(path)).IsFalse();
        });
    }

    /// A mark whose key now names a different message is neither put on it nor lost from the file.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_mark_whose_message_changed_is_not_put_on_another_message() {
        await RunOnUiAsync(async () => {
            await MarkAsync(new ChatMarksFiles(Tmp.PathTo("marks")));

            var store = new ChatMarks(new ChatMarksFiles(Tmp.PathTo("marks")));
            var items = new AvaloniaList<ChatItemViewModel>();
            var topics = new ChatTopics(items, store);
            var bookmarks = new ChatBookmarks(topics, store);
            topics.Reset("feed");
            bookmarks.Reset("feed");
            items.AddRange(Transcript(" rewritten"));
            topics.Restore();

            await Assert.That(items.OfType<ChatTopicItem>()).IsEmpty();
            await Assert.That(items.Any(static row => row.Bookmark is not null)).IsFalse();
            await Assert.That(bookmarks.Sections.Single().Topic).IsNull();
            await Assert.That(bookmarks.Sections.Single().Bookmarks).Count().IsEqualTo(2);
            await Assert.That(store.Topics("feed").Values.Single().Name).IsEqualTo("Locks");
        });
    }

    /// A remote chat opens on its latest page: marks on older messages wait for theirs, a bookmark
    /// from one sits ahead of the messages shown, and both take their place once the page loads above.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Marks_on_older_messages_take_their_place_when_the_older_page_loads() {
        await RunOnUiAsync(async () => {
            var store = new ChatMarks();
            var rows = Transcript();
            var items = new AvaloniaList<ChatItemViewModel>(rows);
            var topics = new ChatTopics(items, store);
            var bookmarks = new ChatBookmarks(topics, store);
            topics.Reset("feed");
            bookmarks.Reset("feed");
            topics.ToggleChange(rows[1]);
            bookmarks.Add(["Five"], rows[0]);
            bookmarks.ToggleMessageCommand.Execute(rows[3]).Subscribe();
            var (older, whole) = (bookmarks.All[0], bookmarks.All[1]);

            items = [];
            topics = new ChatTopics(items, store);
            bookmarks = new ChatBookmarks(topics, store);
            topics.Reset("feed");
            bookmarks.Reset("feed");
            var again = Transcript();
            items.AddRange(again.Skip(2));
            topics.Restore();

            await Assert.That(items.OfType<ChatTopicItem>()).IsEmpty();
            await Assert.That(bookmarks.Sections.Single().Bookmarks).IsEquivalentTo([older, whole], CollectionOrdering.Matching);
            await Assert.That(again[3].Bookmark).IsSameReferenceAs(whole);

            items.InsertRange(0, again.Take(2));
            topics.Restore();

            var topic = (ChatTopicItem)items[1];
            await Assert.That(items[2]).IsSameReferenceAs(again[1]);
            await Assert.That(bookmarks.Sections.Select(static s => (s.Topic, s.Bookmarks.Single()))).IsEquivalentTo(
                [((ChatTopicItem?)null, older), (topic, whole)], CollectionOrdering.Matching);
            bookmarks.RevealCommand.Execute(older).Subscribe();
            await Assert.That(topics.TakeReveal()?.Row).IsSameReferenceAs(again[0]);
        });
    }

    /// A topic's end can load a page ahead of the topic: it waits, saved, for the topic rather than being dropped.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_end_whose_topic_is_on_an_older_page_waits_for_it() {
        await RunOnUiAsync(async () => {
            var store = new ChatMarks();
            store.SetTopics("feed", new Dictionary<ChatMessageKey, ChatTopicMark> {
                [new(11, 0)] = new(false, "Locks", Anchor: "Point 1"),
                [new(12, 1)] = new(false, null, End: true, Anchor: "Point 2"),
            });
            var items = new AvaloniaList<ChatItemViewModel>();
            var topics = new ChatTopics(items, store);
            topics.Reset("feed");
            var rows = Transcript();
            items.AddRange(rows.Skip(2));
            topics.Restore();

            await Assert.That(items.OfType<ChatTopicEndItem>()).IsEmpty();
            await Assert.That(store.Topics("feed").Count).IsEqualTo(2);

            items.InsertRange(0, rows.Take(2));
            topics.Restore();
            await Assert.That(items.Select(static row => row.GetType().Name)).IsEquivalentTo(
                ["AssistantTextItem", "ChatTopicItem", "UserTurnItem", "AssistantTextItem", "ChatTopicEndItem", "UserTurnItem"], CollectionOrdering.Matching);
        });
    }

    /// Two chats on one transcript share its bookmarks, each with its own rows: a second chat opening
    /// it does not cost the first its way back to a bookmark's message.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_second_chat_on_the_transcript_leaves_the_first_able_to_reveal_its_bookmarks() {
        await RunOnUiAsync(async () => {
            var store = new ChatMarks();
            var rows = Transcript();
            var topics = new ChatTopics(new AvaloniaList<ChatItemViewModel>(rows), store);
            var bookmarks = new ChatBookmarks(topics, store);
            topics.Reset("feed");
            bookmarks.Reset("feed");
            bookmarks.Add(["Point 2"], rows[3]);

            var second = new ChatBookmarks(new ChatTopics(new AvaloniaList<ChatItemViewModel>(), store), store);
            second.Reset("feed");

            bookmarks.RevealCommand.Execute(bookmarks.All.Single()).Subscribe();
            await Assert.That(topics.TakeReveal()?.Row).IsSameReferenceAs(rows[3]);
        });
    }
}
