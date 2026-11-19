using Capacitor.App.NeedToKnow;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit.ViewModels;

/// The basis is process-wide, so these hold the whole assembly. The fades replace app brushes, so
/// anything that moves one runs on the UI thread.
[NotInParallel]
public class NeedToKnowBasisViewModelTests {
    static readonly NeedToKnowBasisViewModel Model = NeedToKnowBasisViewModel.Shared;

    [Before(Test)]
    public void Reset() {
        Model.SetDetailCommand.Execute(NeedToKnowDisplay.Normal).Subscribe();
        Model.SetFootnoteCommand.Execute(NeedToKnowDisplay.Faded).Subscribe();
    }

    [After(Test)]
    public Task Restore() => RunOnUiAsync(() => {
        Model.Restore(null, null);
        Reset();
        return Task.CompletedTask;
    });

    [Test]
    public Task A_change_is_saved_and_a_later_run_restores_it() => RunOnUiAsync(async () => {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);
        Model.Restore(null, store);

        Model.SetDetailCommand.Execute(NeedToKnowDisplay.Hidden).Subscribe();
        Model.FootnoteFade = 25;
        await store.UpdateAsync(state => state);

        await Assert.That((await new AppStateStore(path).LoadAsync()).NeedToKnow)
            .IsEqualTo(new NeedToKnowPreferences("Hidden", "Hidden", 60, 25));

        Model.Restore(null, null);
        Model.Restore((await new AppStateStore(path).LoadAsync()).NeedToKnow, null);
        await Assert.That(NeedToKnowBasis.Current).IsEqualTo(new NeedToKnowBasis(NeedToKnowDisplay.Hidden, NeedToKnowDisplay.Hidden));
        await Assert.That(Model.FootnoteFade).IsEqualTo(25);
    });

    /// A value this build does not know, or one the controls could not set, reads as the nearest it can show.
    [Test]
    public Task Saved_values_the_controls_could_not_set_fall_back() => RunOnUiAsync(async () => {
        Model.Restore(new NeedToKnowPreferences("Blurred", "Normal", 300, null), null);

        await Assert.That(NeedToKnowBasis.Current).IsEqualTo(new NeedToKnowBasis(NeedToKnowDisplay.Faded, NeedToKnowDisplay.Faded));
        await Assert.That(Model.DetailFade).IsEqualTo(95);
        await Assert.That(Model.FootnoteFade).IsEqualTo(30);
    });

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
