using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Pages;

/// The open work items of the rail's project, in the server's order, read again when the project changes. Search
/// narrows them by title.
public sealed class WorkItemsPageViewModel : ReactiveObject, IPage {
    public PageKey Key => PageKey.WorkItems;
    readonly CompositeDisposable _disposables = new();
    IReadOnlyList<WorkItemsPageRow> _all = [];

    public ResettableCollection<WorkItemsPageRow> Rows { get; } = [];

    string _heading = "Work items";
    public string Heading { get => _heading; private set => this.RaiseAndSetIfChanged(ref _heading, value); }
    string _count = "";
    public string Count { get => _count; private set => this.RaiseAndSetIfChanged(ref _count, value); }
    string? _message = "Loading…";
    public string? Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }

    string _search = "";
    public string Search {
        get => _search;
        set {
            if (_search == (value ?? "")) return;
            this.RaiseAndSetIfChanged(ref _search, value ?? "");
            Redraw(null);
        }
    }

    public ReactiveCommand<WorkItemsPageRow, Unit> OpenWorkItemCommand { get; }

    public WorkItemsPageViewModel(Func<string, CancellationToken, Task<RailWorkItems?>> read, IObservable<RailProject?> project,
            Action<string> openWorkItem) {
        OpenWorkItemCommand = ReactiveCommand.Create<WorkItemsPageRow>(row => openWorkItem(row.WorkItemId)).DisposeWith(_disposables);
        Observable.Switch(project.DistinctUntilChanged(p => p?.Id)
                .Select(p => p is null ? Observable.Return<(RailProject? Project, RailWorkItems? Read)>((null, null))
                    : Observable.FromAsync(ct => read(p.Id, ct)).Catch(Observable.Return<RailWorkItems?>(null))
                        .Select(r => (Project: (RailProject?)p, Read: r))))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(next => {
                if (_disposables.IsDisposed) return;
                Heading = next.Project is null ? "Work items" : $"Work items · {next.Project.Name}";
                _all = next.Read?.Items.Select(Row).ToList() ?? [];
                Redraw(next.Project is null ? "Pick a project to see its work items"
                    : next.Read is null ? "Couldn't load work items"
                    : !next.Read.Available ? "This server has no work items" : null);
            })
            .DisposeWith(_disposables);
    }

    void Redraw(string? problem) {
        var q = _search.Trim();
        var shown = q.Length == 0 ? _all : [.. _all.Where(row => row.Title.Contains(q, StringComparison.OrdinalIgnoreCase))];
        Rows.ReplaceAll(shown);
        Count = q.Length == 0 ? $"{_all.Count} open" : $"{shown.Count} of {_all.Count} open";
        Message = problem ?? (shown.Count > 0 ? null : _all.Count > 0 ? "No work items match" : "No open work items");
    }

    static WorkItemsPageRow Row(RailWorkItem item) {
        var sessions = item.PartBySession.Count == 1 ? "1 session" : $"{item.PartBySession.Count} sessions";
        return new(item.Id, item.Title, item.PartsTotal == 0 ? sessions : $"{item.PartsDone} of {item.PartsTotal} parts · {sessions}");
    }

    public void Dispose() => _disposables.Dispose();
}
