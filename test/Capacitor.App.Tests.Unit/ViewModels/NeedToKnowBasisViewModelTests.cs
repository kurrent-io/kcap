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

    static readonly NeedToKnowBasis AllFaded = new(NeedToKnowDisplay.Faded, NeedToKnowDisplay.Faded);
    static readonly NeedToKnowBasis AllNormal = new(NeedToKnowDisplay.Normal, NeedToKnowDisplay.Normal);

    [After(Test)]
    public Task Restore() => RunOnUiAsync(() => {
        Model.Restore(null, null);
        return Task.CompletedTask;
    });

    [Test]
    public async Task The_switch_fades_both_levels_or_shows_both_normally() {
        Model.Enabled = false;
        await Assert.That(NeedToKnowBasis.Current).IsEqualTo(AllNormal);

        Model.Enabled = true;
        await Assert.That(NeedToKnowBasis.Current).IsEqualTo(AllFaded);
    }

    [Test]
    public Task A_change_is_saved_and_a_later_run_restores_it() => RunOnUiAsync(async () => {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);
        Model.Restore(null, store);

        Model.Enabled = false;
        Model.FootnoteFade = 25;
        await store.UpdateAsync(state => state);

        await Assert.That((await new AppStateStore(path).LoadAsync()).NeedToKnow)
            .IsEqualTo(new NeedToKnowPreferences("Normal", "Normal", 60, 25));

        Model.Restore(null, null);
        Model.Restore((await new AppStateStore(path).LoadAsync()).NeedToKnow, null);
        await Assert.That(Model.Enabled).IsFalse();
        await Assert.That(Model.FootnoteFade).IsEqualTo(25);
    });

    /// A value this build does not know, or one the switch could not set, reads as on.
    [Test]
    public Task Saved_values_the_switch_could_not_set_read_as_on() => RunOnUiAsync(async () => {
        Model.Restore(new NeedToKnowPreferences("Blurred", "Hidden", 300, null), null);

        await Assert.That(NeedToKnowBasis.Current).IsEqualTo(AllFaded);
        await Assert.That(Model.DetailFade).IsEqualTo(95);
        await Assert.That(Model.FootnoteFade).IsEqualTo(30);
    });
}
