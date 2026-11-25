using System.Reactive;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class ClassicAppearancePreview : ReactiveObject, IDisposable {
    const string LoadFailure = "Could not load this appearance.";

    readonly IAppStateStore _store;
    readonly Func<ClassicAppearance, bool> _apply;
    readonly Func<bool> _systemIsDark;
    readonly IObservable<Unit> _systemChanges;
    IDisposable? _watch;
    bool _disposed;
    DesktopColorScheme _selected;
    DesktopColorScheme _saved;
    ClassicAppearance _visible;
    int _revision;
    Task _pendingSave = Task.CompletedTask;

    internal ClassicAppearancePreview(IAppStateStore store, string? preference,
            Func<ClassicAppearance, bool> apply, Func<bool> systemIsDark, IObservable<Unit> systemChanges) {
        _store = store;
        _apply = apply;
        _systemIsDark = systemIsDark;
        _systemChanges = systemChanges;
        _saved = preference switch {
            "light" => DesktopColorScheme.Light,
            "dark" => DesktopColorScheme.Dark,
            _ => DesktopColorScheme.Auto,
        };
        _selected = _saved;
    }

    public static IReadOnlyList<DesktopColorScheme> Choices { get; } =
        [DesktopColorScheme.Auto, DesktopColorScheme.Light, DesktopColorScheme.Dark];

    public DesktopColorScheme Selected {
        get => _selected;
        set => _ = SelectAsync(value);
    }
    public bool Enabled { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string? Problem { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    internal void SetEnabled(bool enabled) {
        if (_disposed || Enabled == enabled) return;
        Enabled = enabled;
        Problem = null;
        UpdateWatch();
        Paint();
    }

    internal Task SelectAsync(DesktopColorScheme choice) {
        if (_disposed || !Enabled || choice == _selected && Problem is null) return _pendingSave;
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
        if (Enabled && _selected == DesktopColorScheme.Auto) {
            _watch ??= _systemChanges.Subscribe(_ => Paint());
        } else {
            _watch?.Dispose();
            _watch = null;
        }
    }

    bool Paint() {
        var theme = !Enabled ? ClassicAppearance.Original : _selected switch {
            DesktopColorScheme.Light => ClassicAppearance.Light,
            DesktopColorScheme.Dark => ClassicAppearance.Dark,
            _ => _systemIsDark() ? ClassicAppearance.Dark : ClassicAppearance.Light,
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
