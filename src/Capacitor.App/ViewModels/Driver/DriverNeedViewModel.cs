using System.Reactive;
using ReactiveUI.Reactive;
using System.Reactive.Linq;

namespace Capacitor.App.ViewModels.Driver;

/// <summary>One thing waiting on the person: a question, a permission, an agent that finished its turn, or a part in review.</summary>
public sealed class DriverNeedViewModel {
    public DriverNeedViewModel(string kind, string title, string context, string age, string action, Action? act) {
        Kind       = kind;
        Title      = title;
        Context    = context;
        Age        = age;
        Action     = action;
        ActCommand = ReactiveCommand.Create(() => act?.Invoke(), Observable.Return(act is not null));
    }

    public string Kind    { get; }
    public string Title   { get; }
    public string Context { get; }
    public string Age     { get; }
    public string Action  { get; }

    public ReactiveCommand<Unit, Unit> ActCommand { get; }
}
