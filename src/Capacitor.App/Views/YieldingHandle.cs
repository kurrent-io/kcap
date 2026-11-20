using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering;

namespace Capacitor.App.Views;

/// A drag handle drawn over other content that lets the pointer through wherever <see cref="YieldsAt"/>
/// says the content beneath should have it.
public sealed class YieldingHandle : Border, ICustomHitTest {
    public Func<Point, bool>? YieldsAt { get; set; }

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point) && YieldsAt?.Invoke(point) != true;
}
