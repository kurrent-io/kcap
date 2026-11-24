using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class DesktopAppearanceSettings : ReactiveObject, IDisposable {
    readonly IAppStateStore _store;
    readonly Action<DesktopAppearancePreferences?> _apply;
    DesktopAppearancePreferences _selected;
    DesktopAppearancePreferences _saved;
    bool _modernExperiment;
    bool _disposed;

    internal DesktopAppearanceSettings(IAppStateStore store, IReadOnlyDictionary<string, string>? preferences,
            Action<DesktopAppearancePreferences?> apply) {
        _store = store;
        _apply = apply;
        _saved = _selected = DesktopAppearancePreferences.Read(preferences);
    }

    public static IReadOnlyList<DesktopDensity> Densities { get; } = [DesktopDensity.Comfortable, DesktopDensity.Compact];
    public static IReadOnlyList<DesktopMotion> Motions { get; } = [DesktopMotion.System, DesktopMotion.Reduce];
    public static IReadOnlyList<DesktopReadingFont> ReadingFonts { get; } = [DesktopReadingFont.Inter, DesktopReadingFont.Monospace];
    public static IReadOnlyList<DesktopVisualMode> VisualModes { get; } = [DesktopVisualMode.Classic, DesktopVisualMode.Modern];

    public DesktopDensity Density {
        get => _selected.Density;
        set => Select(_selected with { Density = value });
    }
    public DesktopMotion Motion {
        get => _selected.Motion;
        set => Select(_selected with { Motion = value });
    }
    public DesktopReadingFont ReadingFont {
        get => _selected.ReadingFont;
        set => Select(_selected with { ReadingFont = value });
    }
    public bool HighContrast {
        get => _selected.HighContrast;
        set => Select(_selected with { HighContrast = value });
    }
    public DesktopVisualMode VisualMode {
        get => _selected.VisualMode;
        set {
            if (ModernAvailable) Select(_selected with { VisualMode = value });
        }
    }
    public bool ModernAvailable => Enabled && _modernExperiment;
    public bool Enabled { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string? Problem { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool SystemMotionUnavailable => Enabled && Motion == DesktopMotion.System && ReducedMotion.SystemPreference is null;
    internal Task PendingSave { get; private set; } = Task.CompletedTask;

    internal void SetEnabled(bool enabled) {
        if (_disposed || Enabled == enabled) return;
        Enabled = enabled;
        if (enabled) ReducedMotion.SystemPreferenceChanged += NotifySystemMotion;
        else ReducedMotion.SystemPreferenceChanged -= NotifySystemMotion;
        Apply();
        if (_modernExperiment) this.RaisePropertyChanged(nameof(ModernAvailable));
        NotifySystemMotion();
    }

    internal void SetModernAvailable(bool available) {
        if (_disposed || _modernExperiment == available) return;
        _modernExperiment = available;
        if (!Enabled) return;
        if (VisualMode == DesktopVisualMode.Modern) Apply();
        this.RaisePropertyChanged(nameof(ModernAvailable));
    }

    void Apply() => _apply(!Enabled ? null : ModernAvailable ? _selected : _selected with { VisualMode = DesktopVisualMode.Classic });

    void NotifySystemMotion() => this.RaisePropertyChanged(nameof(SystemMotionUnavailable));

    void Select(DesktopAppearancePreferences choice) {
        if (_disposed || !Enabled || _selected == choice) return;
        Show(choice);
        Problem = null;
        if (PendingSave.IsCompleted) PendingSave = SaveAsync();
    }

    void Show(DesktopAppearancePreferences choice) {
        var previous = _selected;
        _selected = choice;
        if (Enabled) Apply();
        if (previous.Density != choice.Density) this.RaisePropertyChanged(nameof(Density));
        if (previous.Motion != choice.Motion) this.RaisePropertyChanged(nameof(Motion));
        if (previous.ReadingFont != choice.ReadingFont) this.RaisePropertyChanged(nameof(ReadingFont));
        if (previous.HighContrast != choice.HighContrast) this.RaisePropertyChanged(nameof(HighContrast));
        if (previous.VisualMode != choice.VisualMode) this.RaisePropertyChanged(nameof(VisualMode));
        NotifySystemMotion();
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
        if (choice.Motion != previous.Motion) values["motion"] = choice.Motion == DesktopMotion.Reduce ? "reduce" : "system";
        if (choice.ReadingFont != previous.ReadingFont) values["reading_font"] = choice.ReadingFont == DesktopReadingFont.Monospace ? "monospace" : "inter";
        if (choice.HighContrast != previous.HighContrast) values["high_contrast"] = choice.HighContrast ? "true" : "false";
        if (choice.VisualMode != previous.VisualMode) values["visual_mode"] = choice.VisualMode == DesktopVisualMode.Modern ? "modern" : "classic";
        return state with { DesktopAppearance = values };
    }

    public void Dispose() {
        SetEnabled(false);
        _disposed = true;
    }
}
