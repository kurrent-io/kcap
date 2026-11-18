using Avalonia;
using Avalonia.Controls;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public partial class SearchSettingsView : UserControl {
    public SearchSettingsView() => InitializeComponent();

    /// <summary>A failing vector pass shows up only after the server has run one, so each showing asks again.
    /// The built-in offer is a network fetch, so it waits for the controls to be shown rather than the app's launch.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        (DataContext as LocalSearchViewModel)?.Shown();
    }
}
