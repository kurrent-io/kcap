using ReactiveUI.Reactive;

namespace Capacitor.App.Services.LocalSetup;

public sealed class InstallRow : ReactiveObject {

    public RowState State {
        get;
        internal set => this.RaiseAndSetIfChanged(ref field, value);
    }
}
