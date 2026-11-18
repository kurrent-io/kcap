using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Every session the rail's scope and project admit, most recent first under day sections, read a page at a
/// time. A page only ever appends below the rows already shown.
public sealed class AllSessionsPageViewModel : ReactiveObject, IDisposable {
    public const int PageSize = 20;

    readonly CompositeDisposable _disposables = new();
    readonly IServerSessionsService _sessions;
    readonly TimeProvider _time;
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    RailScope _scope;
    RailProject? _project;
    int _offset;
    int _generation;
    DateOnly _lastDay;

    public ObservableCollection<AllSessionsDay> Days { get; } = [];

    string _heading = "All sessions";
    public string Heading { get => _heading; private set => this.RaiseAndSetIfChanged(ref _heading, value); }
    bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }
    bool _canShowMore;
    public bool CanShowMore { get => _canShowMore; private set => this.RaiseAndSetIfChanged(ref _canShowMore, value); }
    string? _message;
    public string? Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }

    public ReactiveCommand<Unit, Unit> ShowMoreCommand { get; }
    public ReactiveCommand<AllSessionsRow, Unit> OpenSessionCommand { get; }

    public AllSessionsPageViewModel(IServerSessionsService sessions, IObservable<(RailScope Scope, RailProject? Project)> filter,
            TimeProvider time, Action<string> openSession) {
        _sessions = sessions;
        _time = time;
        ShowMoreCommand = ReactiveCommand.CreateFromTask(LoadAsync,
            this.WhenAnyValue(x => x.CanShowMore, x => x.IsLoading, (more, loading) => more && !loading)).DisposeWith(_disposables);
        OpenSessionCommand = ReactiveCommand.Create<AllSessionsRow>(row => openSession(row.SessionId)).DisposeWith(_disposables);
        filter.DistinctUntilChanged().Subscribe(next => {
            (_scope, _project) = next;
            _generation++;
            _offset = 0;
            _seen.Clear();
            Days.Clear();
            CanShowMore = false;
            Heading = _project is null ? "All sessions" : $"All sessions · {_project.Name}";
            _ = LoadAsync();
        }).DisposeWith(_disposables);
    }

    async Task LoadAsync() {
        var generation = _generation;
        IsLoading = true;
        Message = null;
        var fetch = await _sessions.ReadPageAsync(
            new("all", _scope == RailScope.Mine ? "me" : null, PageSize, _offset, _project?.Id), CancellationToken.None);
        if (generation != _generation || _disposables.IsDisposed) return;
        IsLoading = false;
        if (fetch.Rows is not { } rows) {
            Message = "Couldn't load sessions";
            // Show more stays, as the way to ask again.
            CanShowMore = true;
            return;
        }
        _offset += PageSize;
        var now = _time.GetUtcNow();
        foreach (var row in rows)
            if (_seen.Add(row.SessionId)) Append(row, now);
        CanShowMore = rows.Length == PageSize && _offset < fetch.Total && _offset <= ServerSessionsService.OffsetCap;
        if (Days.Count == 0) Message = "No sessions yet";
    }

    void Append(RepoSessionDto row, DateTimeOffset now) {
        var zone = _time.LocalTimeZone;
        var at = row.EndedAt ?? row.LastActivityAt;
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
        if (Days.Count == 0 || day != _lastDay) {
            Days.Add(new(SessionRailViewModel.DayLabel(day, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime))));
            _lastDay = day;
        }
        string?[] meta = [row.Repo?.Name, row.WorkItem?.Title, row.WorkItem?.Part?.Title];
        Days[^1].Rows.Add(new(row.SessionId, row.Title is { Length: > 0 } title ? title : RailSessionViewModel.UntitledLabel,
            string.Join(" · ", meta.Where(part => !string.IsNullOrEmpty(part))), RelativeTime.Ago(at, now, zone)));
    }

    public void Dispose() => _disposables.Dispose();
}
