namespace Capacitor.App.Services;

/// The window's side panes, the session rail on the left and the work-context pane every session
/// view shows on the right, and the width of the chat column between them. One instance per window,
/// so each retained session view follows it.
public sealed class PaneLayout {
    public const double RailMinWidth = 330;
    public const double ContextMinWidth = 320;

    // What the rail leaves the content beside it: the launcher's 680 composer and its margins.
    public const double ContentMinWidth = 720;

    // What the work-context pane leaves a session: its header's view switch and actions beside a title.
    public const double SessionMinWidth = 600;

    /// A pane may grow until the rest of the space it shares is down to restMin, which also keeps its
    /// splitter on screen, but never below its own minimum.
    public static double MaxWidth(double shared, double min, double restMin) => Math.Max(min, shared - restMin);

    // How a pane dragged below its minimum looks until it is released and hides.
    public const double HidingOpacity = 0.4;

    // The chat column's share of the chat's width, and the narrowest a drag makes it.
    public const double ChatColumnDefaultFraction = 0.75;
    public const double ChatColumnMinWidth = 564;
    // The agent's lane's share of the column, and the narrowest a drag makes it.
    public const double ChatLaneDefaultFraction = 0.85;
    public const double ChatLaneMinWidth = 320;

    // Layout rounding can lay a pane at its minimum out a fraction below it; a click on its splitter
    // must not hide it.
    const double HideTolerance = 1;

    public static bool HidesAt(double width, double min) => width < min - HideTolerance;

    double _railWidth = RailMinWidth;
    bool _railCollapsed;
    double _contextWidth = ContextMinWidth;
    bool _contextCollapsed;
    double _chatColumnFraction = ChatColumnDefaultFraction;
    double _chatLaneFraction = ChatLaneDefaultFraction;

    public double RailWidth { get => _railWidth; set => Set(ref _railWidth, value); }
    public bool RailCollapsed { get => _railCollapsed; set => Set(ref _railCollapsed, value); }
    public double ContextWidth { get => _contextWidth; set => Set(ref _contextWidth, value); }
    public bool ContextCollapsed { get => _contextCollapsed; set => Set(ref _contextCollapsed, value); }
    public double ChatColumnFraction { get => _chatColumnFraction; set => Set(ref _chatColumnFraction, value); }
    public double ChatLaneFraction { get => _chatLaneFraction; set => Set(ref _chatLaneFraction, value); }

    /// What the rail leaves beside it: the launcher's minimum, or a session's beside the open work-context pane.
    public double RailRestMinWidth =>
        ContextCollapsed ? ContentMinWidth : Math.Max(ContentMinWidth, ContextMinWidth + SessionMinWidth);

    public event EventHandler? Changed;

    void Set<T>(ref T field, T value) {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
