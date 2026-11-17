using System.Reactive;
using Avalonia.Collections;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Topics split the chat at the messages the user marks as a change of topic. Each runs from its marked
/// message to the next one, or to the end of the chat, and folds into the header above that message.
/// View state only: nothing reaches the agent, and the marks outlive the tab only in <see cref="ChatTopicMarks"/>.
public sealed class ChatTopics : ReactiveObject {
    const int LabelLength = 72;

    readonly AvaloniaList<ChatItemViewModel> _items;
    readonly ChatTopicMarks? _store;
    string? _feed;
    // Saved marks whose message has not been replayed yet.
    Dictionary<int, ChatTopicMark> _restoring = [];
    // Restored topics saved as folded, folded once the next topic bounds them: until then more of
    // their rows may still be on the way. The last topic stays open, where new activity lands.
    readonly HashSet<ChatTopicItem> _folding = [];
    bool _editing;
    // Until a topic exists, the list's changes need no laying out.
    bool _used;

    public ChatTopics(AvaloniaList<ChatItemViewModel> items, ChatTopicMarks? store = null) {
        _items = items;
        _store = store;
        ToggleChangeCommand = ReactiveCommand.Create<ChatItemViewModel>(ToggleChange);
        RevealCommand = ReactiveCommand.Create<ChatTopicItem>(Reveal);
        _items.CollectionChanged += (_, _) => {
            if (_used && !_editing) Lay();
        };
    }

    public ReactiveCommand<ChatItemViewModel, Unit> ToggleChangeCommand { get; }

    /// Asks the chat to bring a topic's header into view.
    public ReactiveCommand<ChatTopicItem, Unit> RevealCommand { get; }

    /// Raised when a topic is to be shown. The chat view is detached while another tab shows, so the
    /// request also waits in <see cref="TakeReveal"/> until a view takes it.
    public event Action? Revealing;

    ChatTopicItem? _reveal;
    public ChatTopicItem? TakeReveal() {
        var topic = _reveal;
        _reveal = null;
        return topic;
    }

    /// Every topic's header, in chat order.
    public AvaloniaList<ChatTopicItem> All { get; } = [];

    bool _any;
    public bool Any { get => _any; private set => this.RaiseAndSetIfChanged(ref _any, value); }

    /// Starts a topic at the end of the chat, for the message about to be sent.
    public ChatTopicItem Start() {
        var tail = TailStart();
        if (tail > 0 && _items[tail - 1] is ChatTopicItem waiting) return waiting;
        var topic = NewTopic();
        Edit(() => _items.Insert(tail, topic));
        return topic;
    }

    /// Takes back a topic started for a message that never went out, if nothing has landed in it since.
    public void Discard(ChatTopicItem topic) => Edit(() => {
        var index = _items.IndexOf(topic);
        if (index >= 0 && index + 1 >= TailStart()) _items.RemoveAt(index);
    });

    /// Marks <paramref name="row"/> as where a new topic starts, or unmarks it, which joins its topic to
    /// the one before.
    public void ToggleChange(ChatItemViewModel row) => Edit(() => {
        var index = _items.IndexOf(row);
        if (index < 0 || index >= TailStart() || row is ChatTopicItem) return;
        if (index > 0 && _items[index - 1] is ChatTopicItem topic) {
            if (TopicBefore(index - 1) is { IsExpanded: false } previous) Unfold(previous);
            _items.Remove(topic);
        } else {
            _items.Insert(index, NewTopic());
        }
    });

    /// The feed is replaying from scratch: every topic is rebuilt from the marks saved for it, as
    /// its message comes back.
    public void Reset(string? feed) {
        _used = false;
        _feed = feed;
        _folding.Clear();
        All.Clear();
        Any = false;
        _restoring = feed is not null && _store is not null ? new(_store.Get(feed)) : [];
    }

    /// Puts back the saved topics whose message has been replayed since the last call. Called once a
    /// batch of rows has landed, never from inside the list's own change notification.
    public void Restore() {
        if (_restoring.Count == 0 && _folding.Count == 0) return;
        var found = new List<(ChatItemViewModel Row, ChatTopicMark Mark)>();
        var tail = TailStart();
        var ordinal = 0;
        for (var i = 0; i < _items.Count; i++) {
            var row = _items[i];
            if (row is ChatTopicItem { IsExpanded: false } folded) ordinal += folded.Hidden.Count(IsMessage);
            if (!IsMessage(row)) continue;
            if (i < tail && _restoring.Remove(ordinal, out var mark)) found.Add((row, mark));
            ordinal++;
        }
        Edit(() => {
            foreach (var (row, mark) in found) {
                var index = _items.IndexOf(row);
                if (index > 0 && _items[index - 1] is ChatTopicItem) continue;
                var topic = NewTopic();
                topic.CustomName = mark.Name;
                _items.Insert(index, topic);
                if (mark.Folded) _folding.Add(topic);
            }
            foreach (var topic in _folding.ToList()) {
                var index = _items.IndexOf(topic);
                if (index < 0 || !topic.IsExpanded) { _folding.Remove(topic); continue; }
                var end = EndOf(index);
                if (end >= _items.Count || _items[end] is not ChatTopicItem) continue;
                _folding.Remove(topic);
                Toggle(topic);
            }
        });
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

    void Reveal(ChatTopicItem topic) {
        if (!_items.Contains(topic)) return;
        _reveal = topic;
        Revealing?.Invoke();
    }

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
        try { edit(); }
        finally { _editing = false; }
        Lay();
    }

    void Lay() {
        ChatTopicItem? current = null;
        var tail = TailStart();
        var number = 0;
        for (var i = 0; i < _items.Count; i++) {
            var row = _items[i];
            row.StartsTopic = i > 0 && _items[i - 1] is ChatTopicItem && row is not ChatTopicItem;
            if (row is ChatTopicItem topic) {
                row.Topic = null;
                var rows = topic.IsExpanded ? _items.Skip(i + 1).Take(EndOf(i) - i - 1).ToList() : topic.Hidden;
                number++;
                topic.Name(topic.CustomName ?? LabelOf(rows) ?? $"Topic {number}");
                current = topic;
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
        Save();
    }

    void Save() {
        if (_store is null || _feed is null) return;
        var marks = new Dictionary<int, ChatTopicMark>(_restoring);
        ChatTopicItem? header = null;
        var ordinal = 0;
        foreach (var row in Everything()) {
            if (row is ChatTopicItem topic) { header = topic; continue; }
            if (IsMessage(row)) {
                if (header is not null)
                    marks[ordinal] = new(!header.IsExpanded || _folding.Contains(header), header.CustomName);
                ordinal++;
            }
            header = null;
        }
        _store.Set(_feed, marks);
    }

    static bool IsMessage(ChatItemViewModel row) => row is UserTurnItem or AssistantTextItem;

    ChatTopicItem? TopicBefore(int index) {
        for (var i = index - 1; i >= 0; i--)
            if (_items[i] is ChatTopicItem topic) return topic;
        return null;
    }

    int EndOf(int header) {
        var tail = TailStart();
        for (var i = header + 1; i < tail; i++)
            if (_items[i] is ChatTopicItem) return i;
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
        var text = rows.OfType<UserTurnItem>().FirstOrDefault(static turn => turn.HasText)?.Text
            ?? rows.OfType<AssistantTextItem>().FirstOrDefault()?.Text;
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
