using Avalonia;
using Avalonia.Controls;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public partial class BackgroundIntelligenceSettingsView : UserControl {
    Window? _window;

    public BackgroundIntelligenceSettingsView() => InitializeComponent();

    /// <summary>A change made by onboarding, the web admin or the CLI shows without reopening Settings: the settings
    /// are read whenever the tab is shown, and on every refocus of its window while it is.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null) _window.Activated += OnActivated;
        _ = (DataContext as BackgroundIntelligenceSettingsViewModel)?.ShownAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        if (_window is not null) _window.Activated -= OnActivated;
        _window = null;
        (DataContext as BackgroundIntelligenceSettingsViewModel)?.Hidden();
        base.OnDetachedFromVisualTree(e);
    }

    void OnActivated(object? sender, EventArgs e) => _ = (DataContext as BackgroundIntelligenceSettingsViewModel)?.RefocusedAsync();
}
