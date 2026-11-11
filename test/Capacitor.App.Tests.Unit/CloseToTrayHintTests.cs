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
        await Assert.That(hint.TrayHint).IsEqualTo(CloseToTrayHint.TrayText);
    }

    [Test]
    public async Task A_hint_explained_in_an_earlier_run_is_not_shown_again() {
        var shown = new List<DesktopNotification>();
        var hint = new CloseToTrayHint(new States(new AppState { CloseToTrayExplained = true }), () => Task.FromResult(true), shown.Add);

        await hint.OnHiddenAsync();

        await Assert.That(shown).IsEmpty();
        await Assert.That(hint.TrayHint).IsNull();
    }

    /// With notifications denied only the tooltip says it, and nothing proves it was read, so every hide says it again.
    [Test]
    public async Task Without_notifications_every_hide_leads_the_tray_tooltip_with_it() {
        var states = new States(new AppState());
        var shown = new List<DesktopNotification>();
        var hint = new CloseToTrayHint(states, () => Task.FromResult(false), shown.Add);
        var changes = new List<string?>();
        hint.TrayHintChanged += changes.Add;

        await hint.OnHiddenAsync();
        var hidden = hint.TrayHint;
        hint.OnShown();
        var reopened = hint.TrayHint;
        await hint.OnHiddenAsync();

        await Assert.That(shown).IsEmpty();
        await Assert.That(hidden).IsEqualTo(CloseToTrayHint.TrayText);
        await Assert.That(reopened).IsNull();
        await Assert.That(hint.TrayHint).IsEqualTo(CloseToTrayHint.TrayText);
        await Assert.That(changes).IsEquivalentTo([CloseToTrayHint.TrayText, null, CloseToTrayHint.TrayText]);
        await Assert.That(states.State.CloseToTrayExplained).IsFalse();
    }

    /// A hide, a reopen and a second hide all before the first check finishes: the second hide still gets the hint.
    [Test]
    public async Task A_hide_that_lands_while_an_earlier_hide_is_pending_is_explained() {
        var states = new States(new AppState());
        var shown = new List<DesktopNotification>();
        var allowed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hint = new CloseToTrayHint(states, () => allowed.Task, shown.Add);

        var pending = hint.OnHiddenAsync();
        hint.OnShown();
        await hint.OnHiddenAsync();
        allowed.SetResult(true);
        await pending;

        await Assert.That(shown.Count).IsEqualTo(1);
        await Assert.That(hint.TrayHint).IsEqualTo(CloseToTrayHint.TrayText);
        await Assert.That(states.State.CloseToTrayExplained).IsTrue();
    }

    /// A window opened again before the hide's checks finish must not be left with a stale tooltip or a toast.
    [Test]
    public async Task A_window_reopened_while_the_hide_is_pending_gets_no_hint_until_the_next_hide() {
        var states = new States(new AppState());
        var shown = new List<DesktopNotification>();
        var allowed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hint = new CloseToTrayHint(states, () => allowed.Task, shown.Add);

        var pending = hint.OnHiddenAsync();
        hint.OnShown();
        allowed.SetResult(true);
        await pending;

        await Assert.That(shown).IsEmpty();
        await Assert.That(hint.TrayHint).IsNull();
        await Assert.That(states.State.CloseToTrayExplained).IsFalse();

        await hint.OnHiddenAsync();

        await Assert.That(shown.Count).IsEqualTo(1);
        await Assert.That(hint.TrayHint).IsEqualTo(CloseToTrayHint.TrayText);
        await Assert.That(states.State.CloseToTrayExplained).IsTrue();
    }
}
