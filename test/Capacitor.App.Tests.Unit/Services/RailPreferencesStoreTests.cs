using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit.Services;

public class RailPreferencesStoreTests {
    const string Url = "https://kurrent.kcap.ai/";
    static readonly RailPreferences Teams = new(RailScope.Team, RailState.Ended, RailGrouping.Date, RailSort.Title, false, ["day:2026-10-23"]);

    [Test]
    public async Task Unknown_profile_loads_defaults() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var opening = RailPreferencesStore.Opening(await new AppStateStore(path).LoadAsync(), Url, "kurrent");

        await Assert.That(opening.Preferences).IsEqualTo(RailPreferences.Default);
        await Assert.That(opening.UserId).IsNull();
        await Assert.That(RailPreferences.Default).IsEqualTo(new RailPreferences(RailScope.Mine, RailState.All, RailGrouping.Repository, RailSort.LastUpdated, true, []));
    }

    [Test]
    public async Task Save_then_bind_returns_the_users_set_and_opening_is_on_the_last_user() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new RailPreferencesStore(new AppStateStore(path));

        await store.SaveAsync(Url, "u1", Teams);
        var bound = await store.BindUserAsync(Url, "kurrent", "u1");
        await Assert.That(bound).IsEqualTo(Teams);

        var opening = RailPreferencesStore.Opening(await new AppStateStore(path).LoadAsync(), "https://kurrent.kcap.ai", "kurrent");
        await Assert.That(opening).IsEqualTo((Teams, "u1"));
    }

    [Test]
    public async Task Last_user_is_null_until_a_profile_binds_one() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var state = new AppStateStore(path);
        var store = new RailPreferencesStore(state);

        await Assert.That(RailPreferencesStore.Opening(await state.LoadAsync(), Url, "kurrent").UserId).IsNull();
        await store.BindUserAsync(Url, "kurrent", "u1");
        await Assert.That(RailPreferencesStore.Opening(await state.LoadAsync(), Url, "kurrent").UserId).IsEqualTo("u1");
    }

    [Test]
    public async Task Two_users_on_one_profile_keep_separate_sets() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var state = new AppStateStore(path);
        var store = new RailPreferencesStore(state);
        await store.SaveAsync(Url, "u1", Teams);
        await store.BindUserAsync(Url, "kurrent", "u1");

        var second = await store.BindUserAsync(Url, "kurrent", "u2");
        await Assert.That(second).IsEqualTo(RailPreferences.Default);
        await Assert.That(RailPreferencesStore.Opening(await state.LoadAsync(), Url, "kurrent").Preferences).IsEqualTo(RailPreferences.Default);

        var mine = Teams with { Scope = RailScope.Mine, Grouping = RailGrouping.Status };
        await store.SaveAsync(Url, "u2", mine);
        await Assert.That(await store.BindUserAsync(Url, "kurrent", "u1")).IsEqualTo(Teams);
        await Assert.That(await store.BindUserAsync(Url, "kurrent", "u2")).IsEqualTo(mine);
    }

    [Test]
    public async Task Preferences_round_trip_through_app_state_json() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);
        await store.UpdateAsync(s => s with {
            RailPreferencesByUser = new Dictionary<string, RailPreferences> { ["https://kurrent.kcap.ai|u1"] = Teams },
            LastUserByProfile = new Dictionary<string, string> { ["https://kurrent.kcap.ai|kurrent"] = "u1" },
        });

        var reloaded = await new AppStateStore(path).LoadAsync();

        await Assert.That(reloaded.RailPreferencesByUser!["https://kurrent.kcap.ai|u1"]).IsEqualTo(Teams);
        await Assert.That(reloaded.LastUserByProfile!["https://kurrent.kcap.ai|kurrent"]).IsEqualTo("u1");
    }
}
