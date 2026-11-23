using Avalonia;

namespace Capacitor.App.Views;

/// A view that takes rows another view's <see cref="RowDrag"/> carries over it.
public interface IRowDropSurface {
    /// The drop zone at a point in this view for what the drag carries, or null.
    RowDropTarget? TargetAt(object carried, Point at);

    /// The drag left this view, or ended.
    void DragLeft();
}
