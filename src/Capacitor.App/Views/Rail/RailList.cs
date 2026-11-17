using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views.Rail;

/// A rail list of groups or rows. Each slides from where it was to where an add, remove or re-sort put it,
/// and one that is new fades in.
public sealed class RailList : ItemsControl {
    const double Rise = 10;

    // No keyframe animator exists for RenderTransform, so this double drives it.
    static readonly AttachedProperty<double> OffsetProperty = AvaloniaProperty.RegisterAttached<RailList, Control, double>("Offset");

    // Kept by the outermost list and scoped by group: a re-sort rebuilds the nested lists with their view models.
    readonly Dictionary<(object Scope, string Key), double> _tops = [];
    readonly Dictionary<Control, CancellationTokenSource> _settling = [];
    RailList _root;
    bool _pending;

    static RailList() =>
        OffsetProperty.Changed.AddClassHandler<Control>((row, e) => row.RenderTransform = new TranslateTransform(0, e.GetNewValue<double>()));

    public RailList() => _root = this;

    protected override Type StyleKeyOverride => typeof(ItemsControl);

    object Scope => (DataContext as RailGroupViewModel)?.Key ?? (object)this;

    static string? KeyOf(Control row) => row.DataContext switch {
        RailSessionViewModel session => session.Key,
        RailGroupViewModel group => group.Key,
        _ => null,
    };

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        _root = this.FindAncestorOfType<RailList>() ?? this;
    }

    protected override void ContainerForItemPreparedOverride(Control container, object? item, int index) {
        Pend();
        container.PropertyChanged += OnRowPropertyChanged;
        base.ContainerForItemPreparedOverride(container, item, index);
    }

    protected override void ContainerIndexChangedOverride(Control container, int oldIndex, int newIndex) {
        Pend();
        base.ContainerIndexChangedOverride(container, oldIndex, newIndex);
    }

    protected override void ClearContainerForItemOverride(Control container) {
        Pend();
        container.PropertyChanged -= OnRowPropertyChanged;
        if (_settling.Remove(container, out var settling)) settling.Cancel();
        base.ClearContainerForItemOverride(container);
    }

    // Frozen while a change waits for layout: a reset clears every row before the new ones arrive.
    void OnRowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e) {
        if (!_pending && e.Property == BoundsProperty && sender is Control row && KeyOf(row) is { } key)
            _root._tops[(Scope, key)] = row.Bounds.Y;
    }

    void Pend() {
        if (_pending) return;
        _pending = true;
        LayoutUpdated += Settle;
    }

    void Settle(object? sender, EventArgs e) {
        LayoutUpdated -= Settle;
        _pending = false;
        var tops = _root._tops;
        var scope = Scope;
        var before = tops.Where(top => Equals(top.Key.Scope, scope)).ToDictionary(top => top.Key.Key, top => top.Value);
        foreach (var key in before.Keys) tops.Remove((scope, key));
        var animates = before.Count > 0 && IsEffectivelyVisible;

        // Records the layout top, not where a slide starts: a rebuilt container (a Replace on update) would otherwise replay
        // the last slide. A change mid-slide adds the live offset back, so the row continues from where it is drawn.
        foreach (var row in GetRealizedContainers()) {
            if (KeyOf(row) is not { } key) continue;
            if (animates && row.IsVisible) {
                if (!before.TryGetValue(key, out var top)) Enter(row, Rise);
                else if (top + row.GetValue(OffsetProperty) - row.Bounds.Y is var offset && Math.Abs(offset) > 0.5) Move(row, offset);
            }
            tops[(scope, key)] = row.Bounds.Y;
        }

        if (_root != this) return;
        var groups = tops.Keys.Where(key => Equals(key.Scope, scope)).Select(key => (object)key.Key).ToHashSet();
        foreach (var gone in tops.Keys.Where(key => !Equals(key.Scope, scope) && !groups.Contains(key.Scope)).ToList()) tops.Remove(gone);
    }

    void Move(Control row, double offset) => _ = Motion.Settle.RunAsync(row, Restart(row), (OffsetProperty, offset, 0d));

    void Enter(Control row, double offset) =>
        _ = Motion.Settle.RunAsync(row, Restart(row), (OffsetProperty, offset, 0d), (OpacityProperty, 0d, 1d));

    CancellationToken Restart(Control row) {
        if (_settling.Remove(row, out var running)) running.Cancel();
        return (_settling[row] = new CancellationTokenSource()).Token;
    }
}
