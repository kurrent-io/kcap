using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Every session the rail's project admits, most recent first under date sections, read a page at a time. The
/// owner filter is the server's; search and the work-item filter narrow the rows already read, and Show more
/// reads further.
public sealed class AllSessionsPageViewModel : ReactiveObject, IDisposable {
    public const int PageSize = 20;

    public static readonly AllSessionsChoice AnyOwner = new(null, "Anyone");
    public static readonly AllSessionsChoice You = new("me", "You");
    public static readonly AllSessionsChoice AnyWorkItem = new(null, "Any work item");
    public static readonly AllSessionsChoice NoWorkItem = new("-", "No work item");

    readonly CompositeDisposable _disposables = new();
    readonly IServerSessionsService _sessions;
    readonly TimeProvider _time;
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    readonly List<(RepoSessionDto Session, AllSessionsRow Row)> _loaded = [];
    IReadOnlySet<string> _viewer = FrozenSet<string>.Empty;
    RailProject? _project;
    int _offset;
    int _total;
    int _generation;
    bool _failed;

    public ObservableCollection<AllSessionsDay> Days { get; } = [];
    public ObservableCollection<AllSessionsChoice> Owners { get; } = [AnyOwner, You];
    public ObservableCollection<AllSessionsChoice> WorkItems { get; } = [AnyWorkItem, NoWorkItem];

