using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class HandoffOutcomeDismissalsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task A_dismissal_matches_any_spelling_of_the_same_session_id() {
        var dismissals = new HandoffOutcomeDismissals(new AppStateStore(Tmp.PathTo("app-state.json")));

        await dismissals.DismissAsync("0F0F0F0F-2222-4222-8222-000000000003");

        await Assert.That(await dismissals.IsDismissedAsync("0f0f0f0f222242228222000000000003")).IsTrue();
        await Assert.That(await dismissals.IsDismissedAsync("0f0f0f0f-2222-4222-8222-000000000004")).IsFalse();
    }

    [Test]
    public async Task Only_the_newest_dismissals_are_kept() {
        var path = Tmp.PathTo("app-state.json");
        var dismissals = new HandoffOutcomeDismissals(new AppStateStore(path));
        for (var n = 0; n <= HandoffOutcomeDismissals.Capacity; n++) await dismissals.DismissAsync("session-" + n);

        var reloaded = new HandoffOutcomeDismissals(new AppStateStore(path));

        await Assert.That(await reloaded.IsDismissedAsync("session-0")).IsFalse();
        await Assert.That(await reloaded.IsDismissedAsync("session-1")).IsTrue();
        await Assert.That(await reloaded.IsDismissedAsync("session-" + HandoffOutcomeDismissals.Capacity)).IsTrue();
    }

    [Test]
    public async Task A_dismissal_the_store_cannot_write_still_holds_for_the_run() {
        var dismissals = new HandoffOutcomeDismissals(new FailingStore());

        await dismissals.DismissAsync("s2");

        await Assert.That(await dismissals.IsDismissedAsync("s2")).IsTrue();
    }

    sealed class FailingStore : IAppStateStore {
        public Task<AppState> LoadAsync() => Task.FromResult(new AppState());
        public Task<bool> UpdateAsync(Func<AppState, AppState> mutate) => Task.FromResult(false);
    }
}
