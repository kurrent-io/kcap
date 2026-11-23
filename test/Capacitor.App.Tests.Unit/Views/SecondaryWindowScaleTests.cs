using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit.Views;

/// A window without its own scale host follows the text-size setting: its content grows, and so does a
/// fixed-size frame, so the larger content is not clipped.
[NotInParallel("AvaloniaSession")]
public class SecondaryWindowScaleTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public Task A_secondary_window_grows_with_the_text_size_and_follows_a_change() => AvaloniaSession.RunOnUiAsync(async () => {
        var setting = await UiScaleSetting.LoadAsync(new AppStateStore(Config.Root.Path("app-state.json")));
        await setting.SetAsync(1.5);
        var window = new AboutWindow();
        var width = window.Width;
        SecondaryWindowScale.Attach(window, setting);
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var host = (LayoutTransformControl)window.Content!;
            await Assert.That((host.LayoutTransform as ScaleTransform)?.ScaleX).IsEqualTo(1.5);
            await Assert.That(window.Width).IsEqualTo(width * 1.5);

            await setting.SetAsync(UiScale.Default);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(host.LayoutTransform).IsNull();
            await Assert.That(window.Width).IsEqualTo(width);
        } finally { window.Close(); }
    });
}
