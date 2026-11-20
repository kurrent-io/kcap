using System.Reactive;
using Avalonia.Collections;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Topics split the chat at the messages the user marks as a change of topic. Each runs from its marked
/// message to the next mark, an end the user put after it, or the end of the chat, and folds into the
/// header above that message.
/// View state only: nothing reaches the agent, and the marks outlive the tab only in <see cref="ChatMarks"/>.
public sealed class ChatTopics : ReactiveObject {
    const int LabelLength = 72;

    readonly AvaloniaList<ChatItemViewModel> _items;
    readonly ChatMarks? _store;
    string? _feed;
    // Saved marks whose message has not been replayed yet.
    Dictionary<ChatMessageKey, ChatTopicMark> _restoring = [];
    // Restored topics saved as folded, folded once the next topic bounds them: until then more of
    // their rows may still be on the way. The last topic stays open, where new activity lands.
    readonly HashSet<ChatTopicItem> _folding = [];
    bool _editing;
    // Until a topic exists, the list's changes need no laying out.
    bool _used;
    // Marks made for messages being sent, in the order sent, each waiting for its message to land.
    readonly List<ChatItemViewModel> _pending = [];
    // How each waiting mark knows its message; a mark with none takes the next one the user sent.
    readonly Dictionary<ChatItemViewModel, Func<UserTurnItem, bool>> _echoes = new(ReferenceEqualityComparer.Instance);

    public ChatTopics(AvaloniaList<ChatItemViewModel> items, ChatMarks? store = null) {
        _items = items;
        _store = store;
        ToggleChangeCommand = ReactiveCommand.Create<ChatItemViewModel>(ToggleChange);
        RevealCommand = ReactiveCommand.Create<ChatTopicItem>(topic => Reveal(topic));
        RemoveCommand = ReactiveCommand.Create<ChatTopicItem>(Remove);
        RemoveEndCommand = ReactiveCommand.Create<ChatTopicEndItem>(RemoveEnd);
        _items.CollectionChanged += (_, _) => {
            if (_used && !_editing) Lay();
        };
    }

    public ReactiveCommand<ChatItemViewModel, Unit> ToggleChangeCommand { get; }

    /// Asks the chat to bring a topic's header into view.
    public ReactiveCommand<ChatTopicItem, Unit> RevealCommand { get; }

    /// Takes a topic away, joining its rows to the topic before.
    public ReactiveCommand<ChatTopicItem, Unit> RemoveCommand { get; }

    /// Takes an end away, joining the rows after it to the topic it ended.
    public ReactiveCommand<ChatTopicEndItem, Unit> RemoveEndCommand { get; }

    /// Raised when a row is to be shown. The chat view is detached while another tab shows, so the
    /// request also waits in <see cref="TakeReveal"/> until a view takes it.
    public event Action? Revealing;

    ChatReveal? _reveal;
    public ChatReveal? TakeReveal() {
        var reveal = _reveal;
        _reveal = null;
        return reveal;
    }

    /// Raised once topics have been laid out again after any change to the chat or its marks, and once
    /// each batch of rows has landed, whose messages saved bookmarks may be waiting for.
    public event Action? Laid;

    /// Every topic's header, in chat order.
    public AvaloniaList<ChatTopicItem> All { get; } = [];

    bool _any;
    public bool Any { get => _any; private set => this.RaiseAndSetIfChanged(ref _any, value); }

    bool _canEnd;
    /// Whether a topic is open at the end of the chat, for <see cref="End"/> to close.
    public bool CanEnd { get => _canEnd; private set => this.RaiseAndSetIfChanged(ref _canEnd, value); }

    /// Starts a topic at the end of the chat for the message about to be sent. It stays at the end
    /// until that message lands, so rows that land first stay out of it.
    public ChatTopicItem Start(Func<UserTurnItem, bool>? isEcho = null) {
        var topic = NewTopic();
        Edit(() => {
            var tail = TailStart();
            if (tail > 0 && _items[tail - 1] is ChatTopicEndItem end && !_pending.Contains(end)) _items.Remove(end);
            _items.Insert(TailStart(), topic);
        });
        Wait(topic, isEcho);
        return topic;
    }

    /// Ends the open topic at the end of the chat, so the message about to be sent falls outside it.
    /// Null when no topic is open there.
    public ChatTopicEndItem? End(Func<UserTurnItem, bool>? isEcho = null) {
        if (!CanEnd) return null;
        var end = new ChatTopicEndItem();
        Edit(() => _items.Insert(TailStart(), end));
        Wait(end, isEcho);
        return end;
    }

