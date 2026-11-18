namespace Capacitor.App.ViewModels;

/// One section of the chat's outline: a topic and the bookmarks taken from its messages, or, when
/// <paramref name="Topic"/> is null, bookmarks taken from messages outside every topic.
public sealed record ChatBookmarkGroup(ChatTopicItem? Topic, IReadOnlyList<ChatBookmark> Bookmarks);
