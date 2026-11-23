using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Keeps a step's visual identity while its projected progress changes.
public sealed class UpdateStepViewModel(UpdateStep step) : ReactiveObject {

    public UpdateStep Step {
        get;
        internal set => this.RaiseAndSetIfChanged(ref field, value);
    } = step;
}
