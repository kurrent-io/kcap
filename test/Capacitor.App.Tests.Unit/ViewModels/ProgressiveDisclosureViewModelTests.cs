using Capacitor.App.ProgressiveDisclosure;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit.ViewModels;

/// The basis is process-wide, so these hold the whole assembly.
[NotInParallel]
public class ProgressiveDisclosureViewModelTests {
    static readonly ProgressiveDisclosureViewModel Model = ProgressiveDisclosureViewModel.Shared;

    [After(Test)]
    public void Restore() => Model.Restore(null, null);

    [Test]
    public async Task The_toggle_fades_both_levels_or_shows_both_normally() {
        Model.Enabled = false;
        await Assert.That(ProgressiveDisclosureBasis.Current)
            .IsEqualTo(new ProgressiveDisclosureBasis(ProgressiveDisclosureDisplay.Normal, ProgressiveDisclosureDisplay.Normal));

        Model.ToggleCommand.Execute().Subscribe();
        await Assert.That(ProgressiveDisclosureBasis.Current)
            .IsEqualTo(new ProgressiveDisclosureBasis(ProgressiveDisclosureDisplay.Faded, ProgressiveDisclosureDisplay.Faded));
    }

    [Test]
    public async Task A_change_is_saved_and_a_later_run_restores_it() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);
        Model.Restore(null, store);

        Model.Enabled = false;
        await store.UpdateAsync(state => state);

        var saved = (await new AppStateStore(path).LoadAsync()).ProgressiveDisclosure;
        await Assert.That(saved).IsEqualTo(new ProgressiveDisclosurePreferences(false));

        Model.Restore(null, null);
        await Assert.That(Model.Enabled).IsTrue();
        Model.Restore(saved, null);
        await Assert.That(Model.Enabled).IsFalse();
    }
}
