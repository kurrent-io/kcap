using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The session rail. DataContext is the window's own MainWindowViewModel, inherited — this view
/// never builds or assigns one.
public partial class SessionRailView : UserControl {
    readonly SerialDisposable _searchLabel = new();

    public SessionRailView() {
        InitializeComponent();
        DataContextChanged += (_, _) => BindSearchLabel();
        foreach (var anchor in new[] { RailHelpButton, RailGroupButton, RailSortButton }) KeyboardDismissible(anchor);
    }

    // A pointer-opened flyout leaves focus on its button, outside the popup, where Escape never reaches it.
    static void KeyboardDismissible(Button anchor) {
        if (anchor.Flyout is not Flyout { Content: Control content } flyout) return;

        flyout.Opened += (_, _) => FocusFirstItem(content, NavigationMethod.Tab);
        content.AddHandler(KeyDownEvent, (_, e) => DismissOnEscape(flyout, anchor, e, NavigationMethod.Tab), RoutingStrategies.Bubble);
    }

    static void FocusFirstItem(Control content, NavigationMethod method) =>
        content.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(b => b.IsEffectivelyVisible && b.IsEffectivelyEnabled)?.Focus(method);

    static void DismissOnEscape(FlyoutBase flyout, Control anchor, KeyEventArgs e, NavigationMethod refocus) {
        if (e.Key != Key.Escape || e.Handled) return;
        e.Handled = true;
        flyout.Hide();
        anchor.Focus(refocus);
    }

    void OnRowMenuOpened(object? sender, EventArgs e) {
        // Pointer-opened: take focus for Escape without drawing the keyboard focus ring.
        if (sender is Flyout { Content: Control content }) FocusFirstItem(content, NavigationMethod.Pointer);
    }

    void OnRowMenuKeyDown(object? sender, KeyEventArgs e) {
        if (sender is Control item && RowMenuOf(item) is ({ } row, { } flyout)) DismissOnEscape(flyout, row, e, NavigationMethod.Pointer);
    }

    void OnRowMenuItemClick(object? sender, RoutedEventArgs e) {
        if (sender is not Button { Command: { } command } item || !command.CanExecute(null)) return;
        if (RowMenuOf(item) is (_, { } flyout)) flyout.Hide();
        command.Execute(null);
        e.Handled = true;
    }

    static (Control? Row, FlyoutBase? Flyout) RowMenuOf(Control item) =>
        item.FindLogicalAncestorOfType<Popup>()?.PlacementTarget is Control row ? (row, row.ContextFlyout) : (null, null);

    // The field shows the page's query while the page is up, and its placeholder otherwise.
    void BindSearchLabel() {
        if (DataContext is not MainWindowViewModel vm) {
            _searchLabel.Disposable = null;
            ShowSearchLabel(null);
            return;
        }
        var query = vm.Search is { } search ? search.WhenAnyValue(x => x.Query) : Observable.Return("");
        _searchLabel.Disposable = vm.WhenAnyValue(x => x.IsSearchOpen)
            .CombineLatest(query, (open, text) => open && text.Length > 0 ? text : null)
            .Subscribe(ShowSearchLabel);
    }

    void ShowSearchLabel(string? query) {
        RailSearchText.Text = query ?? "Search…";
        RailSearchText.Classes.Set("query", query is not null);
        RailSearchShortcut.IsVisible = query is null;
    }

    // The rail's 44px chrome row IS the title bar on this surface — see WindowChrome.
    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);


    // Button.OnClick raises ClickEvent before reading Command, and Flyout.Hide detaches the popup
    // parent and clears inherited DataContext on its rows, so capture and run Command here.
    void OnHelpFlyoutItemClick(object? sender, RoutedEventArgs e) => RunFlyoutItem(RailHelpButton, sender, e);

    void OnGroupPickerItemClick(object? sender, RoutedEventArgs e) => RunFlyoutItem(RailGroupButton, sender, e);

    void OnSortPickerItemClick(object? sender, RoutedEventArgs e) => RunFlyoutItem(RailSortButton, sender, e);

    void OnProjectPickerItemClick(object? sender, RoutedEventArgs e) => RunFlyoutItem(RailProjectButton, sender, e);

    static void RunFlyoutItem(Button anchor, object? sender, RoutedEventArgs e) {
        if (sender is not Button { Command: { } command, CommandParameter: var parameter }
            || !command.CanExecute(parameter)) return;
        anchor.Flyout?.Hide();
        command.Execute(parameter);
        e.Handled = true;
    }

    AboutWindow? _about;

    void OnAboutClick(object? sender, RoutedEventArgs e) {
        RailHelpButton.Flyout?.Hide();
        if (_about is { } open) {
            open.Activate();
            return;
        }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        _about = new AboutWindow { DataContext = DataContext };
        _about.Closed += (_, _) => _about = null;
        _about.Show(owner);
    }
}