    string _heading = "Sessions";
    public string Heading { get => _heading; private set => this.RaiseAndSetIfChanged(ref _heading, value); }
    string _count = "";
    public string Count { get => _count; private set => this.RaiseAndSetIfChanged(ref _count, value); }
    bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }
    bool _canShowMore;
    public bool CanShowMore { get => _canShowMore; private set => this.RaiseAndSetIfChanged(ref _canShowMore, value); }
    string? _message;
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
    AllSessionsChoice _owner = AnyOwner;
    public AllSessionsChoice Owner {
        get => _owner;
        set {
            if (value is null || value == _owner) return;
            this.RaiseAndSetIfChanged(ref _owner, value);
            Reload();
        }
    }

    AllSessionsChoice _workItem = AnyWorkItem;
    public AllSessionsChoice WorkItem {
        get => _workItem;
        set {
            if (value is null || value == _workItem) return;
            this.RaiseAndSetIfChanged(ref _workItem, value);
            Redraw();
        }
    }

    public ReactiveCommand<Unit, Unit> ShowMoreCommand { get; }
    public ReactiveCommand<AllSessionsRow, Unit> OpenSessionCommand { get; }

    public AllSessionsPageViewModel(IServerSessionsService sessions, IObservable<(RailScope Scope, RailProject? Project)> filter,
            TimeProvider time, Action<string> openSession) {
        _sessions = sessions;
        _time = time;
        ShowMoreCommand = ReactiveCommand.CreateFromTask(LoadAsync,
            this.WhenAnyValue(x => x.CanShowMore, x => x.IsLoading, (more, loading) => more && !loading)).DisposeWith(_disposables);
        OpenSessionCommand = ReactiveCommand.Create<AllSessionsRow>(row => openSession(row.SessionId)).DisposeWith(_disposables);
        sessions.ViewerIds.Subscribe(ids => _viewer = ids).DisposeWith(_disposables);
        filter.DistinctUntilChanged().Subscribe(next => {
            if (next.Project?.Id != _project?.Id) {
                WorkItems.Clear();
                WorkItems.Add(AnyWorkItem);
                WorkItems.Add(NoWorkItem);
                this.RaiseAndSetIfChanged(ref _workItem, AnyWorkItem, nameof(WorkItem));
            }
            _project = next.Project;
            this.RaiseAndSetIfChanged(ref _owner, next.Scope == RailScope.Mine ? You : AnyOwner, nameof(Owner));
            Heading = _project is null ? "Sessions" : $"Sessions · {_project.Name}";
            Reload();
        }).DisposeWith(_disposables);
    }

    void Reload() {
        _generation++;
        _offset = 0;
        _total = 0;
        _seen.Clear();
        _loaded.Clear();
        Days.Clear();
        CanShowMore = false;
        _ = LoadAsync();
    }

    async Task LoadAsync() {
        var generation = _generation;
        IsLoading = true;
        Message = null;
        var fetch = await _sessions.ReadPageAsync(new("all", _owner.Key, PageSize, _offset, _project?.Id), CancellationToken.None);
        if (generation != _generation || _disposables.IsDisposed) return;
        IsLoading = false;
        _failed = fetch.Rows is null;
        if (fetch.Rows is not { } rows) {
            Message = "Couldn't load sessions";
            // Show more stays, as the way to ask again.
            CanShowMore = true;
            return;
        }
        _offset += PageSize;
        _total = fetch.Total;
        var now = _time.GetUtcNow();
        foreach (var session in rows) {
            if (!_seen.Add(session.SessionId)) continue;
            var agent = AgentRow.FromServer(session, hostedPresentation: false);
            var row = RowFor(session, agent, now);
            _loaded.Add((session, row));
            Offer(session, agent);
            if (Matches(session, row)) Place(session, row, now);
        }
        CanShowMore = rows.Length == PageSize && _offset < fetch.Total && _offset <= ServerSessionsService.OffsetCap;
        Describe();
    }

    void Redraw() {
        Days.Clear();
        var now = _time.GetUtcNow();
        foreach (var (session, row) in _loaded)
            if (Matches(session, row)) Place(session, row, now);
        Describe();
    }

    bool Narrowed => _search.Trim().Length > 0 || _workItem.Key is not null;

    void Describe() {
        var shown = Days.Sum(day => day.Rows.Count);
        Count = Narrowed ? $"{shown} of {_loaded.Count} loaded" : _total == 1 ? "1 session" : $"{_total} sessions";
        if (_failed || IsLoading) return;
        Message = shown > 0 ? null : _loaded.Count > 0 ? "No sessions match" : "No sessions yet";
    }

    bool Matches(RepoSessionDto session, AllSessionsRow row) {
        var workItem = session.WorkItem?.WorkItemId;
        if (_workItem.Key == NoWorkItem.Key ? workItem is not null : _workItem.Key is { } key && workItem != key) return false;
        var q = _search.Trim();
        if (q.Length == 0) return true;
        string?[] haystack = [row.Title, row.Meta, row.OwnerName];
        return haystack.Any(text => text?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);
    }

    /// Filter options grow from the rows read and never shrink, so a pick stays in its list.
    void Offer(RepoSessionDto session, AgentRow agent) {
        if (agent.OwnerUserId is { } id && !_viewer.Contains(id) && Owners.All(o => o.Key != id))
            Owners.Add(new(id, agent.OwnerName ?? id));
        if (session.WorkItem is { WorkItemId: { } item, Title: { } title } && WorkItems.All(w => w.Key != item))
            WorkItems.Add(new(item, title));
    }

    void Place(RepoSessionDto session, AllSessionsRow row, DateTimeOffset now) {
        var zone = _time.LocalTimeZone;
        var label = DateGroup(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(At(session), zone).DateTime),
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime));
        if (Days.Count == 0 || Days[^1].Label != label) Days.Add(new(label));
        Days[^1].Rows.Add(row);
    }

    AllSessionsRow RowFor(RepoSessionDto session, AgentRow agent, DateTimeOffset now) {
        string?[] meta = [session.Repo?.Name, session.WorkItem?.Title, session.WorkItem?.Part?.Title];
        var owner = agent.OwnerUserId is { } id && _viewer.Contains(id) || string.IsNullOrWhiteSpace(agent.OwnerName) ? null : agent.OwnerName;
        return new(session.SessionId, agent.Title is { Length: > 0 } title ? title : RailSessionViewModel.UntitledLabel,
            string.Join(" · ", meta.Where(part => !string.IsNullOrEmpty(part))), RelativeTime.Ago(At(session), now, _time.LocalTimeZone),
            SessionStatusDots.ForRow(agent, pending: false), string.IsNullOrEmpty(agent.Vendor) ? null : agent.Vendor,
            owner, RailSessionViewModel.Initials(owner), owner is null || string.IsNullOrEmpty(agent.OwnerAvatarUrl) ? null : agent.OwnerAvatarUrl);
    }

    static DateTimeOffset At(RepoSessionDto session) => session.EndedAt ?? session.LastActivityAt;

    internal static string DateGroup(DateOnly day, DateOnly today) {
        if (day >= today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        if (day >= today.AddDays(-7)) return "Previous 7 days";
        if (day >= today.AddDays(-30)) return "Previous 30 days";
        return day.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
    }

    public void Dispose() => _disposables.Dispose();
}
