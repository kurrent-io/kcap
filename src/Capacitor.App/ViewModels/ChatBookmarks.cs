using System.Reactive;
using Avalonia.Collections;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The chat's bookmarks: text the user picked out of it, in an order they choose. View state only,
/// kept per transcript in <see cref="ChatMarks"/> so the list outlives the tab.
public sealed class ChatBookmarks : ReactiveObject {
    const int TextLength = 400;

    readonly ChatTopics _topics;
    readonly ChatMarks? _store;

    public ChatBookmarks(ChatTopics topics, ChatMarks? store = null) {
        _topics = topics;
        _store = store;
        _topics.Laid += Regroup;
        _all = [];
        _all.CollectionChanged += OnChanged;
        RemoveCommand = ReactiveCommand.Create<ChatBookmark>(bookmark => All.Remove(bookmark));
        RevealCommand = ReactiveCommand.Create<ChatBookmark>(Reveal);
    }

    public ReactiveCommand<ChatBookmark, Unit> RemoveCommand { get; }

    /// Brings the message a bookmark came from into view, with the bookmarked text, or all of it, picked out.
    public ReactiveCommand<ChatBookmark, Unit> RevealCommand { get; }

    AvaloniaList<ChatBookmark> _all;
    public AvaloniaList<ChatBookmark> All {
        get => _all;
        private set {
            _all.CollectionChanged -= OnChanged;
            this.RaiseAndSetIfChanged(ref _all, value);
            _all.CollectionChanged += OnChanged;
            Any = _all.Count > 0;
        }
    }

    bool _any;
    public bool Any { get => _any; private set => this.RaiseAndSetIfChanged(ref _any, value); }

    bool _grouped;
    /// Whether the list shows its bookmarks under the topics they were taken from, in
    /// <see cref="Groups"/>. Grouped, the order within each topic is still the user's.
    public bool Grouped {
        get => _grouped;
        set {
            this.RaiseAndSetIfChanged(ref _grouped, value);
            Regroup();
        }
    }

    IReadOnlyList<ChatBookmarkGroup> _groups = [];
    /// The topics' groups in chat order, then the bookmarks taken from outside every topic. Empty
    /// unless <see cref="Grouped"/>.
    public IReadOnlyList<ChatBookmarkGroup> Groups { get => _groups; private set => this.RaiseAndSetIfChanged(ref _groups, value); }

    /// The chat now shows <paramref name="feed"/>: its bookmarks come back with it.
    public void Reset(string? feed) {
        if (feed == _feed) return;
        _feed = feed;
        All = feed is not null && _store is not null ? _store.Bookmarks(feed) : [];
        Regroup();
    }

    string? _feed;

    /// Bookmarks each piece of text, in order, as taken from <paramref name="source"/>. Blank text is skipped.
    public void Add(IEnumerable<string> texts, ChatItemViewModel source) {
        var message = _topics.OrdinalOf(source);
        All.AddRange(texts.Select(quote => (Text: Tidy(quote), Quote: quote)).Where(static b => b.Text.Length > 0)
            .Select(b => new ChatBookmark(b.Text, message, b.Quote)));
    }

    /// Bookmarks all of <paramref name="source"/>, shown as <paramref name="text"/>.
    public void AddMessage(string text, ChatItemViewModel source) {
        if (Tidy(text) is { Length: > 0 } tidy) All.Add(new ChatBookmark(tidy, _topics.OrdinalOf(source)));
    }

    public void Move(int from, int to) {
        if (from == to || from < 0 || to < 0 || from >= All.Count || to >= All.Count) return;
        All.Move(from, to);
    }

    void Reveal(ChatBookmark bookmark) {
        if (bookmark.Message is { } ordinal && _topics.MessageAt(ordinal) is { } row) _topics.Reveal(row, bookmark.Quote, whole: bookmark.Quote is null);
    }

    void OnChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) {
        Any = _all.Count > 0;
        Regroup();
    }

    void Regroup() {
        if (!_grouped) {
            if (_groups.Count > 0) Groups = [];
            return;
        }
        var topics = _topics.TopicsByMessage();
        var order = _topics.All.Select(static (topic, index) => (topic, index)).ToDictionary(static p => p.topic, static p => p.index);
        Groups = _all
            .GroupBy(bookmark => bookmark.Message is { } m && topics.TryGetValue(m, out var topic) ? topic : null)
            .OrderBy(group => group.Key is { } topic && order.TryGetValue(topic, out var index) ? index : int.MaxValue)
            .Select(static group => new ChatBookmarkGroup(group.Key, group.ToList()))
            .ToList();
    }

    static string Tidy(string text) =>
        TextElision.End(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), TextLength);
}
