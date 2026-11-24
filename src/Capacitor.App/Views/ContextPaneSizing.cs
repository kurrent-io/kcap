using Avalonia.Controls;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

/// Sizes a session view's work-context column from its window's <see cref="PaneLayout"/>. Outside a
/// <see cref="MainWindow"/> the column keeps its minimum width and neither button shows, since
/// nothing could bring a hidden pane back. Inside one, a view too narrow for the pane and a chat
/// column hides the pane until it is wide again (<see cref="ContextPaneFit"/>).
public static class ContextPaneSizing {
    public static void Attach(Control view, ColumnDefinition column, Control pane, WorkContextView content,
            GridSplitter splitter, Button showButton) {
        PaneLayout? panes = null;
        MainWindow? window = null;
        var fit = new ContextPaneFit();

        void Apply() {
            var inZen = window?.IsInZenMode == true;
            var collapsed = inZen || panes is not null && (panes.ContextCollapsed || fit.HidesPane);
            column.MinWidth = panes is null ? PaneLayout.ContextMinWidth : 0;
            column.Width = new GridLength(collapsed ? 0 : panes?.ContextWidth ?? PaneLayout.ContextMinWidth);
            pane.IsVisible = !collapsed;
            splitter.IsVisible = !collapsed;
            showButton.IsVisible = collapsed && !inZen;
            content.HideButton.IsVisible = panes is not null;
        }

        void OnChanged(object? sender, EventArgs e) => Apply();

        void OnZenModeChanged(object? sender, EventArgs e) {
            if (window is { IsInZenMode: false }) fit.Resize(window.WorkspaceWidthWithRail);
            Apply();
        }

        view.AttachedToVisualTree += (_, _) => {
            window = TopLevel.GetTopLevel(view) as MainWindow;
            panes = window?.Panes;
            if (window is not null) window.ZenModeChanged += OnZenModeChanged;
            if (panes is not null) panes.Changed += OnChanged;
            Apply();
        };
        view.DetachedFromVisualTree += (_, _) => {
            if (panes is not null) panes.Changed -= OnChanged;
            if (window is not null) window.ZenModeChanged -= OnZenModeChanged;
            panes = null;
            window = null;
        };
        view.SizeChanged += (_, e) => {
            column.MaxWidth = PaneLayout.MaxWidth(e.NewSize.Width, PaneLayout.ContextMinWidth, PaneLayout.SessionMinWidth);
            if (window?.IsInZenMode != true && fit.Resize(e.NewSize.Width)) Apply();
        };
        splitter.DragDelta += (_, _) =>
            pane.Opacity = panes is not null && PaneLayout.HidesAt(column.ActualWidth, PaneLayout.ContextMinWidth)
                ? PaneLayout.HidingOpacity
                : 1;
        // Released below the minimum, the pane hides and reopens at the minimum.
        splitter.DragCompleted += (_, _) => {
            pane.Opacity = 1;
            if (panes is null) return;
            var hides = PaneLayout.HidesAt(column.ActualWidth, PaneLayout.ContextMinWidth);
            panes.ContextWidth = hides ? PaneLayout.ContextMinWidth : column.ActualWidth;
            if (hides) panes.ContextCollapsed = true;
        };
        content.HideRequested += (_, _) => {
            fit.Hidden();
            if (panes is not null) panes.ContextCollapsed = true;
        };
        showButton.Click += (_, _) => {
            if (panes is null) return;
            fit.ShowAnyway();
            if (panes.ContextCollapsed) panes.ContextCollapsed = false;
            else Apply();
        };
        Apply();
    }
}
