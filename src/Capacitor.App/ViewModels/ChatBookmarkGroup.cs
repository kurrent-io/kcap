namespace Capacitor.App.ViewModels;

/// The bookmarks taken from one topic's messages, or from messages outside every topic when
/// <paramref name="Topic"/> is null.
public sealed record ChatBookmarkGroup(ChatTopicItem? Topic, IReadOnlyList<ChatBookmark> Bookmarks) {
    public string Title => Topic?.Label ?? "No topic";
}
