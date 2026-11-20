using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// A row's usage, held by reference so a clock tick changes the view without rebuilding the row list.
public sealed class AccountUsageSlot : ReactiveObject {

    public AccountUsageView? View {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }
}
