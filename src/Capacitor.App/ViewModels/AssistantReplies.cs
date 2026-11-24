using System.Collections.Specialized;

namespace Capacitor.App.ViewModels;

/// Groups the assistant messages between two prompts into one reply. Tool calls, thinking and system
/// notes sit inside a reply without splitting it, and stay out of its copied text. A folded topic's
/// rows still belong to their replies, so folding never changes what a reply copies.
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
        var rows = new List<ChatItemViewModel>(items.Count);
        int from = 0, to = 0;
        for (var i = 0; i < items.Count; i++) {
            if (i == start) from = rows.Count;
            rows.Add(items[i]);
            if (items[i] is ChatTopicItem { IsExpanded: false } folded) rows.AddRange(folded.Hidden);
            if (i == end) to = rows.Count - 1;
        }
        Refresh(rows, from, to);
    }

    /// Regroups every reply, folded rows included, in chat order.
    public static void RefreshAll(IEnumerable<ChatItemViewModel> rows) {
        var all = rows as IReadOnlyList<ChatItemViewModel> ?? rows.ToList();
        if (all.Count > 0) Refresh(all, 0, all.Count - 1);
    }

    static void Refresh(IReadOnlyList<ChatItemViewModel> rows, int start, int end) {
        while (start > 0 && rows[start - 1] is not UserTurnItem) start--;
        while (end < rows.Count - 1 && rows[end + 1] is not UserTurnItem) end++;
        var reply = new List<AssistantTextItem>();
        for (var i = start; i <= end + 1; i++) {
            if (i <= end && rows[i] is not UserTurnItem) {
                if (rows[i] is AssistantTextItem message) reply.Add(message);
                continue;
            }
            Settle(reply);
            reply.Clear();
        }
    }

    static void Settle(List<AssistantTextItem> reply) {
        if (reply.Count == 0) return;
        for (var i = 0; i < reply.Count - 1; i++) {
            reply[i].EndsReply = false;
            reply[i].ReplyText = reply[i].Text;
        }
        reply[^1].EndsReply = true;
        reply[^1].ReplyText = string.Join("\n\n", reply.Where(static message => !string.IsNullOrWhiteSpace(message.Text)).Select(static message => message.Text));
    }
}
