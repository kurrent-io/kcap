using System.Collections.Frozen;
using System.ComponentModel;
using Avalonia.Collections;
using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

/// The topics and bookmarks the user made in each transcript. A workspace is torn down on every
/// navigation away, so they live here for the app's lifetime, and in <see cref="ChatMarksFiles"/>
/// across restarts when given one. A message is named by its <see cref="ChatMessageKey"/>.
public sealed class ChatMarks(ChatMarksFiles? files = null, TimeProvider? time = null) {
    const int Version = 2;

    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly Dictionary<string, IReadOnlyDictionary<ChatMessageKey, ChatTopicMark>> _topics = new(StringComparer.Ordinal);
    readonly Dictionary<string, AvaloniaList<ChatBookmark>> _bookmarks = new(StringComparer.Ordinal);

    /// Topics by the message each starts at.
    public IReadOnlyDictionary<ChatMessageKey, ChatTopicMark> Topics(string feed) {
        Load(feed);
        return _topics.TryGetValue(feed, out var marks) ? marks : FrozenDictionary<ChatMessageKey, ChatTopicMark>.Empty;
    }

    /// The chat lays its topics out again on every new row, so an unchanged set is not written.
    public void SetTopics(string feed, IReadOnlyDictionary<ChatMessageKey, ChatTopicMark> marks) {
        Load(feed);
        var held = Topics(feed);
        if (held.Count == marks.Count && marks.All(m => held.TryGetValue(m.Key, out var had) && had == m.Value)) return;
        if (marks.Count == 0) _topics.Remove(feed);
        else _topics[feed] = marks;
        Save(feed);
    }

    /// The transcript's bookmarks, in the order they were taken; the chat edits this list in place.
    public AvaloniaList<ChatBookmark> Bookmarks(string feed) {
        Load(feed);
        return _bookmarks[feed];
    }

    void Load(string feed) {
        if (_bookmarks.ContainsKey(feed)) return;
        // A file of another version is left to be replaced by the next save.
        var entry = files?.Read(feed) is { Version: Version } read ? read : null;
        var list = new AvaloniaList<ChatBookmark>();
        if (entry is not null) {
            var topics = new Dictionary<ChatMessageKey, ChatTopicMark>();
            // The file is ours but hand-editable: a null or incomplete record is skipped, not trusted.
            foreach (var topic in entry.Topics ?? [])
                if (ChatMessageKey.Parse(topic?.Message) is { } key)
                    topics[key] = new(topic!.Folded ?? false, topic.Name, topic.End ?? false, topic.Anchor);
            if (topics.Count > 0) _topics[feed] = topics;
            foreach (var mark in entry.Bookmarks ?? []) {
                if (mark?.Text is null) continue;
                var bookmark = new ChatBookmark(mark.Text, ChatMessageKey.Parse(mark.Message), mark.Quote, mark.Anchor) {
                    Color = Enum.TryParse<ChatBookmarkColor>(mark.Color, ignoreCase: true, out var color) ? color : ChatBookmarkColor.Blue,
                };
                list.Add(bookmark);
            }
        }
        foreach (var bookmark in list) bookmark.PropertyChanged += OnBookmarkChanged;
        list.CollectionChanged += (_, e) => {
            foreach (var gone in e.OldItems?.OfType<ChatBookmark>() ?? []) gone.PropertyChanged -= OnBookmarkChanged;
            foreach (var added in e.NewItems?.OfType<ChatBookmark>() ?? []) added.PropertyChanged += OnBookmarkChanged;
            Save(feed);
        };
        _bookmarks[feed] = list;

        void OnBookmarkChanged(object? sender, PropertyChangedEventArgs e) => Save(feed);
    }

    void Save(string feed) {
        if (files is null) return;
        var topics = _topics.TryGetValue(feed, out var marks) ? marks : FrozenDictionary<ChatMessageKey, ChatTopicMark>.Empty;
        var bookmarks = _bookmarks[feed];
        if (topics.Count == 0 && bookmarks.Count == 0) {
            files.Write(feed, null);
            return;
        }
        files.Write(feed, new ChatMarksFiles.Entry(Version, feed, _time.GetUtcNow(),
            [.. topics.OrderBy(static t => t.Key).Select(static t => new ChatMarksFiles.Topic(
                t.Key.ToString(), t.Value.Anchor, t.Value.Name, t.Value.Folded ? true : null, t.Value.End ? true : null))],
            [.. bookmarks.Select(static b => new ChatMarksFiles.Bookmark(
                b.Text, b.Color.ToString().ToLowerInvariant(), b.Message?.ToString(), b.Anchor, b.Quote))]));
    }
}
