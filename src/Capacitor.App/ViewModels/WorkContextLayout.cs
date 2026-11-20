using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Section folds a reader sets in one session's work context and expects in the next: one instance
/// for the app's lifetime, shared by every workspace's pane.
public sealed class WorkContextLayout : ReactiveObject {
    public bool UsageExpanded { get; set => this.RaiseAndSetIfChanged(ref field, value); } = true;

    public bool CheckoutExpanded { get; set => this.RaiseAndSetIfChanged(ref field, value); }

    public bool SessionExpanded { get; set => this.RaiseAndSetIfChanged(ref field, value); }
}