    void Wait(ChatItemViewModel mark, Func<UserTurnItem, bool>? isEcho) {
        _pending.Add(mark);
        if (isEcho is not null) _echoes[mark] = isEcho;
    }

    bool Unwait(ChatItemViewModel mark) {
        _echoes.Remove(mark);
        return _pending.Remove(mark);
    }

    /// Takes back a mark made for a message that never went out, unless that message has landed.
    public void Discard(ChatItemViewModel mark) => Edit(() => {
        Settle();
        if (Unwait(mark)) _items.Remove(mark);
    });

    /// Marks <paramref name="row"/> as where a new topic starts, or unmarks it, which joins its topic to
    /// the one before.
    public void ToggleChange(ChatItemViewModel row) => Edit(() => {
        var index = _items.IndexOf(row);
        if (index < 0 || index >= TailStart() || row is ChatTopicItem or ChatTopicEndItem) return;
        if (index > 0 && _items[index - 1] is ChatTopicItem topic) Remove(topic);
        else if (index > 0 && _items[index - 1] is ChatTopicEndItem end) _items[index - 1] = NewTopic();
        else _items.Insert(index, NewTopic());
    });

    /// The feed is replaying from scratch: every topic is rebuilt from the marks saved for it, as
    /// its message comes back.
    public void Reset(string? feed) {
        _used = false;
        _feed = feed;
        _folding.Clear();
        _pending.Clear();
        _echoes.Clear();
        All.Clear();
        Any = false;
        _restoring = feed is not null && _store is not null ? new(_store.Topics(feed)) : [];
    }

    /// Puts back the saved topics whose message has been replayed since the last call. Called once a
    /// batch of rows has landed, never from inside the list's own change notification.
    public void Restore() {
        if (_restoring.Count == 0 && _folding.Count == 0 && _pending.Count == 0) {
            Laid?.Invoke();
            return;
        }
        var found = new List<(ChatItemViewModel Row, ChatMessageKey Key, ChatTopicMark Mark)>();
        var tail = TailStart();
        for (var i = 0; i < tail; i++) {
            var row = _items[i];
            // A mark whose message changed stays saved but is not put back.
            if (row.Key is { } key && _restoring.TryGetValue(key, out var mark) && (mark.Anchor is null || mark.Anchor == AnchorOf(row))) {
                _restoring.Remove(key);
                found.Add((row, key, mark));
            }
        }
        Edit(() => {
            Settle();
            foreach (var (row, key, mark) in found) {
                var index = _items.IndexOf(row);
                if (index > 0 && _items[index - 1] is ChatTopicItem or ChatTopicEndItem) continue;
                if (mark.End) {
                    // The topic it ends may be on a page still to load.
                    if (TopicBefore(index) is null) {
                        _restoring[key] = mark;
                        continue;
                    }
                    _used = true;
                    _items.Insert(index, new ChatTopicEndItem());
                    continue;
                }
                var topic = NewTopic();
                topic.CustomName = mark.Name;
                _items.Insert(index, topic);
                if (mark.Folded) _folding.Add(topic);
            }
            foreach (var topic in _folding.ToList()) {
                var index = _items.IndexOf(topic);
                if (index < 0 || !topic.IsExpanded) { _folding.Remove(topic); continue; }
                var end = EndOf(index);
                if (!(end < _items.Count && _items[end] is ChatTopicItem || _items[end - 1] is ChatTopicEndItem)) continue;
                _folding.Remove(topic);
                Toggle(topic);
            }
        });
    }

    ChatTopicItem? _current;
    // A topic the user went to stays current until they scroll: the chat may not be able to bring
    // its header to the top, near the end of the transcript.
    bool _held;

    /// The chat now shows <paramref name="row"/> at its top: the topic holding it becomes the current
    /// one, unless the user went to a topic and has not scrolled since.
    public void ShowingAtTop(ChatItemViewModel? row) {
        if (!_held) SetCurrent(row);
    }

    /// The user scrolled: the topic at the top of the chat is current again.
    public void ReleaseCurrent() => _held = false;

    void SetCurrent(ChatItemViewModel? row) {
        var topic = row as ChatTopicItem ?? row?.Topic;
        if (ReferenceEquals(topic, _current)) return;
        _current?.IsCurrent = false;
        _current = topic;
        _current?.IsCurrent = true;
    }

    /// Every row, those inside folded topics included.
    public IEnumerable<ChatItemViewModel> Everything() {
        foreach (var row in _items) {
            yield return row;
            if (row is ChatTopicItem { IsExpanded: false } folded)
                foreach (var hidden in folded.Hidden) yield return hidden;
        }
    }

    ChatTopicItem NewTopic() {
        _used = true;
        return new ChatTopicItem(Toggle, Rename);
    }

