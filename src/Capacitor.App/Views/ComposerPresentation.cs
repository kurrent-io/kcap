using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

public sealed class ComposerPresentation : AvaloniaObject {
    public static readonly AttachedProperty<bool> IsPreviewProperty =
        AvaloniaProperty.RegisterAttached<ComposerPresentation, Control, bool>("IsPreview");

    static ComposerPresentation() => IsPreviewProperty.Changed.AddClassHandler<Control>(
        (control, change) => control.Classes.Set("previewComposer", change.NewValue is true));

    public static bool GetIsPreview(Control control) => control.GetValue(IsPreviewProperty);
    public static void SetIsPreview(Control control, bool value) => control.SetValue(IsPreviewProperty, value);
}
