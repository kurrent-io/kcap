using System.Reactive;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The desktop color scheme: Auto follows the system's light or dark setting, live; Light and Dark
/// stay fixed. Saved for this computer, across profiles. The saved choice is painted as soon as this
/// is built, so every window, dialogs included, opens in it.
public sealed class DesktopColorSchemeSettings : ReactiveObject, IDisposable {
    const string LoadFailure = "Could not load this appearance.";

    readonly IAppStateStore _store;
    readonly Func<DesktopTheme, bool> _apply;
    readonly Func<bool> _systemIsDark;
    readonly IObservable<Unit> _systemChanges;
    IDisposable? _watch;
    bool _disposed;
    DesktopColorScheme _selected;
    DesktopColorScheme _saved;
    DesktopTheme? _visible;
    int _revision;
    Task _pendingSave = Task.CompletedTask;

    internal DesktopColorSchemeSettings(IAppStateStore store, string? preference,
            Func<DesktopTheme, bool> apply, Func<bool> systemIsDark, IObservable<Unit> systemChanges) {
        _store = store;
        _apply = apply;
        _systemIsDark = systemIsDark;
        _systemChanges = systemChanges;
        _saved = Parse(preference);
        _selected = _saved;
        UpdateWatch();
        Paint();
    }

    static DesktopColorScheme Parse(string? preference) => preference switch {
        "light" => DesktopColorScheme.Light,
        "dark" => DesktopColorScheme.Dark,
        _ => DesktopColorScheme.Auto,
    };

    public static IReadOnlyList<DesktopColorScheme> Choices { get; } =
        [DesktopColorScheme.Auto, DesktopColorScheme.Light, DesktopColorScheme.Dark];

    public DesktopColorScheme Selected {
        get => _selected;
        set => _ = SelectAsync(value);
    }
    public string? Problem { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    internal Task SelectAsync(DesktopColorScheme choice) {
        if (_disposed || choice == _selected && Problem is null) return _pendingSave;
        var previous = _selected;
        _selected = choice;
        if (!Paint()) {
            _selected = previous;
            this.RaisePropertyChanged(nameof(Selected));
            return _pendingSave;
        }
        UpdateWatch();
        Problem = null;
        this.RaisePropertyChanged(nameof(Selected));
        _revision++;
        if (_pendingSave.IsCompleted) _pendingSave = SaveAsync();
        return _pendingSave;
    }

    void UpdateWatch() {
        if (!_disposed && _selected == DesktopColorScheme.Auto) {
            _watch ??= _systemChanges.Subscribe(_ => Paint());
        } else {
            _watch?.Dispose();
            _watch = null;
        }
    }

    bool Paint() {
        var theme = _selected switch {
            DesktopColorScheme.Light => DesktopTheme.Light,
            DesktopColorScheme.Dark => DesktopTheme.Dark,
            _ => _systemIsDark() ? DesktopTheme.Dark : DesktopTheme.Light,
        };
        if (_visible != theme && !_apply(theme)) {
            Problem = LoadFailure;
            return false;
        }
        _visible = theme;
        if (Problem == LoadFailure) Problem = null;
        return true;
    }

    async Task SaveAsync() {
        while (true) {
            var choice = _selected;
            var revision = _revision;
            var saved = await _store.UpdateAsync(state => state with {
                DesktopColorScheme = choice.ToString().ToLowerInvariant(),
            });
            if (saved) _saved = choice;
            if (revision != _revision) continue;
            if (!saved && !_disposed) {
                _selected = _saved;
                UpdateWatch();
                Paint();
                this.RaisePropertyChanged(nameof(Selected));
                Problem = "Could not save your appearance. Try again.";
            }
            return;
        }
    }

    public void Dispose() {
        _disposed = true;
        _watch?.Dispose();
        _watch = null;
    }
}
