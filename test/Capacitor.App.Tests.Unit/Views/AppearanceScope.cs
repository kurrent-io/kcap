using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Capacitor.App.Tests.Unit.Views;

/// Paints a theme and the default reading preferences for one test, and puts the test app back in
/// its resting state (Dark, no reading layout) afterwards.
internal sealed class AppearanceScope : IDisposable {
    readonly DesktopPalette _palette = new(Application.Current!);
    readonly DesktopReadingResources _reading = new(Application.Current!);

    public void Apply(DesktopTheme theme) {
        if (!_palette.TryApply(theme)) throw new InvalidOperationException($"Could not apply {theme}");
        _reading.Apply(new());
    }

    public void Dispose() {
        ResetReading(_reading);
        _palette.TryApply(DesktopTheme.Dark);
    }

    /// Default preferences, and no reading-layout revision for a later test's transcript to react to.
    public static void ResetReading(DesktopReadingResources reading) {
        reading.Apply(new());
        Application.Current!.Resources.Remove("KcapReadingLayout");
    }

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
