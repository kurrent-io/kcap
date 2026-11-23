using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Capacitor.App.Views;

/// Fluent's TextBox template draws its placeholder at half opacity, a template value no style can
/// override, which halves the placeholder brush's contrast. Installed once per application: every
/// TextBox draws its placeholder at full opacity, so KcapPlaceholderBrush is what reaches the screen.
public static class FullOpacityPlaceholder {
    static bool _installed;

    public static void Install() {
        if (_installed) return;
        _installed = true;
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<TextBox>((_, e) => {
            if (e.NameScope.Find<TextBlock>("PART_Placeholder") is { } placeholder) placeholder.Opacity = 1;
        });
    }
}
