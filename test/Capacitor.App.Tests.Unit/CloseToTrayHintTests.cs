using Capacitor.App.Services;
using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Tests.Unit;

/// Closing the main window on Windows hides it to the notification area; the first time, the user is told so.
public class CloseToTrayHintTests {
    sealed class States(AppState state) : IAppStateStore {
        public AppState State = state;

        public Task<AppState> LoadAsync() => Task.FromResult(State);

        public Task<bool> UpdateAsync(Func<AppState, AppState> mutate) {
            State = mutate(State);
            return Task.FromResult(true);
        }
    }

    [Test]
    public async Task The_first_hide_notifies_once_and_remembers_it() {
        var states = new States(new AppState());
        var shown = new List<DesktopNotification>();
        var hint = new CloseToTrayHint(states, () => Task.FromResult(true), shown.Add);

        await hint.OnHiddenAsync();
        await hint.OnHiddenAsync();

        await Assert.That(shown.Count).IsEqualTo(1);
        await Assert.That(shown[0].Title).IsEqualTo(CloseToTrayHint.Title);
        await Assert.That(shown[0].Body).IsEqualTo(CloseToTrayHint.Body);
        await Assert.That(states.State.CloseToTrayExplained).IsTrue();
        await Assert.That(hint.TrayHint).IsNull();
    }

    [Test]
    public async Task A_hint_explained_in_an_earlier_run_is_not_shown_again() {
        var shown = new List<DesktopNotification>();
        var hint = new CloseToTrayHint(new States(new AppState { CloseToTrayExplained = true }), () => Task.FromResult(true), shown.Add);

        await hint.OnHiddenAsync();

        await Assert.That(shown).IsEmpty();
        await Assert.That(hint.TrayHint).IsNull();
    }

    /// With notifications denied the tray icon's tooltip says it instead, until the window is opened again.
    [Test]
    public async Task Without_notifications_the_tray_tooltip_carries_it_until_the_window_opens() {
        var states = new States(new AppState());
        var shown = new List<DesktopNotification>();
        var hint = new CloseToTrayHint(states, () => Task.FromResult(false), shown.Add);
        var changes = new List<string?>();
        hint.TrayHintChanged += changes.Add;

        await hint.OnHiddenAsync();
        var hidden = hint.TrayHint;
        hint.OnShown();

        await Assert.That(shown).IsEmpty();
        await Assert.That(hidden).IsEqualTo(CloseToTrayHint.TrayText);
        await Assert.That(hint.TrayHint).IsNull();
        await Assert.That(changes).IsEquivalentTo([CloseToTrayHint.TrayText, null]);
        await Assert.That(states.State.CloseToTrayExplained).IsTrue();
    }
}
