using System.Collections.Specialized;

namespace Capacitor.App.ViewModels;

/// Groups the rows of one agent turn into a reply. A turn starts at a prompt or at a note the agent
/// answers on its own and runs to the next one; its tool calls, thinking and other notes belong to it,
/// but only its messages are copied. A folded topic's rows still belong to their replies, so folding
/// never changes what a reply copies, though the reply then ends at its last row still shown.
static class AssistantReplies {
    public static void Refresh(IReadOnlyList<ChatItemViewModel> items, NotifyCollectionChangedEventArgs change) {
        if (items.Count == 0) return;
        var (start, end) = change.Action switch {
            NotifyCollectionChangedAction.Reset => (0, items.Count - 1),
            NotifyCollectionChangedAction.Remove => (change.OldStartingIndex - 1, change.OldStartingIndex),
            NotifyCollectionChangedAction.Move => (Math.Min(change.OldStartingIndex, change.NewStartingIndex) - 1,
                Math.Max(change.OldStartingIndex, change.NewStartingIndex) + (change.NewItems?.Count ?? 1)),
            _ => (change.NewStartingIndex, change.NewStartingIndex + (change.NewItems?.Count ?? 1) - 1),
        };
        start = Math.Clamp(start, 0, items.Count - 1);
        end = Math.Clamp(end, 0, items.Count - 1);
        var rows = new List<(ChatItemViewModel Row, bool Shown)>(items.Count);
        int from = 0, to = 0;
        for (var i = 0; i < items.Count; i++) {
            if (i == start) from = rows.Count;
            rows.Add((items[i], true));
            if (items[i] is ChatTopicItem { IsExpanded: false } folded)
                foreach (var hidden in folded.Hidden) rows.Add((hidden, false));
            if (i == end) to = rows.Count - 1;
        }
        Refresh(rows, from, to);
    }

    /// Regroups every reply in a list of shown rows and the folded rows each folded topic holds.
    public static void RefreshAll(IReadOnlyList<ChatItemViewModel> items) =>
        Refresh(items, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

    static void Refresh(List<(ChatItemViewModel Row, bool Shown)> rows, int start, int end) {
        while (start > 0 && !StartsReply(rows[start - 1].Row)) start--;
        while (end < rows.Count - 1 && !StartsReply(rows[end + 1].Row)) end++;
        var used = new HashSet<AssistantReply>(ReferenceEqualityComparer.Instance);
        var reply = new List<(ChatItemViewModel Row, bool Shown)>();
        for (var i = start; i <= end + 1; i++) {
            if (i <= end && !StartsReply(rows[i].Row)) {
                reply.Add(rows[i]);
                continue;
            }
            if (i <= end) Clear(rows[i].Row);
            Settle(reply, used);
            reply.Clear();
        }
    }

    static bool StartsReply(ChatItemViewModel row) => row is UserTurnItem or SystemNoteItem { StartsTurn: true };

    // Its time and Copy follow a row that reads as part of the turn, not a question waiting on the user
    // or a topic's header or end.
    static bool CanEnd(ChatItemViewModel row) => row is not (PendingCardItem or ChatTopicItem or ChatTopicEndItem);

    static void Settle(List<(ChatItemViewModel Row, bool Shown)> rows, HashSet<AssistantReply> used) {
        var messages = rows.Select(static entry => entry.Row).OfType<AssistantTextItem>()
            .Where(static message => !string.IsNullOrWhiteSpace(message.Text)).ToList();
        var last = rows.FindLastIndex(static entry => entry.Shown && CanEnd(entry.Row));
        if (messages.Count == 0 || last < 0) {
            foreach (var (row, _) in rows) Clear(row);
            return;
        }
        var reply = rows.Select(static entry => entry.Row.Reply).FirstOrDefault(candidate => candidate is not null && !used.Contains(candidate))
            ?? new AssistantReply();
        used.Add(reply);
        reply.Text = string.Join("\n\n", messages.Select(static message => message.Text));
        reply.Time = messages[^1].Time;
        for (var i = 0; i < rows.Count; i++) {
            rows[i].Row.Reply = reply;
            rows[i].Row.EndsReply = i == last;
        }
    }

    static void Clear(ChatItemViewModel row) {
        row.Reply = null;
        row.EndsReply = false;
    }
}
