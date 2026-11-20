using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

/// Rows that glide from where they were to where a rebuild put them, matched by key. A row's place is taken relative to
/// its nearest keyed ancestor, so a row moving with its group is not moved twice.
public sealed class Reflow {
    readonly Control _root;
    readonly Func<Control, object?> _keyOf;
    Dictionary<object, Point> _before = [];

    /// <param name="root">Holds the rows.</param>
    /// <param name="keyOf">Identifies a row across a rebuild; null for a control that is not a row.</param>
    public Reflow(Control root, Func<Control, object?> keyOf) {
        _root = root;
        _keyOf = keyOf;
    }

    /// Notes where each row is now. Call it before the rows are rebuilt.
    public void Capture() => _before = Places().ToDictionary(place => place.Key, place => place.At);

    /// Glides each row noted by <see cref="Capture"/> from its old place to its new one. The rows are offset in the first
    /// layout pass after the rebuild, before it is drawn, so the jump never shows.
    public void Play(object? skip = null) {
        var before = _before;
        _before = [];
        if (before.Count == 0) return;
        void Glide(object? sender, EventArgs e) {
            _root.LayoutUpdated -= Glide;
            foreach (var (row, key, at) in Places().ToList())
                if (!Equals(key, skip) && before.TryGetValue(key, out var was) && was != at) GlideFrom(row, was - at);
        }
        _root.LayoutUpdated += Glide;
    }

    IEnumerable<(Control Row, object Key, Point At)> Places() {
        foreach (var row in _root.GetVisualDescendants().OfType<Control>()) {
            if (!row.IsEffectivelyVisible || _keyOf(row) is not { } key) continue;
            var anchor = row.GetVisualAncestors().OfType<Control>().FirstOrDefault(up => up == _root || _keyOf(up) is not null);
            if (anchor is not null && row.TranslatePoint(default, anchor) is { } at) yield return (row, key, at);
        }
    }

    // RenderTransform has no animator, only a transition, so the row takes one for the glide and gives it back after.
    static void GlideFrom(Control row, Vector by) {
        var transitions = new Transitions();
        if (row.Transitions is { } styled) transitions.AddRange(styled);
        transitions.Add(new TransformOperationsTransition {
            Property = Visual.RenderTransformProperty, Duration = Motion.Standard.Duration, Easing = Motion.Standard.Easing,
        });
        row.RenderTransform = TransformOperations.Parse(FormattableString.Invariant($"translate({by.X}px, {by.Y}px)"));
        row.Transitions = transitions;
        row.RenderTransform = TransformOperations.Parse("translate(0px, 0px)");
        DispatcherTimer.RunOnce(() => {
            row.ClearValue(Animatable.TransitionsProperty);
            row.ClearValue(Visual.RenderTransformProperty);
        }, Motion.Standard.Duration);
    }
}
