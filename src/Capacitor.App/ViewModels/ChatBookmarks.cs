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
    // The row each bookmark came from, while this chat shows it. Per chat: the bookmarks are shared
    // by every tab showing the transcript, each with rows of its own.
    readonly Dictionary<ChatBookmark, ChatItemViewModel> _sources = new(ReferenceEqualityComparer.Instance);

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

    /// Brings the message a bookmark came from into view, with the bookmarked text picked out, paging
    /// the chat back first when that message is on a page still to load.
    public ReactiveCommand<ChatBookmark, Unit> RevealCommand { get; }

    /// Asks for the page before the oldest one shown; false when there is none.
    public Func<bool>? LoadOlder { get; init; }

    // The bookmark being revealed once the page holding its message loads.
    ChatBookmark? _seeking;
    // Bookmarks whose message is older than every one shown.
    readonly HashSet<ChatBookmark> _older = new(ReferenceEqualityComparer.Instance);

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
        _seeking = null;
        _sources.Clear();
        All = feed is not null && _store is not null ? _store.Bookmarks(feed) : [];
        Outline();
    }

    string? _feed;

    /// Bookmarks each piece of text, in order, as taken from <paramref name="source"/>. Blank text is skipped.
    public void Add(IEnumerable<string> texts, ChatItemViewModel source) {
        var anchor = ChatTopics.AnchorOf(source);
        var added = texts.Select(quote => (Text: Tidy(quote), Quote: quote)).Where(static b => b.Text.Length > 0)
            .Select(b => new ChatBookmark(b.Text, source.Key, b.Quote, anchor)).ToList();
        foreach (var bookmark in added) _sources[bookmark] = source;
        All.AddRange(added);
    }

    void ToggleMessage(ChatItemViewModel source) {
        if (source.Bookmark is { } taken) {
            All.Remove(taken);
            return;
        }
        var text = Tidy(source switch { UserTurnItem user => user.Text, AssistantTextItem reply => reply.Text, _ => "" });
        var bookmark = new ChatBookmark(text.Length > 0 ? text : "Message", source.Key, anchor: ChatTopics.AnchorOf(source));
        _sources[bookmark] = source;
        All.Add(bookmark);
    }

    void Reveal(ChatBookmark bookmark) {
        _seeking = null;
        if (_sources.TryGetValue(bookmark, out var row)) _topics.Reveal(row, bookmark.Quote);
        else if (_older.Contains(bookmark) && LoadOlder?.Invoke() == true) _seeking = bookmark;
    }

    void Seek() {
        if (_seeking is not { } wanted) return;
        if (_sources.TryGetValue(wanted, out var row)) {
            _seeking = null;
            _topics.Reveal(row, wanted.Quote);
        } else if (!_older.Contains(wanted) || LoadOlder?.Invoke() != true) _seeking = null;
    }

    void OnChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) {
        foreach (var gone in e.OldItems?.OfType<ChatBookmark>() ?? []) _sources.Remove(gone);
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
            if (_sources.TryGetValue(b, out var source) && shown.TryGetValue(source, out var at)) return at;
            if (b.Message is { } key && keyed.TryGetValue(key, out var row) && b.Fits(row)) {
                _sources[b] = row;
                return shown[row];
            }
            _sources.Remove(b);
            if (b.Message is not { } wanted || keyed.ContainsKey(wanted)) return null;
            if (first is null || wanted < first.Value) {
                _older.Add(b);
                return -1;
            }
            return wanted > last!.Value ? int.MaxValue : null;
        }
        _older.Clear();
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
        Seek();
    }

    readonly List<ChatItemViewModel> _marked = [];

    void MarkMessages() {
        foreach (var row in _marked) row.Bookmark = null;
        _marked.Clear();
        foreach (var bookmark in _all)
            if (bookmark.Quote is null && _sources.TryGetValue(bookmark, out var row) && row.Bookmark is null) {
                row.Bookmark = bookmark;
                _marked.Add(row);
            }
    }

    static string Tidy(string text) =>
        TextElision.End(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), TextLength);
}
