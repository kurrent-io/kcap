using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Pages;

/// The open work items of the rail's project under status sections, in the server's order within each: the rail's own
/// read. The owner filter keeps the items holding a session of that person; search narrows by title or person.
public sealed class WorkItemsPageViewModel : ReactiveObject, IPage {
    public PageKey Key => PageKey.WorkItems;
    const int PeopleShown = 3;
    readonly CompositeDisposable _disposables = new();
    IReadOnlyList<RailWorkItem> _all = [];
    IReadOnlySet<string> _viewer = FrozenSet<string>.Empty;
    string? _problem;

    public ObservableCollection<WorkItemsPageGroup> Groups { get; } = [];
    public ObservableCollection<SessionsPageChoice> Owners { get; } = [SessionsPageViewModel.AnyOwner, SessionsPageViewModel.You];

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
            Redraw();
        }
    }

    // A ComboBox writes null while its items change; that is not a pick.
    SessionsPageChoice _owner = SessionsPageViewModel.AnyOwner;
    public SessionsPageChoice Owner {
        get => _owner;
        set {
            if (value is null || value == _owner) return;
            this.RaiseAndSetIfChanged(ref _owner, value);
            Redraw();
        }
    }

    public ReactiveCommand<WorkItemsPageRow, Unit> OpenWorkItemCommand { get; }

    public WorkItemsPageViewModel(IObservable<(RailProject? Project, RailWorkItems? Read)> workItems,
            IObservable<RailScope> scope, IObservable<IReadOnlySet<string>> viewerIds, Action<string> openWorkItem) {
        OpenWorkItemCommand = ReactiveCommand.Create<WorkItemsPageRow>(row => openWorkItem(row.WorkItemId)).DisposeWith(_disposables);
        viewerIds.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(ids => {
            _viewer = ids;
            Redraw();
        }).DisposeWith(_disposables);
        scope.DistinctUntilChanged().ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(scope => {
            this.RaiseAndSetIfChanged(ref _owner, scope == RailScope.Mine ? SessionsPageViewModel.You : SessionsPageViewModel.AnyOwner, nameof(Owner));
            Redraw();
        }).DisposeWith(_disposables);
        workItems.ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(next => {
                if (_disposables.IsDisposed) return;
                Heading = next.Project is null ? "Work items" : $"Work items · {next.Project.Name}";
                _all = next.Read?.Items ?? [];
                Offer();
                _problem = next.Project is null ? "Pick a project to see its work items"
                    : next.Read is null ? "Couldn't load work items"
                    : !next.Read.Available ? "This server has no work items" : null;
                Redraw();
            })
            .DisposeWith(_disposables);
    }

    /// Owner options grow from the items read and never shrink, so a pick stays in its list.
    void Offer() {
        foreach (var person in _all.SelectMany(item => item.People))
            if (!_viewer.Contains(person.UserId) && Owners.All(o => o.Key != person.UserId))
                Owners.Add(new(person.UserId, person.Name ?? person.UserId));
    }

    void Redraw() {
        var shown = _all.Where(Matches).ToList();
        Groups.Clear();
        foreach (var status in shown.Select(item => item.Status).Distinct().Order()) {
            var group = new WorkItemsPageGroup(status);
            foreach (var item in shown.Where(item => item.Status == status)) group.Rows.Add(Row(item));
            Groups.Add(group);
        }
        Count = shown.Count == _all.Count ? $"{_all.Count} open" : $"{shown.Count} of {_all.Count} open";
        Message = _problem ?? (shown.Count > 0 ? null : _all.Count > 0 ? "No work items match" : "No open work items");
    }

    bool Matches(RailWorkItem item) {
        var people = item.People;
        if (_owner == SessionsPageViewModel.You ? !people.Any(p => _viewer.Contains(p.UserId))
            : _owner.Key is { } id && people.All(p => p.UserId != id)) return false;
        var q = _search.Trim();
        return q.Length == 0 || item.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || people.Any(p => p.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);
    }

    static WorkItemsPageRow Row(RailWorkItem item) {
        var sessions = item.PartBySession.Count == 1 ? "1 session" : $"{item.PartBySession.Count} sessions";
        var people = item.People.Take(PeopleShown)
            .Select(p => new WorkItemsPageRowPerson(p.Name ?? p.UserId, RailSessionViewModel.Initials(p.Name), p.AvatarUrl)).ToList();
        return new(item.Id, item.Title, item.PartsTotal == 0 ? sessions : $"{item.PartsDone} of {item.PartsTotal} parts · {sessions}",
            item.Status, people);
    }

    public void Dispose() => _disposables.Dispose();
}
