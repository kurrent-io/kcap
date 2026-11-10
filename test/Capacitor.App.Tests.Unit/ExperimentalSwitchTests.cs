using Capacitor.App.Services;
using Capacitor.Cli.Core.Config;

namespace Capacitor.App.Tests.Unit;

/// The switch reads the shared store each time, so a change made elsewhere applies at the next ask.
public class ExperimentalSwitchTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly ExperimentalFeature Feature = ExperimentalFeatures.InAppPages;
    static readonly string Id = Feature.Id;

    [Test]
    public async Task A_profile_that_never_set_it_reads_off() =>
        await Assert.That(new ExperimentalSwitch(new ExperimentalSettingsStore(Config.Root), "work", Feature).IsOn).IsFalse();

    [Test]
    public async Task A_change_to_the_store_reads_back_at_the_next_ask() {
        var store = new ExperimentalSettingsStore(Config.Root);
        var sut = new ExperimentalSwitch(store, "work", Feature);

        await store.SetAsync("work", Id, true, CancellationToken.None);
        await Assert.That(sut.IsOn).IsTrue();

        await store.SetAsync("work", Id, false, CancellationToken.None);
        await Assert.That(sut.IsOn).IsFalse();
    }

    [Test]
    public async Task Another_profiles_switch_and_no_profile_read_off() {
        var store = new ExperimentalSettingsStore(Config.Root);
        await store.SetAsync("other", Id, true, CancellationToken.None);

        await Assert.That(new ExperimentalSwitch(store, "work", Feature).IsOn).IsFalse();
        await Assert.That(new ExperimentalSwitch(store, null, Feature).IsOn).IsFalse();
    }

    [Test]
    public async Task An_unreadable_store_reads_off() {
        var store = new ExperimentalSettingsStore(Config.Root);
        await File.WriteAllTextAsync(store.Path, "{ not json");

        await Assert.That(new ExperimentalSwitch(store, "work", Feature).IsOn).IsFalse();
    }
}
