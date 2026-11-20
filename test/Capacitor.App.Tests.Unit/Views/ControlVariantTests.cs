using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Capacitor.App.Tests.Unit.Views;

/// Each button variant, tested where it is defined, so every button carrying it behaves the same:
/// a readable label, feedback on hover and press, a visible keyboard focus, and a disabled state
/// that reads as disabled and gives no feedback.
[NotInParallel("AvaloniaSession")]
public class ControlVariantTests {
    public static IEnumerable<Func<string>> ButtonVariants() => new[] {
        "kcapPrimary", "kcapChip", "kcapGhost", "kcapDanger", "kcapDangerFill", "acpOption",
        "onboardingPrimary", "onboardingSecondary", "onboardingQuiet", "onboardingLink",
    }.Select(v => (Func<string>)(() => v));

    readonly record struct Look(Color Background, Color Border, Color Foreground, double Opacity);

    sealed class Rig : IDisposable {
        public Window Window { get; }
        public Button Button { get; }
        public Color Canvas { get; }

        public Rig(string variant) {
            var canvasKey = variant.StartsWith("onboarding", StringComparison.Ordinal) ? "OnboardingCanvasBrush" : "KcapSurfaceBrush";
            Canvas = ((ISolidColorBrush)Application.Current!.FindResource(canvasKey)!).Color;
            Button = new Button { Content = "Action", Classes = { variant } };
            Window = new Window {
                Width = 320, Height = 160, Background = new SolidColorBrush(Canvas),
                Content = new StackPanel { Margin = new Thickness(40), Children = { new TextBox(), Button } },
            };
            Window.Show();
            Pump();
        }

        public void Pump() {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Window.UpdateLayout();
        }

        Point Centre => Button.TranslatePoint(new Point(Button.Bounds.Width / 2, Button.Bounds.Height / 2), Window)!.Value;

        public void Hover() { Window.MouseMove(Centre); Pump(); }
        public void Press() { Window.MouseMove(Centre); Window.MouseDown(Centre, MouseButton.Left); Pump(); }
        public void Release() { Window.MouseUp(Centre, MouseButton.Left); Window.MouseMove(new Point(1, 1)); Pump(); }
        public void TabFocus() { Button.Focus(NavigationMethod.Tab); Pump(); }

        public Look Read() {
            var presenter = Button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            var background = Over(Solid(presenter.Background), Canvas);
            return new(background, Over(Solid(presenter.BorderBrush), background), Over(Solid(presenter.Foreground), background), Button.Opacity * presenter.Opacity);
        }

        public void Dispose() => Window.Close();
    }

    static Color Solid(IBrush? brush) => brush is ISolidColorBrush s
        ? Color.FromArgb((byte)(s.Color.A * s.Opacity), s.Color.R, s.Color.G, s.Color.B)
        : Colors.Transparent;

    static Color Over(Color top, Color under) {
        var a = top.A / 255.0;
        byte Mix(byte t, byte u) => (byte)Math.Round(t * a + u * (1 - a));
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    static double Contrast(Color x, Color y) {
        static double L(Color c) {
            static double Ch(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        var (a, b) = (L(x), L(y));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    [Test]
    [MethodDataSource(nameof(ButtonVariants))]
    public Task The_label_is_readable_at_rest(string variant) => AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig(variant);
        var look = rig.Read();
        await Assert.That(Contrast(look.Foreground, look.Background)).IsGreaterThanOrEqualTo(4.5);
    });

    [Test]
    [MethodDataSource(nameof(ButtonVariants))]
    public Task Hover_and_press_change_what_the_button_shows(string variant) => AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig(variant);
        var rest = rig.Read();
        rig.Hover();
        var hover = rig.Read();
        rig.Press();
        var pressed = rig.Read();
        rig.Release();

        await Assert.That(hover).IsNotEqualTo(rest);
        await Assert.That(pressed).IsNotEqualTo(rest);
        await Assert.That(rig.Read()).IsEqualTo(rest).Because("leaving the button returns it to rest");
    });

    [Test]
    [MethodDataSource(nameof(ButtonVariants))]
    public Task Keyboard_focus_is_visible(string variant) => AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig(variant);
        var rest = rig.Read();
        rig.TabFocus();
        await Assert.That(rig.Button.IsFocused).IsTrue();
        var focused = rig.Read();
        var layer = AdornerLayer.GetAdornerLayer(rig.Button);
        var adorned = layer?.Children.Any(c => c.IsVisible && ReferenceEquals(AdornerLayer.GetAdornedElement(c), rig.Button)) == true;
        await Assert.That(focused != rest || adorned).IsTrue();
    });

    [Test]
    [MethodDataSource(nameof(ButtonVariants))]
    public Task A_disabled_button_reads_as_disabled_and_gives_no_feedback(string variant) => AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig(variant);
        var enabled = rig.Read();
        rig.Button.IsEnabled = false;
        rig.Pump();
        var disabled = rig.Read();
        rig.Hover();

        var dimmer = disabled.Opacity < enabled.Opacity
            || Contrast(disabled.Foreground, disabled.Background) < Contrast(enabled.Foreground, enabled.Background);
        await Assert.That(dimmer).IsTrue();
        await Assert.That(rig.Read()).IsEqualTo(disabled);
    });

    [Test]
    public Task A_field_shows_keyboard_focus() => AvaloniaSession.RunOnUiAsync(async () => {
        var field = new TextBox { Classes = { "kcapField" } };
        var window = new Window { Width = 320, Height = 120, Content = new StackPanel { Margin = new Thickness(20), Children = { new Button(), field } } };
        window.Show();
        try {
            Dispatcher.UIThread.RunJobs();
            Border Frame() => field.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
            var rest = Solid(Frame().BorderBrush);
            field.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(Solid(Frame().BorderBrush)).IsNotEqualTo(rest);
        } finally { window.Close(); }
    });
}
