using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Keyboard movement inside the session rail's list, which is a single Tab stop: Up and Down step
/// through group headers and rows, Home and End jump to either end, Left folds a group (or moves from a
/// row to its header, or from a PR link to its row), Right unfolds a group or reaches a row's PR link.
/// Enter and Space are the buttons' own. Tabbing in lands on the open session's row when it shows.
public static class RailKeyboard {
    const string Header = "railRepoHeader";
    const string Row = "railRow";
    const string PullRequest = "railPr";

    public static void Attach(ItemsControl list) {
        list.AddHandler(InputElement.KeyDownEvent, (_, e) => OnKeyDown(list, e), RoutingStrategies.Bubble);
        list.AddHandler(InputElement.GotFocusEvent, (_, e) => OnGotFocus(list, e), RoutingStrategies.Bubble);
    }

    static List<Button> Stops(ItemsControl list) => list.GetVisualDescendants().OfType<Button>()
        .Where(b => (b.Classes.Contains(Header) || b.Classes.Contains(Row)) && b.IsEffectivelyVisible && b.IsEffectivelyEnabled)
        .ToList();

    static void OnGotFocus(ItemsControl list, FocusChangedEventArgs e) {
        if (e.NavigationMethod != NavigationMethod.Tab || e.Source is not Button entered) return;
        var selected = Stops(list).FirstOrDefault(b => b.Classes.Contains(Row) && b.DataContext is RailSessionViewModel { IsSelected: true });
        if (selected is not null && !ReferenceEquals(selected, entered)) selected.Focus(NavigationMethod.Tab);
    }

    static void OnKeyDown(ItemsControl list, KeyEventArgs e) {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None) return;
        if (TopLevel.GetTopLevel(list)?.FocusManager?.GetFocusedElement() is not Button focused) return;
        var stops = Stops(list);
        var isLink = focused.Classes.Contains(PullRequest);
        var row = isLink ? RowOf(stops, focused) : focused;
        var at = row is null ? -1 : stops.IndexOf(row);
        if (at < 0) return;

        Button? target = e.Key switch {
            Key.Down => at + 1 < stops.Count ? stops[at + 1] : null,
            Key.Up => at > 0 ? stops[at - 1] : null,
            Key.Home => stops[0],
            Key.End => stops[^1],
            Key.Left when isLink => row,
            Key.Left when Fold(list, focused, expand: false) => focused,
            Key.Left when focused.Classes.Contains(Row) => stops.Take(at).LastOrDefault(b => b.Classes.Contains(Header)),
            Key.Right when Fold(list, focused, expand: true) => focused,
            Key.Right when focused.Classes.Contains(Row) => LinkOf(focused),
            _ => null,
        };
        if (target is null) {
            // An arrow at either end, or onto nothing, still belongs to the rail rather than the scroller.
            e.Handled = e.Key is Key.Up or Key.Down or Key.Left or Key.Right;
            return;
        }
        if (!ReferenceEquals(target, focused)) target.Focus(NavigationMethod.Directional);
        target.BringIntoView();
        e.Handled = true;
    }

    // Folds or unfolds the focused group header; false when it is not a header or already in that state.
    // Folding rebuilds the header, so focus is put back on the group's new header once layout has run.
    static bool Fold(ItemsControl list, Button focused, bool expand) {
        if (!focused.Classes.Contains(Header) || focused.DataContext is not RailGroupViewModel group || group.IsExpanded == expand) return false;
        group.ToggleCommand.Execute().Subscribe();
        Dispatcher.UIThread.Post(() => {
            if (Stops(list).FirstOrDefault(b => b.Classes.Contains(Header) && ReferenceEquals(b.DataContext, group)) is { IsFocused: false } header)
                header.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Loaded);
        return true;
    }

    // The row and its PR link share the session's container.
    static Button? LinkOf(Button row) => row.GetVisualParent()?.GetVisualChildren().OfType<Button>()
        .FirstOrDefault(b => b.Classes.Contains(PullRequest) && b.IsEffectivelyVisible);

    static Button? RowOf(List<Button> stops, Button link) => link.GetVisualParent()?.GetVisualChildren().OfType<Button>()
        .FirstOrDefault(stops.Contains);
}
