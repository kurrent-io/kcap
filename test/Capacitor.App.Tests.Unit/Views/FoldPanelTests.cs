using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
using Capacitor.App.Views;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit.Views;

public class FoldPanelTests {
    sealed record Item(bool Open);

    /// A fold on the same item slides shut, so its child stays on screen until the motion ends; a
    /// fold that arrives with a new data context, as a recycled row's does, hides at once.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_fold_slides_on_its_own_item_and_snaps_with_a_new_one() {
        await RunOnUiAsync(async () => {
            var panel = new FoldPanel { Child = new Border { Height = 40 }, DataContext = new Item(true) };
            panel.Bind(FoldPanel.IsOpenProperty, new ReflectionBinding(nameof(Item.Open)));
            var window = new Window { Content = panel, Width = 200, Height = 200 };
            try {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                panel.DataContext = new Item(false);
                await Assert.That(panel.IsVisible).IsFalse();

                panel.DataContext = new Item(true);
                await Assert.That(panel.IsVisible).IsTrue();
                window.UpdateLayout();

                panel.IsOpen = false;
                await Assert.That(panel.IsVisible).IsTrue();
            } finally { window.Close(); }
        });
    }
}
