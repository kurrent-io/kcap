using System.Reactive;
using Avalonia.Collections;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The chat's bookmarks: text the user picked out of it. View state only, kept per transcript in
/// <see cref="ChatMarks"/> so the list outlives the tab.
public sealed class ChatBookmarks : ReactiveObject {
    const int TextLength = 400;

    readonly ChatTopics _topics;
    readonly ChatMarks? _store;

    public ChatBookmarks(ChatTopics topics, ChatMarks? store = null) {
        _topics = topics;
        _store = store;
        _topics.Laid += Outline;
        _all = [];
        _all.CollectionChanged += OnChanged;
        RemoveCommand = ReactiveCommand.Create<ChatBookmark>(bookmark => All.Remove(bookmark));
        RevealCommand = ReactiveCommand.Create<ChatBookmark>(Reveal);
        ToggleMessageCommand = ReactiveCommand.Create<ChatItemViewModel>(ToggleMessage);
    }

    /// Bookmarks a whole message, or takes its bookmark away.
    public ReactiveCommand<ChatItemViewModel, Unit> ToggleMessageCommand { get; }

    public ReactiveCommand<ChatBookmark, Unit> RemoveCommand { get; }

    /// Brings the message a bookmark came from into view, with the bookmarked text picked out.
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

    IReadOnlyList<ChatBookmarkGroup> _sections = [];
    /// The chat's outline: each topic with the bookmarks taken from its messages, and the bookmarks
    /// taken from outside every topic where they fall between topics, all in chat order.
    public IReadOnlyList<ChatBookmarkGroup> Sections {
        get => _sections;
        private set {
            this.RaiseAndSetIfChanged(ref _sections, value);
            HasSections = value.Count > 0;
        }
    }

    bool _hasSections;
    public bool HasSections { get => _hasSections; private set => this.RaiseAndSetIfChanged(ref _hasSections, value); }

    /// The chat now shows <paramref name="feed"/>: its bookmarks come back with it.
    public void Reset(string? feed) {
        if (feed == _feed) return;
        _feed = feed;
        All = feed is not null && _store is not null ? _store.Bookmarks(feed) : [];
        Outline();
    }

    string? _feed;

    /// Bookmarks each piece of text, in order, as taken from <paramref name="source"/>. Blank text is skipped.
    public void Add(IEnumerable<string> texts, ChatItemViewModel source) {
        var message = _topics.OrdinalOf(source);
        All.AddRange(texts.Select(quote => (Text: Tidy(quote), Quote: quote)).Where(static b => b.Text.Length > 0)
            .Select(b => new ChatBookmark(b.Text, message, b.Quote)));
    }

    void ToggleMessage(ChatItemViewModel source) {
        if (source.Bookmark is { } taken) {
            All.Remove(taken);
            return;
        }
        var text = Tidy(source switch { UserTurnItem user => user.Text, AssistantTextItem reply => reply.Text, _ => "" });
        All.Add(new ChatBookmark(text.Length > 0 ? text : "Message", _topics.OrdinalOf(source)));
    }

    void Reveal(ChatBookmark bookmark) {
        if (bookmark.Message is { } ordinal && _topics.MessageAt(ordinal) is { } row) _topics.Reveal(row, bookmark.Quote);
    }

    void OnChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) {
        Any = _all.Count > 0;
        Outline();
    }

    // A whole-message bookmark leads its message's picks; the picks keep the order they were taken in.
    void Outline() {
        var runs = _topics.Runs();
        var placed = _all.Where(static b => b.Message is not null)
            .OrderBy(static b => b.Message).ThenBy(static b => b.Quote is not null).ToList();
        var sections = new List<ChatBookmarkGroup>();
        var next = 0;
        for (var i = 0; i < runs.Count; i++) {
            var end = i + 1 < runs.Count ? runs[i + 1].First : int.MaxValue;
            var start = next;
            while (next < placed.Count && placed[next].Message < end) next++;
            if (runs[i].Topic is not null || next > start) sections.Add(new ChatBookmarkGroup(runs[i].Topic, placed[start..next]));
        }
        if (_all.Where(static b => b.Message is null).ToList() is { Count: > 0 } loose) sections.Add(new ChatBookmarkGroup(null, loose));
        Sections = sections;
        MarkMessages();
    }

    readonly List<ChatItemViewModel> _marked = [];

    void MarkMessages() {
        foreach (var row in _marked) row.Bookmark = null;
        _marked.Clear();
        var messages = _topics.Messages();
        foreach (var bookmark in _all)
            if (bookmark is { Quote: null, Message: { } ordinal } && ordinal < messages.Count && messages[ordinal] is { Bookmark: null } row) {
                row.Bookmark = bookmark;
                _marked.Add(row);
            }
    }

    static string Tidy(string text) =>
        TextElision.End(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), TextLength);
}
