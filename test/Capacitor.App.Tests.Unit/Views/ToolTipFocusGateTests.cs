using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit.Views;

public class ToolTipFocusGateTests {
    [Test]
    [Arguments(TextWrapping.NoWrap)]
    [Arguments(TextWrapping.Wrap)]
    [NotInParallel("AvaloniaSession")]
    public Task A_label_only_shows_its_text_tooltip_when_truncated(TextWrapping wrapping) =>
        AvaloniaSession.RunOnUiAsync(async () => {
            var label = new TextBlock {
                Text = "A label with enough words to be clipped on either one or two lines.",
                Width = 450,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = wrapping,
                MaxLines = 2,
            };
            TrimmedTextTooltip.SetShowOnlyWhenTrimmed(label, true);
            ToolTip.SetTip(label, label.Text);
            var window = new Window { Content = label, Width = 550, Height = 150 };
            window.Show();
            try {
                TooltipPreviewScope.Pump(window);
                ToolTip.SetIsOpen(label, true);
                await Assert.That(ToolTip.GetIsOpen(label)).IsFalse();

                label.Width = 90;
                TooltipPreviewScope.Pump(window);
                ToolTip.SetIsOpen(label, true);
                await Assert.That(ToolTip.GetIsOpen(label)).IsTrue();
            } finally {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_focused_option_can_show_help_in_a_native_dropdown_of_the_active_window() =>
        AvaloniaSession.RunWithNativePopupRootsAsync(async () => {
            ToolTipFocusGate.Install();
            var choices = new ComboBox { ItemsSource = new[] { "First", "Second" } };
            var window = new Window { Content = choices, Width = 300, Height = 200 };
            window.Show();
            try {
                choices.IsDropDownOpen = true;
                TooltipPreviewScope.Pump(window);
                var second = (ComboBoxItem)choices.ContainerFromIndex(1)!;
                second.Focus();
                var popup = (PopupRoot)TopLevel.GetTopLevel(second)!;
                AvaloniaSession.LoseKeyboardFocus(popup);
                await Assert.That(window.IsActive).IsTrue();
                await Assert.That(popup.IsActive).IsFalse();
                ToolTip.SetTip(second, "Second choice");
                ToolTip.SetIsOpen(second, true);
                await Assert.That(ToolTip.GetIsOpen(second)).IsTrue();
                AvaloniaSession.LoseKeyboardFocus(window);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(window.IsActive).IsFalse();
                await Assert.That(ToolTip.GetIsOpen(second)).IsFalse();
            } finally {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task An_inactive_window_shows_no_tooltip(bool useOverlay) {
        await AvaloniaSession.RunWithNativePopupRootsAsync(async () => {
            ToolTipFocusGate.Install();
            var target = new Button { Content = "x" };
            ToolTip.SetTip(target, "tip");
            ToolTip.SetShouldUseOverlayLayer(target, useOverlay);
            var window = new Window { Content = target, Width = 200, Height = 100 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try {
                ToolTip.SetIsOpen(target, true);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(ToolTip.GetIsOpen(target)).IsTrue();

                AvaloniaSession.LoseKeyboardFocus(window);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(window.IsActive).IsFalse();
                await Assert.That(ToolTip.GetIsOpen(target)).IsFalse();

                ToolTip.SetIsOpen(target, true);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(ToolTip.GetIsOpen(target)).IsFalse();
            } finally {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });
    }
}
