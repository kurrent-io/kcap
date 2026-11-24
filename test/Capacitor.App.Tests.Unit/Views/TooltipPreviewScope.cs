using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Capacitor.App.Tests.Unit.Views;

internal sealed class TooltipPreviewScope : IDisposable {
    readonly ClassicPreviewPalette _palette = new(Application.Current!);
    readonly DesktopReadingResources _reading = new(Application.Current!);

    public void Enable(ClassicAppearance appearance) {
        if (!_palette.TryApply(appearance)) throw new InvalidOperationException($"Could not apply {appearance}");
        _reading.Apply(new());
    }

    public void Disable() {
        _reading.Apply(null);
        _palette.TryApply(ClassicAppearance.Original);
    }

    public void Dispose() => Disable();

    public static void Pump(Window window) {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
    }

    public static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None) {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
        Pump(window);
    }

    public static string[] Text(Control target) {
        if (!ToolTip.GetIsOpen(target) || ToolTip.GetTip(target) is not ToolTip tooltip)
            throw new InvalidOperationException("Structured tooltip is not open.");
        return tooltip.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsEffectivelyVisible).Select(text => text.Text!)
            .ToArray();
    }
}