    void Rename(ChatTopicItem topic, string? name) => Edit(() => topic.CustomName = name);

    /// Asks the chat to bring <paramref name="row"/> into view, unfolding the topic that holds it, and
    /// to pick out <paramref name="quote"/> in it.
    public void Reveal(ChatItemViewModel row, string? quote = null) {
        if (!_items.Contains(row)) {
            if (_items.OfType<ChatTopicItem>().FirstOrDefault(topic => topic.Hidden.Contains(row)) is not { } folded) return;
            Toggle(folded);
        }
        _reveal = new ChatReveal(row, quote);
        SetCurrent(row);
        _held = true;
        Revealing?.Invoke();
    }

    public void RemoveEnd(ChatTopicEndItem end) => Edit(() => {
        Unwait(end);
        var index = _items.IndexOf(end);
        if (index < 0) return;
        if (TopicBefore(index) is { IsExpanded: false } previous) Unfold(previous);
        _items.Remove(end);
    });

    public void Remove(ChatTopicItem topic) => Edit(() => {
        if (!_items.Contains(topic)) return;
        if (!topic.IsExpanded) Unfold(topic);
        var index = _items.IndexOf(topic);
        if (TopicBefore(index) is { IsExpanded: false } previous) Unfold(previous);
        _folding.Remove(topic);
        Unwait(topic);
        _items.Remove(topic);
    });

    // Each mark waiting for a message goes before the first message the user sent after it, or the
    // one before it was bound, and stays at the end of the chat until there is one.
    void Settle() {
        ChatItemViewModel? after = null;
        foreach (var mark in _pending.ToList()) {
            var index = _items.IndexOf(mark);
            if (index < 0) {
                Unwait(mark);
                continue;
            }
            var tail = TailStart();
            var target = Math.Max(index + 1, after is null ? 0 : _items.IndexOf(after) + 1);
            var isEcho = _echoes.GetValueOrDefault(mark);
            while (target < tail && !(_items[target] is UserTurnItem turn && (isEcho?.Invoke(turn) ?? true))) target++;
            var bound = target < tail;
            if (target != index + 1) {
                _items.RemoveAt(index);
                _items.Insert(target - 1, mark);
            }
            if (bound) {
                Unwait(mark);
                after = _items[target];
            } else after = mark;
        }
    }

    /// The chat as runs of messages in order, each starting at the index in <see cref="Messages"/> of its
    /// first message: a topic's, or one outside every topic. A topic with no messages yet still has its run.
    public IReadOnlyList<(ChatTopicItem? Topic, int First)> Runs() {
        var runs = new List<(ChatTopicItem?, int)> { (null, 0) };
        var ordinal = 0;
        foreach (var row in Everything()) {
            if (row is ChatTopicItem topic) runs.Add((topic, ordinal));
            else if (row is ChatTopicEndItem) runs.Add((null, ordinal));
            else if (IsMessage(row)) ordinal++;
        }
        return runs;
    }

    /// Every message shown, folded ones too, in chat order.
    public IReadOnlyList<ChatItemViewModel> Messages() => Everything().Where(IsMessage).ToList();

    void Toggle(ChatTopicItem topic) => Edit(() => {
        _folding.Remove(topic);
        var index = _items.IndexOf(topic);
        if (index < 0) return;
        if (!topic.IsExpanded) {
            Unfold(topic);
            return;
        }
        var end = EndOf(index);
        if (end <= index + 1) return;
        var rows = _items.GetRange(index + 1, end - index - 1).ToList();
        _items.RemoveRange(index + 1, rows.Count);
        topic.Fold(rows, SummaryOf(rows));
    });

    void Unfold(ChatTopicItem topic) => _items.InsertRange(_items.IndexOf(topic) + 1, topic.Unfold());

    // Rails and names are laid once the whole edit is done: midway, a topic's rows can be out of the list
    // before they are in its fold.
    void Edit(Action edit) {
        if (_editing) { edit(); return; }
        _editing = true;
        try {
            edit();
            Prune();
        } finally { _editing = false; }
        Lay();
    }

    // An end with no topic open before it ends nothing, as when the topic it ended is taken away.
    void Prune() {
        var open = false;
        for (var i = 0; i < _items.Count; i++) {
            if (_items[i] is ChatTopicItem topic) open = !Ended(topic);
            else if (_items[i] is ChatTopicEndItem end) {
                if (open) open = false;
                else {
                    Unwait(end);
                    _items.RemoveAt(i--);
                }
            }
        }
    }

