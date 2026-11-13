using Avalonia;
using Avalonia.Controls;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit.Views;

[NotInParallel("AvaloniaSession")]
public class LauncherLayoutPanelTests {
    /// The composer stays centred in the whole pane while the notices fit above it, and moves down
    /// only as far as the notices and the gap need, so it is never covered.
    [Test]
    [Arguments(0, 300)]
    [Arguments(100, 300)]
    [Arguments(400, 420)]
    public async Task The_composer_moves_down_only_when_the_notices_would_reach_it(double noticeHeight, double composerTop) {
        var top = await AvaloniaSession.DispatchAsync(() => {
            var notices = new Border { Height = noticeHeight, IsVisible = noticeHeight > 0 };
            var composer = new Border { Height = 200 };
            var panel = new LauncherLayoutPanel { Gap = 20, Children = { notices, composer } };
            panel.Measure(new Size(600, 800));
            panel.Arrange(new Rect(0, 0, 600, 800));
            return composer.Bounds.Y;
        });

        await Assert.That(top).IsEqualTo(composerTop);
    }

    /// With the list shown, the composer is placed by its resting height and the list's fixed reserve: a goal that
    /// grows it, or a list that fills, leaves its top where it was, and the list takes the height left under it.
    [Test]
    public async Task With_the_list_shown_the_composer_stays_put_while_its_goal_and_the_list_grow() {
        var (before, after, listTop, listHeight) = await AvaloniaSession.DispatchAsync(() => {
            var notices = new Border { IsVisible = false };
            var composer = new Border { Height = 200 };
            var rows = new Border { Height = 60 };
            var list = new Border { Child = rows };
            var panel = new LauncherLayoutPanel { Gap = 20, ListReserve = 240, Children = { notices, composer, list } };
            panel.Measure(new Size(600, 1000));
            panel.Arrange(new Rect(0, 0, 600, 1000));
            var resting = composer.Bounds.Y;

            composer.Height = 320;
            rows.Height = 900;
            panel.InvalidateMeasure();
            list.InvalidateMeasure();
            panel.Measure(new Size(600, 1000));
            panel.Arrange(new Rect(0, 0, 600, 1000));
            return (resting, composer.Bounds.Y, list.Bounds.Y, list.Bounds.Height);
        });

        await Assert.That(before).IsEqualTo((1000 - (200 + 20 + 240)) / 2.0);
        await Assert.That(after).IsEqualTo(before);
        await Assert.That(listTop).IsEqualTo(before + 320 + 20);
        await Assert.That(listHeight).IsEqualTo(1000 - (before + 320 + 20));
    }

    /// A hidden list leaves the composer centred as it is without one.
    [Test]
    public async Task A_hidden_list_leaves_the_composer_centred() {
        var top = await AvaloniaSession.DispatchAsync(() => {
            var composer = new Border { Height = 200 };
            var panel = new LauncherLayoutPanel { Gap = 20, Children = { new Border { IsVisible = false }, composer, new Border { Height = 300, IsVisible = false } } };
            panel.Measure(new Size(600, 800));
            panel.Arrange(new Rect(0, 0, 600, 800));
            return composer.Bounds.Y;
        });

        await Assert.That(top).IsEqualTo(300);
    }
}
