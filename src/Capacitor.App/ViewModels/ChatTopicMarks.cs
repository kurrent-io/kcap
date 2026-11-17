using System.Collections.Frozen;

namespace Capacitor.App.ViewModels;

/// Where the user changed topic in each transcript, held for the app's lifetime: a workspace is torn
/// down on every navigation away, so its topics have to outlive the chat tab. A mark is the ordinal
/// of the message that starts the topic, counting user and agent messages from the transcript's
/// start.
public sealed class ChatTopicMarks {
    readonly Dictionary<string, IReadOnlyDictionary<int, ChatTopicMark>> _marks = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<int, ChatTopicMark> Get(string feed) =>
        _marks.TryGetValue(feed, out var marks) ? marks : FrozenDictionary<int, ChatTopicMark>.Empty;

    public void Set(string feed, IReadOnlyDictionary<int, ChatTopicMark> marks) {
        if (marks.Count == 0) _marks.Remove(feed);
        else _marks[feed] = marks;
    }
}
