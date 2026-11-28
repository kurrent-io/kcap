using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Capacitor.App.Platform;
using Capacitor.App.Platform.Mac;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit.Views;

public class PlatformTextRenderingTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_window_and_its_dropdown_draw_text_the_platform_way() => RunWithNativePopupRootsAsync(async () => {
        var choices = new ComboBox { ItemsSource = new[] { "First", "Second" } };
        var window = new Window { Content = choices, Width = 300, Height = 200 };
        window.Show();
        try {
            choices.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs();
            var popup = (PopupRoot)TopLevel.GetTopLevel(choices.ContainerFromIndex(1)!)!;

            await Assert.That(TextOptions.GetTextRenderingMode(window)).IsEqualTo(Program.Host.TextRendering);
            await Assert.That(TextOptions.GetTextRenderingMode(popup)).IsEqualTo(Program.Host.TextRendering);
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Test]
    public async Task macOS_draws_grayscale_text_as_AppKit_does() {
        if (!OperatingSystem.IsMacOS()) return;
        await Assert.That(new MacDesktopHost("", false, SingleInstanceLock.Unenforced).TextRendering).IsEqualTo(TextRenderingMode.Antialias);
    }
}
