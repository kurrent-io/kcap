using System.Collections.Frozen;
using Avalonia.Collections;

namespace Capacitor.App.ViewModels;

/// The topics and bookmarks the user made in each transcript, held for the app's lifetime: a workspace
/// is torn down on every navigation away, so they have to outlive the chat tab. A message is named by
/// its ordinal, counting user and agent messages from the transcript's start.
public sealed class ChatMarks {
    readonly Dictionary<string, IReadOnlyDictionary<int, ChatTopicMark>> _topics = new(StringComparer.Ordinal);
    readonly Dictionary<string, AvaloniaList<ChatBookmark>> _bookmarks = new(StringComparer.Ordinal);

    /// Topics by the ordinal of the message each starts at.
    public IReadOnlyDictionary<int, ChatTopicMark> Topics(string feed) =>
        _topics.TryGetValue(feed, out var marks) ? marks : FrozenDictionary<int, ChatTopicMark>.Empty;

    public void SetTopics(string feed, IReadOnlyDictionary<int, ChatTopicMark> marks) {
        if (marks.Count == 0) _topics.Remove(feed);
        else _topics[feed] = marks;
    }

    /// The transcript's bookmarks, in the user's order; the chat edits this list in place.
    public AvaloniaList<ChatBookmark> Bookmarks(string feed) {
        if (!_bookmarks.TryGetValue(feed, out var list)) _bookmarks[feed] = list = [];
        return list;
    }
}
