namespace Capacitor.App.Services;

/// Whether a session view has room for its work-context pane beside a chat column of at least
/// <see cref="PaneLayout.SessionMinWidth"/>. Without room the pane hides on its own and comes back
/// once there is room, leaving the user's own collapsed setting untouched. Showing it while there is
/// no room holds until the view next crosses between narrow and wide.
public sealed class ContextPaneFit {
    public static double RoomNeeded => PaneLayout.ContextMinWidth + PaneLayout.SessionMinWidth;

    bool _narrow;
    bool _shownAnyway;

    public bool IsNarrow => _narrow;

    /// True when the pane should be hidden for lack of room.
    public bool HidesPane => _narrow && !_shownAnyway;

    /// Returns true when the answer to <see cref="HidesPane"/> changed.
    public bool Resize(double viewWidth) {
        var before = HidesPane;
        var narrow = viewWidth > 0 && viewWidth < RoomNeeded;
        if (narrow != _narrow) {
            _narrow = narrow;
            _shownAnyway = false;
        }
        return before != HidesPane;
    }

    /// The user asked for the pane: it shows even without room, until the next crossing.
    public void ShowAnyway() => _shownAnyway = _narrow;

    /// The user hid the pane: there is nothing left to show anyway.
    public void Hidden() => _shownAnyway = false;
}
