using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Capacitor.App.ViewModels.Pages;

namespace Capacitor.App.Views.Pages;

/// A work item's page drawn over the main area. DataContext is its WorkItemPageViewModel.
public partial class WorkItemPageView : UserControl {
    public WorkItemPageView() {
        InitializeComponent();
        ContextPaneSizing.Attach(this, SplitGrid.ColumnDefinitions[1], DetailsPane, HideButton, ContextSplitter, ContextShowButton);
        _ = new RowMenu(this, "sessionRow", row =>
            row is WorkItemPageSession session && DataContext is WorkItemPageViewModel page ? page.MenuFor(session) : []);
    }

    void OnFlyoutItemClick(object? sender, RoutedEventArgs e) =>
        ((sender as Control)?.FindLogicalAncestorOfType<Popup>()?.PlacementTarget as Button)?.Flyout?.Hide();
}
