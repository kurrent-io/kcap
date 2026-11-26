using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Threading;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.Views;

[NotInParallel("AvaloniaSession")]
public class ScrollBarSmokeTests {
    [Test]
    public Task Auto_hiding_scrollbar_reveals_on_hover_and_hides_on_exit() => RunOnUiAsync(async () => {
        var viewer = new ScrollViewer {
            Content = new Border { Height = 800 },
        };
        var window = new Window { Width = 250, Height = 200, Content = viewer };
        window.Show();
        try {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            var bar = viewer.GetTemplateDescendants().OfType<ScrollBar>()
                .Single(bar => bar.Orientation == Orientation.Vertical);
            bar.ShowDelay = TimeSpan.FromMilliseconds(1);
            bar.HideDelay = TimeSpan.FromMilliseconds(1);
            bar.Transitions = null;

            await Assert.That(bar.Opacity).IsEqualTo(0d);

            var center = bar.TranslatePoint(new Point(bar.Bounds.Width / 2, bar.Bounds.Height / 2), window)!.Value;
            window.MouseMove(center);
            await WaitUntilAsync(() => bar.IsExpanded, what: "the scrollbar to expand on hover");
            await Assert.That(bar.Opacity).IsEqualTo(1d);

            window.MouseMove(new Point(10, 10));
            await WaitUntilAsync(() => !bar.IsExpanded, what: "the scrollbar to collapse after pointer exit");
            await Assert.That(bar.Opacity).IsEqualTo(0d);
        } finally {
            window.Close();
        }
    });
}
