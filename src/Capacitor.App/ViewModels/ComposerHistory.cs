namespace Capacitor.App.ViewModels;

/// Prompts recalled with ↑/↓ the way a shell recalls commands: the input the transcript records
/// as submitted, followed by the sends it has not echoed yet. Recall continues only while the box
/// still shows the recalled text unchanged: an edit turns it back into the user's own draft.
///
/// The draft a recall replaced stays stashed until it is handed back, so it is never lost. There is
/// one stash, so while it holds text a second non-blank draft (an edited recall) refuses recall
/// in both directions; clearing the box lets ↑ walk again and ↓ bring the stash back. A
/// whitespace-only box counts as blank: recall discards it rather than stash it.
public sealed class ComposerHistory {
    public const int Capacity = 100;

    readonly List<string> _entries = new();
    // Where the shown entry stood. Arrivals, evictions and replays move entries between keystrokes,
    // so each step finds the shown text again from here instead of trusting the index.
    int _cursor;
    string? _shown;
    string _draft = "";

    /// Input the transcript records as submitted, in transcript order.
    public void Observe(string text) {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_entries.Count > 0 && string.Equals(_entries[^1], text, StringComparison.Ordinal)) return;
        _entries.Add(text);
        if (_entries.Count <= Capacity) return;
        _entries.RemoveAt(0);
        _cursor--;
    }

    /// The chat now reads another transcript.
    public void Clear() {
        _entries.Clear();
        EndNavigation();
    }

    /// The transcript is replaying from the start; a recall on screen picks up again once its
    /// turn is replayed.
    public void ClearForReplay() => _entries.Clear();

    /// The next older prompt, or null when there is none or `current` is a second draft.
    public string? Older(string current, IReadOnlyList<string> unechoed) {
        var all = All(unechoed);
        var showing = IsShowing(current);
        if (!showing && IsSecondDraft(current)) return null;
        var target = showing ? Locate(all).Index - 1 : all.Count - 1;
        if (target < 0) return null;
        if (!showing && !HoldsDraft) _draft = Blank(current) ? "" : current;
        _cursor = target;
        return _shown = all[target];
    }

    /// The next newer prompt, the stashed draft once past the newest or over a blank box, or null.
    public string? Newer(string current, IReadOnlyList<string> unechoed) {
        if (!IsShowing(current)) return HoldsDraft && Blank(current) ? TakeDraft() : null;
        var all = All(unechoed);
        var (index, found) = Locate(all);
        var target = found ? index + 1 : index;
        if (target < all.Count) {
            _cursor = target;
            return _shown = all[target];
        }
        return TakeDraft();
    }

    /// Whether `current` is a recall still on screen.
    public bool IsShowing(string current) => _shown is not null && string.Equals(_shown, current, StringComparison.Ordinal);

    /// Ends navigation and hands back the stashed draft, which the caller puts in the box.
    public string TakeDraft() {
        var draft = _draft;
        _draft = "";
        EndNavigation();
        return draft;
    }

    /// A stash a previous holder of this composer left behind, held as if a recall had replaced it.
    public void Stash(string draft) {
        if (!HoldsDraft && !Blank(draft)) _draft = draft;
    }

    /// Leaves whatever the composer shows as the user's own draft.
    public void EndNavigation() => _shown = null;

    bool HoldsDraft => _draft.Length > 0;

    static bool Blank(string text) => string.IsNullOrWhiteSpace(text);

    bool IsSecondDraft(string current) =>
        HoldsDraft && !Blank(current) && !string.Equals(current, _draft, StringComparison.Ordinal);

    List<string> All(IReadOnlyList<string> unechoed) {
        var all = new List<string>(_entries.Count + unechoed.Count);
        all.AddRange(_entries);
        all.AddRange(unechoed);
        return all;
    }

    /// The shown entry's index nearest where it stood; when it is gone, the slot it left, which
    /// is the index of the entry just newer than it.
    (int Index, bool Found) Locate(List<string> all) {
        var start = Math.Clamp(_cursor, 0, all.Count);
        for (var distance = 0; distance <= all.Count; distance++) {
            foreach (var i in (ReadOnlySpan<int>)[start - distance, start + distance]) {
                if (i >= 0 && i < all.Count && string.Equals(all[i], _shown, StringComparison.Ordinal)) return (i, true);
            }
        }
        return (start, false);
    }
}
