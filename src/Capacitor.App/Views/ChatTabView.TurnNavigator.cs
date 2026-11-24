using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public partial class ChatTabView {
    internal const string TurnNavigatorResource = "KcapTurnNavigator";
    static readonly StyledProperty<bool> TurnNavigatorEnabledProperty =
        AvaloniaProperty.Register<ChatTabView, bool>("TurnNavigatorEnabled");
    ChatTurnNavigator? _turnNavigator;

    internal ItemsControl NavigatorTranscript => ChatItems;
    internal ChatVirtualizingPanel? NavigatorPanel => ChatItems.ItemsPanelRoot as ChatVirtualizingPanel;

    void ObserveTurnNavigator() {
        var chat = GetValue(TurnNavigatorEnabledProperty) && this.IsAttachedToVisualTree() ? DataContext as ChatTabViewModel : null;
        if (!ReferenceEquals(_turnNavigator?.Chat, chat)) RemoveTurnNavigator(restoreFocus: true);
        if (_turnNavigator is not null || chat is null || _scroll is not { } scroll || ChatItems.Parent is not Grid grid) return;
        var navigator = _turnNavigator = new ChatTurnNavigator();
        Grid.SetRow(navigator, Grid.GetRow(ChatItems));
        navigator.ZIndex = 1;
        grid.Children.Insert(grid.Children.IndexOf(FindBar), navigator);
        navigator.Start(this, chat, scroll);
    }

    void RemoveTurnNavigator(bool restoreFocus) {
        if (_turnNavigator is not { } navigator) return;
        _turnNavigator = null;
        if (restoreFocus && navigator.HasFocus) FocusAfterNavigator();
        navigator.Stop();
        (navigator.Parent as Grid)?.Children.Remove(navigator);
        ReserveNavigatorSpace(false);
    }

    internal void ReserveNavigatorSpace(bool reserve) {
        FindBar.Classes.Set("navigatorReserved", reserve);
        _scroll?.Classes.Set("turnNavigator", reserve);
        if (reserve) {
            ChatItems.Margin = new Thickness(0, 0, ChatTurnNavigator.GutterWidth, 0);
            // The map describes the loaded range continuously, so keep its real thumb visible.
            _scroll?.SetValue(ScrollViewer.AllowAutoHideProperty, false);
        } else {
            ChatItems.ClearValue(MarginProperty);
            _scroll?.ClearValue(ScrollViewer.AllowAutoHideProperty);
        }
    }

    internal void ScrollFromNavigator(PointerWheelEventArgs e) {
        OnReaderGesture(this, e);
        OnChatWheelChanged(this, e);
    }

    internal void ScrollFromNavigator(ScrollGestureEventArgs e) {
        if (_scroll is not { } scroll || e.Delta.Y == 0) return;
        OnReaderGesture(this, e);
        var next = Math.Clamp(scroll.Offset.Y + e.Delta.Y, 0, scroll.ScrollBarMaximum.Y);
        if (next == scroll.Offset.Y) return;
        scroll.Offset = scroll.Offset.WithY(next);
        e.Handled = true;
    }

    internal void FocusAfterNavigator() {
        if (HasTranscriptSelection && ChatItems.Focus(NavigationMethod.Tab)) return;
        var index = NavigatorPanel?.GetReadingAnchor() is { } anchor ? ChatItems.Items.IndexOf(anchor.Row) : 0;
        FocusReadingAction(index);
    }

    internal void JumpFromNavigator(ChatTabViewModel chat, ChatItemViewModel row) {
        if (!ReferenceEquals(chat, DataContext) || !chat.CanNavigateTranscript) return;
        var target = row.SelectionKey is { } key
            ? chat.Topics.Everything().FirstOrDefault(item => item.SelectionKey == key) : row;
        if (target is null || !chat.Topics.MakeVisible(target)) return;
        ReadingNavigation();
        // A jump to the last loaded prompt can clamp to the end. Keep reading there through appends.
        _holdRestoredPosition = true;
        ShowRow(target, preserveSelection: true);
        var index = ChatItems.Items.IndexOf(target);
        if (index >= 0) {
            ChatItems.UpdateLayout();
            FocusReadingAction(index);
        }
    }
}
