using Capacitor.App.NeedToKnow;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit.ViewModels;

/// The basis is process-wide, so these hold the whole assembly.
[NotInParallel]
public class NeedToKnowBasisViewModelTests {
    static readonly NeedToKnowBasisViewModel Model = NeedToKnowBasisViewModel.Shared;

    [Before(Test)]
    public void Reset() {
        Model.SetDetailCommand.Execute(NeedToKnowDisplay.Normal).Subscribe();
        Model.SetFootnoteCommand.Execute(NeedToKnowDisplay.Faded).Subscribe();
    }

    [After(Test)]
    public void Restore() => Reset();

    [Test]
    public async Task Raising_detail_past_footnote_carries_it_along() {
        Model.SetDetailCommand.Execute(NeedToKnowDisplay.Hidden).Subscribe();

        await Assert.That(NeedToKnowBasis.Current).IsEqualTo(new NeedToKnowBasis(NeedToKnowDisplay.Hidden, NeedToKnowDisplay.Hidden));
        await Assert.That(Model.FootnoteCanBeFaded).IsFalse();
    }

    [Test]
    public async Task Footnote_cannot_be_set_more_visible_than_detail() {
        Model.SetDetailCommand.Execute(NeedToKnowDisplay.Faded).Subscribe();
        Model.SetFootnoteCommand.Execute(NeedToKnowDisplay.Normal).Subscribe();

        await Assert.That(NeedToKnowBasis.Current.Footnote).IsEqualTo(NeedToKnowDisplay.Faded);
        await Assert.That(Model.FootnoteCanBeNormal).IsFalse();
    }

    [Test]
    public async Task Lowering_detail_leaves_footnote_where_it_was() {
        Model.SetDetailCommand.Execute(NeedToKnowDisplay.Hidden).Subscribe();
        Model.SetDetailCommand.Execute(NeedToKnowDisplay.Normal).Subscribe();

        await Assert.That(NeedToKnowBasis.Current).IsEqualTo(new NeedToKnowBasis(NeedToKnowDisplay.Normal, NeedToKnowDisplay.Hidden));
    }
}
