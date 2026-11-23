using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Draws the chat's topic rails behind its rows and folds a topic when its rail is clicked.
/// Hit-testable only over a rail, so the rest of the chat keeps its pointer.
public sealed class ChatTopicRailLayer(ChatVirtualizingPanel panel) : Control, ICustomHitTest {
    ChatTopicItem? _hovered;

    public bool HitTest(Point point) => panel.RailAt(point) is not null;

    public override void Render(DrawingContext context) => panel.DrawRails(context, _hovered);

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        Hover(panel.RailAt(e.GetPosition(this)));
    }

    protected override void OnPointerExited(PointerEventArgs e) {
        base.OnPointerExited(e);
        Hover(null);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || panel.RailAt(e.GetPosition(this)) is not { } topic) return;
        e.Handled = true;
        Hover(null);
        panel.FoldFromRail(topic);
    }

    void Hover(ChatTopicItem? topic) {
        if (ReferenceEquals(topic, _hovered)) return;
        _hovered = topic;
        Cursor = topic is null ? null : new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, topic is null ? null : topic.ToggleTip);
        InvalidateVisual();
    }
}
