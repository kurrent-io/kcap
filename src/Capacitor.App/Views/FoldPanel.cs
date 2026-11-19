using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// Shows or hides its child, sliding its height and fading when <see cref="Animates"/> is set.
/// A fold that arrives with a new data context, such as a recycled row, snaps instead.
public sealed class FoldPanel : Decorator {
    public static readonly StyledProperty<bool> IsOpenProperty = AvaloniaProperty.Register<FoldPanel, bool>(nameof(IsOpen), true);
    public static readonly StyledProperty<bool> AnimatesProperty = AvaloniaProperty.Register<FoldPanel, bool>(nameof(Animates), true);

    CancellationTokenSource? _folding;
    bool _rebinding;

    public FoldPanel() => ClipToBounds = true;

    public bool IsOpen { get => GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    public bool Animates { get => GetValue(AnimatesProperty); set => SetValue(AnimatesProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == IsOpenProperty) _ = FoldAsync(change.GetNewValue<bool>());
    }

    protected override void OnDataContextBeginUpdate() {
        base.OnDataContextBeginUpdate();
        _rebinding = true;
    }

    protected override void OnDataContextEndUpdate() {
        base.OnDataContextEndUpdate();
        _rebinding = false;
    }

    async Task FoldAsync(bool open) {
        _folding?.Cancel();
        if (!Animates || !IsLoaded || _rebinding) {
            ClearValue(HeightProperty);
            ClearValue(OpacityProperty);
            IsVisible = open;
            return;
        }

        var token = (_folding = new()).Token;
        var height = IsVisible ? Bounds.Height : 0;
        var opacity = IsVisible ? Opacity : 0;
        var target = 0d;
        if (open) {
            ClearValue(HeightProperty);
            ClearValue(OpacityProperty);
            IsVisible = true;
            Measure(new Size(Parent is Control parent ? parent.Bounds.Width : Bounds.Width, double.PositiveInfinity));
            target = DesiredSize.Height;
        } else {
            Height = 0;
            Opacity = 0;
        }

        await Motion.Standard.RunAsync(this, token, (HeightProperty, height, target), (OpacityProperty, opacity, open ? 1d : 0d));

        if (!open && !token.IsCancellationRequested) IsVisible = false;
    }
}
