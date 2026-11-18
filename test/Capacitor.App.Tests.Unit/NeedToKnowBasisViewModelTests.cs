using Capacitor.App.NeedToKnow;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

/// The basis is process-wide, so these hold the whole assembly.
[NotInParallel]
public class NeedToKnowBasisViewModelTests {
    static readonly NeedToKnowBasisViewModel Model = NeedToKnowBasisViewModel.Shared;

    [Before(Test)]
    public void Reset() {
        Model.SetWorthKnowingCommand.Execute(NeedToKnowDisplay.Normal).Subscribe();
        Model.SetOnlyIfCuriousCommand.Execute(NeedToKnowDisplay.Faded).Subscribe();
    }

    [After(Test)]
    public void Restore() => Reset();

    [Test]
    public async Task Raising_worth_knowing_past_only_if_curious_carries_it_along() {
        Model.SetWorthKnowingCommand.Execute(NeedToKnowDisplay.Collapsed).Subscribe();

        await Assert.That(NeedToKnowBasis.Current).IsEqualTo(new NeedToKnowBasis(NeedToKnowDisplay.Collapsed, NeedToKnowDisplay.Collapsed));
        await Assert.That(Model.OnlyIfCuriousCanBeFaded).IsFalse();
        await Assert.That(Model.OnlyIfCuriousCanBeCollapsed).IsTrue();
    }

    [Test]
    public async Task Only_if_curious_cannot_be_set_more_visible_than_worth_knowing() {
        Model.SetWorthKnowingCommand.Execute(NeedToKnowDisplay.Marked).Subscribe();
        Model.SetOnlyIfCuriousCommand.Execute(NeedToKnowDisplay.Normal).Subscribe();

        await Assert.That(NeedToKnowBasis.Current.OnlyIfCurious).IsEqualTo(NeedToKnowDisplay.Marked);
        await Assert.That(Model.OnlyIfCuriousCanBeNormal).IsFalse();
    }

    [Test]
    public async Task Lowering_worth_knowing_leaves_only_if_curious_where_it_was() {
        Model.SetWorthKnowingCommand.Execute(NeedToKnowDisplay.Hidden).Subscribe();
        Model.SetWorthKnowingCommand.Execute(NeedToKnowDisplay.Normal).Subscribe();

        await Assert.That(NeedToKnowBasis.Current).IsEqualTo(new NeedToKnowBasis(NeedToKnowDisplay.Normal, NeedToKnowDisplay.Hidden));
    }
}
