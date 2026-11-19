using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Styling;

namespace Capacitor.App.Views;

/// A duration and curve the app animates with, and a one-shot tween of properties along it.
public sealed record Motion(TimeSpan Duration, Easing Easing) {
    /// Chrome opening, closing or sliding aside.
    public static readonly Motion Standard = new(TimeSpan.FromMilliseconds(200), new SplineEasing(0.4, 0, 0.2));

    /// A new row settling into a list.
    public static readonly Motion Arrive = new(TimeSpan.FromMilliseconds(380), new SplineEasing(0.32, 0.72, 0, 1));

    public Task RunAsync(Animatable target, CancellationToken token, params ReadOnlySpan<(AvaloniaProperty Property, object From, object To)> tweens) {
        var from = new KeyFrame { Cue = new Cue(0d) };
        var to = new KeyFrame { Cue = new Cue(1d) };
        foreach (var tween in tweens) {
            from.Setters.Add(new Setter(tween.Property, tween.From));
            to.Setters.Add(new Setter(tween.Property, tween.To));
        }
        return new Animation { Duration = Duration, Easing = Easing, Children = { from, to } }.RunAsync(target, token);
    }
}
