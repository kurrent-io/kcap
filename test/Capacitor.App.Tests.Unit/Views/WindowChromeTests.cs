using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Capacitor.App.Views;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit.Views;

public class WindowChromeTests {
    [Test]
    [Arguments(WindowState.Normal, null, WindowState.Maximized)]
    [Arguments(WindowState.Maximized, null, WindowState.Normal)]
    [Arguments(WindowState.Normal, "Maximize", WindowState.Maximized)]
    [Arguments(WindowState.Maximized, "Maximize", WindowState.Normal)]
    [Arguments(WindowState.Maximized, "Minimize", WindowState.Minimized)]
    [Arguments(WindowState.Normal, "None", WindowState.Normal)]
    public async Task Double_click_uses_the_platform_action(WindowState current, string? action, WindowState expected) {
        await Assert.That(WindowChrome.DoubleClickState(current, action)).IsEqualTo(expected);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_second_title_bar_press_is_consumed_even_in_a_fixed_size_window() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var strip = new Border { Background = Brushes.Transparent };
            var window = new Window { Content = strip, Width = 400, Height = 300, CanResize = false };
            var handled = new List<bool>();
            strip.PointerPressed += (_, e) => {
                WindowChrome.BeginDrag(strip, e);
                handled.Add(e.Handled);
            };

            window.Show();
            try {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                var point = new Point(42, 21);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);

                await Assert.That(handled).IsEquivalentTo([false, true], CollectionOrdering.Matching);
                await Assert.That(window.WindowState).IsEqualTo(WindowState.Normal);
            } finally {
                window.Close();
            }
        });
    }
}
