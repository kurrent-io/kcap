using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

public sealed class PreviewMotion : AvaloniaObject {
    public static readonly AttachedProperty<bool> IsReducedProperty =
        AvaloniaProperty.RegisterAttached<PreviewMotion, Control, bool>("IsReduced");

    static PreviewMotion() => IsReducedProperty.Changed.AddClassHandler<Control>(
        (control, change) => control.Classes.Set("reduceMotion", change.NewValue is true));

    public static bool GetIsReduced(Control control) => control.GetValue(IsReducedProperty);
    public static void SetIsReduced(Control control, bool value) => control.SetValue(IsReducedProperty, value);
}
