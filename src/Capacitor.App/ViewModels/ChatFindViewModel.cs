using System.Collections.Specialized;
using System.Reactive;
using System.Runtime.CompilerServices;
using Avalonia.Collections;
using Capacitor.App.GitHubHtml;
using Markdig;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Find in the chat transcript: matches the query in the prose of each message, thinking block
/// and note, the text the transcript renders as markdown. Tool calls and cards are not searched.
public sealed class ChatFindViewModel : ReactiveObject {
    readonly IAvaloniaReadOnlyList<ChatItemViewModel> _items;
    readonly ConditionalWeakTable<ChatItemViewModel, IReadOnlyList<string>> _blocks = new();
    List<ChatFindMatch> _matches = [];
    bool _isOpen;
    string _query = "";
    int _currentIndex = -1;
    readonly Action<ChatFindState?>? _remember;
    (int ItemIndex, int Ordinal)? _returnTo;

    /// <param name="remember">Told the open find after every change, and null once it closes.</param>
    public ChatFindViewModel(IAvaloniaReadOnlyList<ChatItemViewModel> items, Action<ChatFindState?>? remember = null) {
        _items = items;
        _remember = remember;
        _items.CollectionChanged += OnItemsChanged;
        NextCommand = ReactiveCommand.Create(Next);
        PreviousCommand = ReactiveCommand.Create(Previous);
        CloseCommand = ReactiveCommand.Create(Close);
    }

    /// Raised when the current match moves by the reader's hand or a new query, not when the
    /// transcript grows: the view scrolls to it, and an append must not pull the reader away.
    public event Action<ChatFindMatch?>? Navigated;

