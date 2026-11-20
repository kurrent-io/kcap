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
        var anchor = ChatTopics.AnchorOf(source);
        All.AddRange(texts.Select(quote => (Text: Tidy(quote), Quote: quote)).Where(static b => b.Text.Length > 0)
            .Select(b => new ChatBookmark(b.Text, source.Key, b.Quote, anchor) { Source = source }));
    }

    void ToggleMessage(ChatItemViewModel source) {
        if (source.Bookmark is { } taken) {
            All.Remove(taken);
            return;
        }
        var text = Tidy(source switch { UserTurnItem user => user.Text, AssistantTextItem reply => reply.Text, _ => "" });
        All.Add(new ChatBookmark(text.Length > 0 ? text : "Message", source.Key, anchor: ChatTopics.AnchorOf(source)) { Source = source });
    }

    void Reveal(ChatBookmark bookmark) {
        if (bookmark.Source is { } row) _topics.Reveal(row, bookmark.Quote);
    }

    void OnChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) {
        Any = _all.Count > 0;
        Outline();
    }

    // A whole-message bookmark leads its message's picks; the picks keep the order they were taken in.
    // One whose message is not shown yet sits where its key puts it: before the messages shown when an
    // older page holds it, after them when it is still to be read. One whose message has come back as a
    // different message, or is gone, joins those taken from no message.
    void Outline() {
        var runs = _topics.Runs();
        var messages = _topics.Messages();
        var shown = new Dictionary<ChatItemViewModel, int>(ReferenceEqualityComparer.Instance);
        var keyed = new Dictionary<ChatMessageKey, ChatItemViewModel>();
        for (var i = 0; i < messages.Count; i++) {
            shown[messages[i]] = i;
            if (messages[i].Key is { } key) keyed.TryAdd(key, messages[i]);
        }
        var first = messages.Select(static m => m.Key).FirstOrDefault(static k => k is not null);
        var last = messages.Select(static m => m.Key).LastOrDefault(static k => k is not null);
        int? PositionOf(ChatBookmark b) {
            if (b.Source is { } source && shown.TryGetValue(source, out var at)) return at;
            b.Source = b.Message is { } key && keyed.TryGetValue(key, out var row) && b.Fits(row) ? row : null;
            if (b.Source is not null) return shown[b.Source];
            if (b.Message is not { } wanted || keyed.ContainsKey(wanted)) return null;
            if (first is null || wanted < first.Value) return -1;
            return wanted > last!.Value ? int.MaxValue : null;
        }
        var positions = _all.Select(b => (Bookmark: b, At: PositionOf(b))).ToList();
        var placed = positions.Where(static p => p.At is not null)
            .OrderBy(static p => p.At).ThenBy(static p => p.Bookmark.Message).ThenBy(static p => p.Bookmark.Quote is not null).ToList();
        var sections = new List<ChatBookmarkGroup>();
        var next = 0;
        for (var i = 0; i < runs.Count; i++) {
            var end = i + 1 < runs.Count ? runs[i + 1].First : int.MaxValue;
            var start = next;
            while (next < placed.Count && (placed[next].At < end || i + 1 == runs.Count)) next++;
            if (runs[i].Topic is not null || next > start)
                sections.Add(new ChatBookmarkGroup(runs[i].Topic, [.. placed[start..next].Select(static p => p.Bookmark)]));
        }
        if (positions.Where(static p => p.At is null).Select(static p => p.Bookmark).ToList() is { Count: > 0 } loose)
            sections.Add(new ChatBookmarkGroup(null, loose));
        Sections = sections;
        MarkMessages();
    }

    readonly List<ChatItemViewModel> _marked = [];

    void MarkMessages() {
        foreach (var row in _marked) row.Bookmark = null;
        _marked.Clear();
        foreach (var bookmark in _all)
            if (bookmark is { Quote: null, Source: { Bookmark: null } row }) {
                row.Bookmark = bookmark;
                _marked.Add(row);
            }
    }

    static string Tidy(string text) =>
        TextElision.End(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), TextLength);
}