    void Lay() {
        ChatTopicItem? current = null;
        var tail = TailStart();
        var number = 0;
        for (var i = 0; i < _items.Count; i++) {
            var row = _items[i];
            row.StartsTopic = i > 0 && _items[i - 1] is ChatTopicItem && row is not ChatTopicItem;
            // An end sits on its topic's rail while the topic is open, as the rail's last row.
            if (row is ChatTopicEndItem) {
                row.Topic = current is { IsExpanded: true } ? current : null;
                current = null;
                continue;
            }
            if (row is ChatTopicItem topic) {
                row.Topic = null;
                var rows = topic.IsExpanded ? _items.Skip(i + 1).Take(EndOf(i) - i - 1).ToList() : topic.Hidden;
                number++;
                topic.Name(topic.CustomName ?? LabelOf(rows) ?? $"Topic {number}");
                current = Ended(topic) ? null : topic;
                continue;
            }
            row.Topic = i < tail ? current : null;
        }
        var headers = _items.OfType<ChatTopicItem>().ToList();
        if (!All.SequenceEqual(headers)) {
            All.Clear();
            All.AddRange(headers);
        }
        Any = headers.Count > 0;
        CanEnd = current is not null;
        Save();
        Laid?.Invoke();
    }

    void Save() {
        if (_store is null || _feed is null) return;
        var marks = new Dictionary<ChatMessageKey, ChatTopicMark>(_restoring);
        ChatItemViewModel? mark = null;
        foreach (var row in Everything()) {
            if (row is ChatTopicItem or ChatTopicEndItem) { mark = row; continue; }
            // A topic made at a row from no feed lasts as long as the tab.
            if (row.Key is { } key) {
                if (mark is ChatTopicItem header)
                    marks[key] = new(!header.IsExpanded || _folding.Contains(header), header.CustomName, Anchor: AnchorOf(row));
                else if (mark is ChatTopicEndItem)
                    marks[key] = new(false, null, End: true, Anchor: AnchorOf(row));
            }
            mark = null;
        }
        _store.SetTopics(_feed, marks);
    }

    static bool IsMessage(ChatItemViewModel row) => row is UserTurnItem or AssistantTextItem;

    const int AnchorLength = 32;

    /// The start of a message's text, whitespace collapsed: what a saved mark checks its message by.
    public static string AnchorOf(ChatItemViewModel row) {
        var text = row switch { UserTurnItem turn => turn.Text, AssistantTextItem reply => reply.Text, _ => "" };
        var words = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return words.Length <= AnchorLength ? words : words[..AnchorLength];
    }

    ChatTopicItem? TopicBefore(int index) {
        for (var i = index - 1; i >= 0; i--)
            if (_items[i] is ChatTopicItem topic) return topic;
        return null;
    }

    // A folded topic holds its end, if it has one.
    static bool Ended(ChatTopicItem topic) => !topic.IsExpanded && topic.Hidden is [.., ChatTopicEndItem];

    // The end of a topic's rows, an end of its own included, so folding takes the end in too. Marks
    // still waiting for their message stay out: they move once it lands.
    int EndOf(int header) {
        var tail = TailStart();
        foreach (var mark in _pending)
            if (_items.IndexOf(mark) is var at && at > header && at < tail) tail = at;
        for (var i = header + 1; i < tail; i++) {
            if (_items[i] is ChatTopicItem) return i;
            if (_items[i] is ChatTopicEndItem) return i + 1;
        }
        return Math.Max(header + 1, tail);
    }

    // Pending cards stay pinned to the end of the chat, so a topic never takes them in.
    int TailStart() {
        var start = _items.Count;
        while (start > 0 && _items[start - 1] is PendingCardItem) start--;
        return start;
    }

    // A topic is named by the first thing the user said in it, or failing that, the first thing said.
    static string? LabelOf(IReadOnlyList<ChatItemViewModel> rows) {
        // The message the topic starts at, whoever wrote it.
        var text = rows.Select(static row => row switch {
            UserTurnItem { HasText: true } turn => turn.Text,
            AssistantTextItem reply => reply.Text,
            _ => null,
        }).FirstOrDefault(static t => t is not null);
        var line = text?.Split('\n').Select(static l => l.Trim()).FirstOrDefault(static l => l.Length > 0);
        return line is null ? null : TextElision.End(line, LabelLength);
    }

    static string SummaryOf(List<ChatItemViewModel> rows) {
        var messages = rows.Count(IsMessage);
        var tools    = rows.OfType<ToolGroupItem>().Sum(static group => group.Calls.Count);
        var parts    = new List<string>(2) { Count(messages, "message") };
        if (tools > 0) parts.Add(Count(tools, "tool call"));
        return string.Join(" · ", parts);
    }

    static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