    public bool IsOpen {
        get => _isOpen;
        private set {
            if (_isOpen == value) return;
            _isOpen = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(ActiveQuery));
        }
    }

    public string Query {
        get => _query;
        set {
            value ??= "";
            if (_query == value) return;
            _query = value;
            _returnTo = null;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(ActiveQuery));
            Search(StartIndex);
        }
    }

    /// What the transcript highlights: nothing while the bar is closed or the query is blank.
    public string? ActiveQuery => _isOpen && _query.Trim() is { Length: > 0 } query ? query : null;

    /// The item index the reader is looking at, from which a new query's first match is chosen.
    public int StartIndex { get; set; }

    public IReadOnlyList<ChatFindMatch> Matches => _matches;
    public int MatchCount => _matches.Count;
    public int CurrentIndex => _currentIndex;
    public ChatFindMatch? Current => _currentIndex >= 0 && _currentIndex < _matches.Count ? _matches[_currentIndex] : null;
    public bool HasMatches => _matches.Count > 0;

    public string Summary => ActiveQuery is null ? ""
        : _matches.Count == 0 ? "No results"
        : $"{_currentIndex + 1} of {_matches.Count}";

    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    /// Opening again keeps the query, as a browser's find bar does, and searches it afresh from
    /// where the reader now is.
    public void Open(int startIndex) {
        StartIndex = startIndex;
        if (_isOpen) return;
        IsOpen = true;
        Search(startIndex);
    }

    /// Reopens a find remembered from an earlier visit. Its match is taken back to once the
    /// transcript holds it, since a returning session's items are still loading.
    public void Restore(ChatFindState state) {
        _query = state.Query;
        _returnTo = state.ItemIndex >= 0 ? (state.ItemIndex, state.Ordinal) : null;
        IsOpen = true;
        this.RaisePropertyChanged(nameof(Query));
        Search(StartIndex);
    }

    public void Close() {
        if (!_isOpen) return;
        _returnTo = null;
        IsOpen = false;
        SetMatches([], -1);
        Navigated?.Invoke(null);
    }

    public void Next() => Step(1);

    public void Previous() => Step(-1);

    void Step(int delta) {
        if (_matches.Count == 0) return;
        _returnTo = null;
        var next = _currentIndex < 0 ? (delta > 0 ? 0 : _matches.Count - 1)
            : (_currentIndex + delta + _matches.Count) % _matches.Count;
        SetMatches(_matches, next);
        Navigated?.Invoke(Current);
    }

    void Search(int startIndex) {
        var matches = Collect();
        var current = -1;
        for (var i = 0; i < matches.Count; i++) {
            if (matches[i].ItemIndex < startIndex) continue;
            current = i;
            break;
        }
        if (current < 0 && matches.Count > 0) current = 0;
        if (ReturnTo(matches) is { } back) current = back;
        SetMatches(matches, current);
        if (_returnTo is null) Navigated?.Invoke(Current);
    }

    int? ReturnTo(List<ChatFindMatch> matches) {
        if (_returnTo is not var (itemIndex, ordinal)) return null;
        var at = matches.FindIndex(m => m.ItemIndex == itemIndex && m.Ordinal == ordinal);
        if (at < 0) return null;
        _returnTo = null;
        return at;
    }

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        if (ActiveQuery is null) return;
        var was = Current;
        var matches = Collect();
        var current = was is null ? (matches.Count > 0 ? 0 : -1)
            : matches.FindIndex(m => ReferenceEquals(m.Item, was.Item) && m.Ordinal == was.Ordinal);
        if (current < 0 && matches.Count > 0) current = Math.Min(_currentIndex, matches.Count - 1);
        if (ReturnTo(matches) is { } back) {
            SetMatches(matches, back);
            Navigated?.Invoke(Current);
            return;
        }
        SetMatches(matches, current);
    }

    List<ChatFindMatch> Collect() {
        var matches = new List<ChatFindMatch>();
        if (ActiveQuery is not { } query) return matches;
        for (var index = 0; index < _items.Count; index++) {
            var item = _items[index];
            if (!_blocks.TryGetValue(item, out var blocks)) {
                if (Source(item) is not var (text, pipeline)) continue;
                blocks = MarkdownFindText.Blocks(text, pipeline);
                _blocks.AddOrUpdate(item, blocks);
            }
            var ordinal = 0;
            foreach (var block in blocks) {
                var count = TextMatches.Count(block, query);
                for (var i = 0; i < count; i++) matches.Add(new ChatFindMatch(item, index, ordinal++));
            }
        }
        return matches;
    }

    void SetMatches(List<ChatFindMatch> matches, int current) {
        var countChanged = matches.Count != _matches.Count;
        _matches = matches;
        _currentIndex = current;
        if (Current?.Item is { } item) Reveal(item);
        if (countChanged) {
            this.RaisePropertyChanged(nameof(MatchCount));
            this.RaisePropertyChanged(nameof(HasMatches));
        }
        this.RaisePropertyChanged(nameof(Matches));
        this.RaisePropertyChanged(nameof(CurrentIndex));
        this.RaisePropertyChanged(nameof(Current));
        this.RaisePropertyChanged(nameof(Summary));
        _remember?.Invoke(!_isOpen ? null
            : _returnTo is var (itemIndex, ordinal) ? new ChatFindState(_query, itemIndex, ordinal)
            : new ChatFindState(_query, Current?.ItemIndex ?? -1, Current?.Ordinal ?? 0));
    }

    /// A match in a folded block is shown by unfolding it.
    static void Reveal(ChatItemViewModel item) {
        switch (item) {
            case AssistantThinkingItem { IsExpanded: false } thinking: thinking.Toggle(); break;
            case SystemNoteItem { IsCollapsible: true, IsExpanded: false } note: note.Toggle(); break;
        }
    }

    /// Each kind with the pipeline its view renders it under, so the counts agree with the highlights.
    static (string Text, MarkdownPipeline Pipeline)? Source(ChatItemViewModel item) => item switch {
        UserTurnItem { HasText: true } user => (user.Text, ChatPipeline.Prompt),
        AssistantTextItem assistant => (assistant.Text, ChatPipeline.Instance),
        AssistantThinkingItem thinking => (thinking.Text, ChatPipeline.Instance),
        SystemNoteItem note => (note.Text, ChatPipeline.Instance),
        _ => null,
    };
}
