using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// One "how much history" segment and the sessions it would bring for the current selection.
public sealed class HistoryWindowOption : ReactiveObject {

    internal HistoryWindowOption(string key, string label, DateOnly? since) {
        Key   = key;
        Label = label;
        Since = since;
    }

    public string    Key   { get; }
    public string    Label { get; }
    public DateOnly? Since { get; }

    /// "12 sessions", or null when any selected repository did not report this window.
    public string? CountLabel {
        get;
        internal set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool IsSelected {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }
}
