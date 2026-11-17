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
        _all = [];
        _all.CollectionChanged += OnChanged;
        RemoveCommand = ReactiveCommand.Create<ChatBookmark>(bookmark => All.Remove(bookmark));
        RevealCommand = ReactiveCommand.Create<ChatBookmark>(Reveal);
    }

    public ReactiveCommand<ChatBookmark, Unit> RemoveCommand { get; }

    /// Brings the message a bookmark came from into view.
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

    /// The chat now shows <paramref name="feed"/>: its bookmarks come back with it.
    public void Reset(string? feed) {
        if (feed == _feed) return;
        _feed = feed;
        All = feed is not null && _store is not null ? _store.Bookmarks(feed) : [];
    }

    string? _feed;

    /// Bookmarks each piece of text, in order, as taken from <paramref name="source"/>. Blank text is skipped.
    public void Add(IEnumerable<string> texts, ChatItemViewModel source) {
        var message = _topics.OrdinalOf(source);
        All.AddRange(texts.Select(Tidy).Where(static text => text.Length > 0).Select(text => new ChatBookmark(text, message)));
    }

    public void Move(int from, int to) {
        if (from == to || from < 0 || to < 0 || from >= All.Count || to >= All.Count) return;
        All.Move(from, to);
    }

    void Reveal(ChatBookmark bookmark) {
        if (bookmark.Message is { } ordinal && _topics.MessageAt(ordinal) is { } row) _topics.Reveal(row);
    }

    void OnChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Any = _all.Count > 0;

    static string Tidy(string text) =>
        TextElision.End(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), TextLength);
}
