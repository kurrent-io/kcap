using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Views;
using Lucide.Avalonia;

namespace Capacitor.App.Tests.Unit.Views;

public class StatusBadgeSmokeTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_changing_badge_speaks_its_current_text_once_and_keeps_the_full_hover_label() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var badge = new StatusBadge { Text = "Waiting" };
            var window = new Window { Content = badge, Width = 180, Height = 100 };
            try {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var peer = ControlAutomationPeer.CreatePeerForElement(badge)!;
                var glyph = badge.GetVisualDescendants().OfType<AppIcon>().Single();
                await Assert.That(peer.GetName()).IsEqualTo("Waiting");
                await Assert.That(glyph.IsEffectivelyVisible).IsFalse();

                badge.Text = "Running a background review with a longer status description";
                badge.Icon = LucideIconKind.Circle;
                badge.Classes.Add("inflight");
                Dispatcher.UIThread.RunJobs();
                await Assert.That(peer.GetName()).IsEqualTo(badge.Text);
                await Assert.That(ToolTip.GetTip(badge)).IsEqualTo(badge.Text);
                await Assert.That(peer.GetAutomationControlType()).IsEqualTo(AutomationControlType.Text);
                await Assert.That(peer.GetChildren().Count).IsEqualTo(0);
                await Assert.That(glyph.IsEffectivelyVisible).IsTrue();
                await Assert.That(ControlAutomationPeer.CreatePeerForElement(glyph)!.IsContentElement()).IsFalse();

                badge.Text = "Review finished";
                badge.Icon = null;
                badge.Classes.Remove("inflight");
                badge.Classes.Add("success");
                Dispatcher.UIThread.RunJobs();
                await Assert.That(peer.GetName()).IsEqualTo("Review finished");
                await Assert.That(glyph.IsEffectivelyVisible).IsFalse();

                AutomationProperties.SetName(badge, "Review finished for this session");
                badge.Text = "Finished";
                Dispatcher.UIThread.RunJobs();
                await Assert.That(peer.GetName()).IsEqualTo("Review finished for this session");
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Passive_badges_do_not_add_keyboard_stops_between_actions() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var first = new Button { Content = "Choose account" };
            var next = new Button { Content = "Start session" };
            var starts = 0;
            next.Click += (_, _) => starts++;
            var window = new Window {
                Content = new StackPanel { Children = { first, new StatusBadge { Text = "Default" }, next } },
                Width = 300, Height = 200,
            };
            try {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                first.Focus();
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(next.IsFocused).IsTrue();
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                await Assert.That(starts).IsEqualTo(1);
            } finally { window.Close(); }
        });
    }
}
