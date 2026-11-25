using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

public sealed class TrimmedTextTooltip : AvaloniaObject {
    public static readonly AttachedProperty<bool> ShowOnlyWhenTrimmedProperty =
        AvaloniaProperty.RegisterAttached<TrimmedTextTooltip, TextBlock, bool>("ShowOnlyWhenTrimmed");

    static TrimmedTextTooltip() => ToolTip.ToolTipOpeningEvent.AddClassHandler<TextBlock>((text, e) => {
        if (ReferenceEquals(e.Source, text) && GetShowOnlyWhenTrimmed(text) &&
            !text.TextLayout.TextLines.Any(line => line.HasCollapsed))
            e.Cancel = true;
    });

    public static bool GetShowOnlyWhenTrimmed(TextBlock text) => text.GetValue(ShowOnlyWhenTrimmedProperty);
    public static void SetShowOnlyWhenTrimmed(TextBlock text, bool value) => text.SetValue(ShowOnlyWhenTrimmedProperty, value);
}
