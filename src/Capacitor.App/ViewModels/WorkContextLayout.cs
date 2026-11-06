using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Section folds a reader sets in one session's work context and expects in the next: one instance
/// for the app's lifetime, shared by every workspace's pane.
public sealed class WorkContextLayout : ReactiveObject {
    bool _usageExpanded = true;
    public bool UsageExpanded { get => _usageExpanded; set => this.RaiseAndSetIfChanged(ref _usageExpanded, value); }

    bool _checkoutExpanded;
    public bool CheckoutExpanded { get => _checkoutExpanded; set => this.RaiseAndSetIfChanged(ref _checkoutExpanded, value); }

    bool _sessionExpanded;
    public bool SessionExpanded { get => _sessionExpanded; set => this.RaiseAndSetIfChanged(ref _sessionExpanded, value); }
}
