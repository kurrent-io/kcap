using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Lucide.Avalonia;

namespace Capacitor.App.Views;

/// A right chevron that turns down while <see cref="IsOpen"/>, for any section that expands and collapses.
/// The turn is set here rather than from a style: an icon's RenderTransform set by a style does not show.
public sealed class FoldChevron : Decorator {
    public static readonly StyledProperty<bool> IsOpenProperty = AvaloniaProperty.Register<FoldChevron, bool>(nameof(IsOpen));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<FoldChevron, double>(nameof(Size), 12);
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<FoldChevron>();

    static readonly TransformOperations Closed = TransformOperations.Parse("rotate(0deg)");
    static readonly TransformOperations Open = TransformOperations.Parse("rotate(90deg)");

    readonly AppIcon _icon = new() { Kind = LucideIconKind.ChevronRight, Size = 12 };

    public FoldChevron() {
        Child = _icon;
        IsHitTestVisible = false;
        RenderTransform = Closed;
    }

    public bool IsOpen { get => GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    // Turning only once loaded keeps a row that arrives open from spinning into place.
    protected override void OnLoaded(RoutedEventArgs e) {
        base.OnLoaded(e);
        Transitions ??= [new TransformOperationsTransition {
            Property = RenderTransformProperty, Duration = Motion.Standard.Duration, Easing = Motion.Standard.Easing,
        }];
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == IsOpenProperty) RenderTransform = change.GetNewValue<bool>() ? Open : Closed;
        else if (change.Property == SizeProperty) _icon.Size = change.GetNewValue<double>();
    }
}
