using System.Globalization;

namespace Capacitor.App.ViewModels;

/// One section of the chat's outline: a topic and the bookmarks taken from its messages, or, when
/// <paramref name="Topic"/> is null, bookmarks taken from messages outside every topic.
public sealed record ChatBookmarkGroup(ChatTopicItem? Topic, IReadOnlyList<ChatBookmark> Bookmarks) {
    public string LooseEndsLabel => Bookmarks.Count == 1
        ? "File 1 bookmark as a loose end"
        : string.Create(CultureInfo.InvariantCulture, $"File {Bookmarks.Count} bookmarks as loose ends");
}
