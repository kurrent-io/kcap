using System.Reactive.Subjects;

namespace Capacitor.App.Services;

/// The theme and fonts, saved in app state. A failed write keeps the choice for this run.
public sealed class AppearanceSetting {
    readonly IAppStateStore _store;
    readonly BehaviorSubject<AppearanceChoice> _changes;

    AppearanceSetting(IAppStateStore store, AppearanceChoice initial) {
        _store = store;
        _changes = new BehaviorSubject<AppearanceChoice>(initial);
    }

    public static async Task<AppearanceSetting> LoadAsync(IAppStateStore store) {
        var state = await store.LoadAsync().ConfigureAwait(false);
        return new(store, new AppearanceChoice(AppTheme.Find(state.Theme), AppFont.FindUi(state.UiFont), AppFont.FindCode(state.CodeFont)));
    }

    public AppearanceChoice Current => _changes.Value;

    /// The current choice, then each change.
    public IObservable<AppearanceChoice> Changes => _changes;

    public Task SetAsync(AppearanceChoice choice) {
        if (choice == _changes.Value) return Task.CompletedTask;

        _changes.OnNext(choice);
        return _store.UpdateAsync(state => state with {
            Theme = _changes.Value.Theme.Id, UiFont = _changes.Value.UiFont.Id, CodeFont = _changes.Value.CodeFont.Id,
        });
    }
}
