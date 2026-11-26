using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Density, reading font and contrast, saved for this computer across profiles and applied as soon
/// as this is built.
public sealed class DesktopAppearanceSettings : ReactiveObject, IDisposable {
    readonly IAppStateStore _store;
    readonly Action<DesktopAppearancePreferences> _apply;
    DesktopAppearancePreferences _selected;
    DesktopAppearancePreferences _saved;
    bool _disposed;

    internal DesktopAppearanceSettings(IAppStateStore store, IReadOnlyDictionary<string, string>? preferences,
            Action<DesktopAppearancePreferences> apply) {
        _store = store;
        _apply = apply;
        _saved = _selected = DesktopAppearancePreferences.Read(preferences);
        _apply(_selected);
    }

    public static IReadOnlyList<DesktopDensity> Densities { get; } = [DesktopDensity.Comfortable, DesktopDensity.Compact];
    public static IReadOnlyList<DesktopReadingFont> ReadingFonts { get; } = [DesktopReadingFont.Inter, DesktopReadingFont.Monospace];

    public DesktopDensity Density {
        get => _selected.Density;
        set => Select(_selected with { Density = value });
    }
    public DesktopReadingFont ReadingFont {
        get => _selected.ReadingFont;
        set => Select(_selected with { ReadingFont = value });
    }
    public bool HighContrast {
        get => _selected.HighContrast;
        set => Select(_selected with { HighContrast = value });
    }
    public string? Problem { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    internal Task PendingSave { get; private set; } = Task.CompletedTask;

    void Select(DesktopAppearancePreferences choice) {
        if (_disposed || _selected == choice) return;
        Show(choice);
        Problem = null;
        if (PendingSave.IsCompleted) PendingSave = SaveAsync();
    }

    void Show(DesktopAppearancePreferences choice) {
        var previous = _selected;
        _selected = choice;
        if (!_disposed) _apply(choice);
        if (previous.Density != choice.Density) this.RaisePropertyChanged(nameof(Density));
        if (previous.ReadingFont != choice.ReadingFont) this.RaisePropertyChanged(nameof(ReadingFont));
        if (previous.HighContrast != choice.HighContrast) this.RaisePropertyChanged(nameof(HighContrast));
    }

    async Task SaveAsync() {
        while (true) {
            var choice = _selected;
            var previous = _saved;
            if (await _store.UpdateAsync(state => Save(state, previous, choice))) _saved = choice;
            if (_selected != choice) continue;
            if (_saved != choice && !_disposed) {
                Show(_saved);
                Problem = "Could not save your reading and accessibility settings. Try again.";
            }
            return;
        }
    }

    static AppState Save(AppState state, DesktopAppearancePreferences previous, DesktopAppearancePreferences choice) {
        var values = state.DesktopAppearance is { } current
            ? new Dictionary<string, string>(current, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        if (choice.Density != previous.Density) values["density"] = choice.Density == DesktopDensity.Compact ? "compact" : "comfortable";
        if (choice.ReadingFont != previous.ReadingFont) values["reading_font"] = choice.ReadingFont == DesktopReadingFont.Monospace ? "monospace" : "inter";
        if (choice.HighContrast != previous.HighContrast) values["high_contrast"] = choice.HighContrast ? "true" : "false";
        return state with { DesktopAppearance = values };
    }

    public void Dispose() => _disposed = true;
}
