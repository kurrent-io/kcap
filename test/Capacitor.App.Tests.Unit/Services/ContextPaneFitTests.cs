using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit.Services;

public class ContextPaneFitTests {
    static readonly double Wide = ContextPaneFit.RoomNeeded + 100;
    static readonly double Narrow = ContextPaneFit.RoomNeeded - 100;

    [Test]
    public async Task The_pane_hides_below_the_session_minimum_and_returns_above_it() {
        var fit = new ContextPaneFit();
        fit.Resize(Wide);
        await Assert.That(fit.HidesPane).IsFalse();

        await Assert.That(fit.Resize(Narrow)).IsTrue();
        await Assert.That(fit.HidesPane).IsTrue();

        await Assert.That(fit.Resize(Wide)).IsTrue();
        await Assert.That(fit.HidesPane).IsFalse();
    }

    [Test]
    public async Task Showing_the_pane_while_narrow_holds_until_the_next_crossing() {
        var fit = new ContextPaneFit();
        fit.Resize(Narrow);
        fit.ShowAnyway();
        await Assert.That(fit.HidesPane).IsFalse();

        await Assert.That(fit.Resize(Narrow - 50)).IsFalse().Because("a resize that stays narrow keeps the user's choice");
        await Assert.That(fit.HidesPane).IsFalse();

        fit.Resize(Wide);
        fit.Resize(Narrow);
        await Assert.That(fit.HidesPane).IsTrue();
    }

    [Test]
    public async Task A_view_not_laid_out_yet_is_not_narrow() {
        var fit = new ContextPaneFit();
        fit.Resize(0);
        await Assert.That(fit.HidesPane).IsFalse();
    }
}
