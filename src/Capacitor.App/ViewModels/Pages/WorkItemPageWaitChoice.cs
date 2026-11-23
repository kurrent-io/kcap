using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Pages;

/// Another slice this one can wait on, and whether it does. <see cref="Loops"/> when that slice already waits on this
/// one, directly or through others, so it can't be picked.
public sealed class WorkItemPageWaitChoice : ReactiveObject {
    public string Id { get; }
    public string Title { get; }
    public bool Loops { get; }
    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    bool _waits;
    public bool Waits { get => _waits; private set => this.RaiseAndSetIfChanged(ref _waits, value); }

    /// <param name="set">Records the wait or its removal; false when it was refused, which puts the choice back.</param>
    public WorkItemPageWaitChoice(string id, string title, bool waits, bool loops, Func<bool, Task<bool>> set) {
        Id = id;
        Title = title;
        Loops = loops;
        _waits = waits;
        ToggleCommand = ReactiveCommand.CreateFromTask(async () => {
            Waits = !Waits;
            if (!await set(Waits)) Waits = !Waits;
        }, Observable.Return(!loops));
    }
}
