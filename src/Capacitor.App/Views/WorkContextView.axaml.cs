using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The work-context pane. DataContext is the workspace's WorkContextViewModel, supplied by
/// WorkspaceView; this view builds nothing of its own.
public partial class WorkContextView : UserControl {
    public WorkContextView() {
        InitializeComponent();
    }

    ChatBookmark? _colouring;

    // Both rows draft a request in the session's composer, so they are offered only while it shows.
    void OnBookmarkMenuOpening(object? sender, EventArgs e) {
        _bookmarkMenu = sender as Flyout;
        if (_bookmarkMenu?.Content is not Panel rows) return;
        var can = DataContext is WorkContextViewModel { CanAskAgent: true };
        foreach (var row in rows.Children) {
            row.IsEnabled = can;
            if (row is Button { Tag: ChatBookmarkAsk ask } button) AgentRow(button, ask.Label());
        }
    }

    // A row that sends the agent a message leads with the agent's own mark.
    void AgentRow(Button row, string label) {
        Control mark = (DataContext as WorkContextViewModel)?.AgentVendor is { Length: > 0 } vendor
            ? LaunchChipsView.BuildGlyph(vendor, 14)
            : new AppIcon { Kind = ToolCategoryIcons.AgentIcon, Size = 14, VerticalAlignment = VerticalAlignment.Center };
        row.Content = new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { mark, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } },
        };
        AutomationProperties.SetName(row, label);
    }

    Flyout? _bookmarkMenu;

    void OnBookmarkAskClick(object? sender, RoutedEventArgs e) {
        var bookmark = _bookmarkMenu?.Target?.DataContext as ChatBookmark;
        _bookmarkMenu?.Hide();
        if (sender is Button { Tag: ChatBookmarkAsk ask } && bookmark is not null && DataContext is WorkContextViewModel pane)
            pane.AskAbout(bookmark, ask);
    }

    void OnTopicMenuOpening(object? sender, EventArgs e) {
        _topicMenu = sender as Flyout;
        if (_topicMenu?.Content is not Panel rows) return;
        var section = _topicMenu.Target?.DataContext as ChatBookmarkGroup;
        var can = DataContext is WorkContextViewModel { CanAskAgent: true } && section is { Bookmarks.Count: > 0 };
        foreach (var row in rows.Children) {
            row.IsEnabled = can;
            if (row is Button { Classes: var classes } ask && classes.Contains("topicAsk") && section is not null) AgentRow(ask, section.LooseEndsLabel);
        }
    }

    Flyout? _topicMenu;

    void OnTopicLooseEndsClick(object? sender, RoutedEventArgs e) {
        var section = _topicMenu?.Target?.DataContext as ChatBookmarkGroup;
        _topicMenu?.Hide();
        if (section is not null && DataContext is WorkContextViewModel pane)
            pane.FileLooseEnds(section);
    }

    void OnBookmarkColorsOpening(object? sender, EventArgs e) {
        _colours = sender as FlyoutBase;
        _colouring = _colours?.Target?.DataContext as ChatBookmark;
    }

    FlyoutBase? _colours;

    void OnBookmarkColorClick(object? sender, RoutedEventArgs e) {
        if (sender is Button { Tag: ChatBookmarkColor color } && _colouring is { } bookmark) bookmark.Color = color;
        _colouring = null;
        _colours?.Hide();
    }

    /// Shown only where something can bring the pane back, so set by <see cref="ContextPaneSizing"/>.
    public event EventHandler? HideRequested;

    void OnHideClick(object? sender, RoutedEventArgs e) => HideRequested?.Invoke(this, EventArgs.Empty);

    // Drag only from empty chrome — never from the refresh button (or any other Button in the strip).
    void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        WindowChrome.BeginDrag(this, e);
    }

    async void OnSessionIdClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not WorkContextViewModel { CanCopySessionId: true, SessionIdText: { Length: > 0 } id }) return;
        await CopyTextAsync(sender as Control, id);
    }

    async void OnCopyTextClick(object? sender, RoutedEventArgs e) {
        if (sender is not Control { Tag: string { Length: > 0 } text } control) return;
        await CopyTextAsync(control, text);
    }

    async Task CopyTextAsync(Control? control, string text) {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(text);
        if (control is null) return;
        // Restore the copied string, not GetTip(): a visual tip SetTip replaced cannot be
        // reparented, and ClearValue leaves an empty bubble.
        ClearCopyFlash();
        _copyRestoreText = text;
        ToolTip.SetTip(control, "Copied");
        ToolTip.SetIsOpen(control, true);
        _copyTarget = control;
        _copyRestore = (_, _) => ClearCopyFlash();
        control.PointerExited += _copyRestore;
    }

    Control? _copyTarget;
    string? _copyRestoreText;
    EventHandler<PointerEventArgs>? _copyRestore;

    void ClearCopyFlash() {
        if (_copyTarget is not null && _copyRestore is not null)
            _copyTarget.PointerExited -= _copyRestore;
        if (_copyTarget is not null && _copyRestoreText is not null)
            ToolTip.SetTip(_copyTarget, _copyRestoreText);
        _copyTarget = null;
        _copyRestoreText = null;
        _copyRestore = null;
    }
}
