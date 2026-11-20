using Avalonia.Controls;

namespace Capacitor.App.Views;

/// A drop zone under a dragged row and what dropping there does. Dropped over it, the zone carries
/// <see cref="RowDrag.DropOverClass"/>; at an edge, the drag's insertion line shows there.
public sealed record RowDropTarget(Control Zone, Action Drop, RowDropEdge Edge = RowDropEdge.Over);
