using Avalonia.Controls;
using Avalonia.Input;
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
        var fit = new ContextPaneFit();

        void Apply() {
            var collapsed = panes is not null && (panes.ContextCollapsed || fit.HidesPane);
            var focusShow = collapsed && (pane.IsKeyboardFocusWithin || splitter.IsFocused);
            var focusHide = !collapsed && showButton.IsFocused;
            column.MinWidth = panes is null ? PaneLayout.ContextMinWidth : 0;
            column.Width = new GridLength(collapsed ? 0 : panes?.ContextWidth ?? PaneLayout.ContextMinWidth);
            pane.IsVisible = !collapsed;
            splitter.IsVisible = !collapsed;
            showButton.IsVisible = collapsed;
            content.HideButton.IsVisible = panes is not null;
            if (focusShow && showButton.IsEffectivelyVisible) showButton.Focus(NavigationMethod.Tab);
            if (focusHide) content.HideButton.Focus(NavigationMethod.Tab);
        }

        void OnChanged(object? sender, EventArgs e) => Apply();

        view.AttachedToVisualTree += (_, _) => {
            panes = (TopLevel.GetTopLevel(view) as MainWindow)?.Panes;
            if (panes is not null) panes.Changed += OnChanged;
            Apply();
        };
        view.DetachedFromVisualTree += (_, _) => {
            if (panes is not null) panes.Changed -= OnChanged;
            panes = null;
        };
        view.SizeChanged += (_, e) => {
            column.MaxWidth = PaneLayout.MaxWidth(e.NewSize.Width, PaneLayout.ContextMinWidth, PaneLayout.SessionMinWidth);
            if (fit.Resize(e.NewSize.Width)) Apply();
        };
        splitter.DragDelta += (_, _) =>
            pane.Opacity = panes is not null && PaneLayout.HidesAt(column.ActualWidth, PaneLayout.ContextMinWidth)
                ? PaneLayout.HidingOpacity
                : 1;
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
