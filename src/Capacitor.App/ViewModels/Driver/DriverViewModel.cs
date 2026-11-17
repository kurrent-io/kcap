using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.Services.Driver;
using DynamicData;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Driver;

/// <summary>The Driver view: everything waiting on the person across their work items, then each work item in flight
/// with its parts. Status comes from the server; what agents are doing comes from the live sessions on each part, and
/// is never stored. Reads while open, every <see cref="RefreshEvery"/>.</summary>
public sealed class DriverViewModel : ReactiveObject, IDisposable {
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(30);

    readonly IDriverApi              _api;
    readonly TimeProvider            _time;
    readonly IScheduler              _scheduler;
    readonly Action<string>?         _openAgent;
    readonly Action<string>?         _openUrl;
    readonly CompositeDisposable     _subscriptions = new();
    readonly SerialDisposable        _polling       = new();
    IReadOnlyDictionary<string, AgentRow>           _live    = new Dictionary<string, AgentRow>();
    IReadOnlyList<PendingPermissionRequest>         _pending = [];
    DriverWorkItemsDto?                             _data;
    int                                             _reading;

    public DriverViewModel(IDriverApi api, TimeProvider time, IObservable<IChangeSet<AgentRow, string>>? agents = null,
            IObservable<IChangeSet<PendingPermissionRequest, string>>? pending = null, Action<string>? openAgent = null,
            Action<string>? openUrl = null, IScheduler? scheduler = null) {
        _api       = api;
        _time      = time;
        _scheduler = scheduler ?? RxSchedulers.MainThreadScheduler;
        _openAgent = openAgent;
        _openUrl   = openUrl;

        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        SelectCommand  = ReactiveCommand.Create<DriverWorkItemViewModel>(item => SelectedId = item.WorkItemId);
        BackCommand    = ReactiveCommand.Create(() => { SelectedId = null; });

        if (agents is not null)
            _subscriptions.Add(agents
                .Filter(row => row.SessionId is not null && !row.IsEndedSession)
                .ToCollection()
                .Select(rows => (IReadOnlyDictionary<string, AgentRow>)rows
                    .GroupBy(r => r.SessionId!, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal))
                .ObserveOn(_scheduler)
                .Subscribe(live => { _live = live; Project(); }));

        if (pending is not null)
            _subscriptions.Add(pending
                .ToCollection()
                .ObserveOn(_scheduler)
                .Subscribe(requests => { _pending = requests.ToList(); Project(); }));
    }

    public ReactiveCommand<Unit, Unit>                    RefreshCommand { get; }
    public ReactiveCommand<DriverWorkItemViewModel, Unit> SelectCommand  { get; }
    public ReactiveCommand<Unit, Unit>                    BackCommand    { get; }

    public DriverPhase Phase {
        get;
        private set {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(PhaseText));
            this.RaisePropertyChanged(nameof(ShowsPhase));
        }
    } = DriverPhase.Loading;

    public string PhaseText => Phase switch {
        DriverPhase.Loading     => "Reading your work items…",
        DriverPhase.SignedOut   => "Sign in to see your work items.",
        DriverPhase.Unavailable => "This server has no work items version 2, so there is nothing to drive yet.",
        DriverPhase.Failed      => "The server could not be read. Driver tries again shortly.",
        _                       => WorkItems.Count == 0 ? "No work items in flight." : ""
    };

    public bool ShowsPhase => PhaseText.Length > 0;

    public IReadOnlyList<DriverNeedViewModel> NeedsYou {
        get;
        private set {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasNeeds));
            this.RaisePropertyChanged(nameof(NeedsHeader));
        }
    } = [];

    public IReadOnlyList<DriverWorkItemViewModel> WorkItems {
        get;
        private set {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(WorkItemsHeader));
            this.RaisePropertyChanged(nameof(PhaseText));
            this.RaisePropertyChanged(nameof(ShowsPhase));
        }
    } = [];

    public string? SelectedId {
        get;
        private set {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(Selected));
            this.RaisePropertyChanged(nameof(IsOverview));
            this.RaisePropertyChanged(nameof(IsDetail));
        }
    }

    public DriverWorkItemViewModel? Selected => SelectedId is null ? null : WorkItems.FirstOrDefault(w => w.WorkItemId == SelectedId);
    public bool IsOverview => Selected is null;
    public bool IsDetail   => Selected is not null;

    public bool   HasNeeds        => NeedsYou.Count > 0;
    public string NeedsHeader     => $"Needs you · {NeedsYou.Count}";
    public string WorkItemsHeader => $"Work items in flight · {WorkItems.Count}";

    public string LiveSummary {
        get {
            var working = _live.Values.Count(r => DriverActivities.Of(r) == DriverActivity.Working);
            var idle    = _live.Values.Count(r => DriverActivities.Of(r) == DriverActivity.Idle);
            return $"{working} working · {idle} idle";
        }
    }

    /// <summary>Starts reading now and every <see cref="RefreshEvery"/> until <see cref="Deactivate"/>.</summary>
    public void Activate() =>
        _polling.Disposable = Observable.Timer(TimeSpan.Zero, RefreshEvery, _scheduler)
            .Select(_ => Observable.FromAsync(RefreshAsync))
            .Concat()
            .Subscribe(_ => { }, _ => { });

    public void Deactivate() => _polling.Disposable = Disposable.Empty;

    public async Task RefreshAsync() {
        if (Interlocked.Exchange(ref _reading, 1) == 1) return;
        try {
            var read = await _api.WorkItemsAsync(CancellationToken.None);
            Apply(read);
        } finally {
            Volatile.Write(ref _reading, 0);
        }
    }

    void Apply(NextWorkRead<DriverWorkItemsDto> read) {
        if (read.Kind == NextWorkReadKind.Ready && read.Value is { } data) {
            _data = data;
            Phase = DriverPhase.Ready;
        } else {
            Phase = read.Kind switch {
                NextWorkReadKind.SignedOut   => DriverPhase.SignedOut,
                NextWorkReadKind.Unavailable => DriverPhase.Unavailable,
                _                            => _data is null ? DriverPhase.Failed : Phase
            };
        }
        Project();
    }

    /// <summary>Rebuilds both lists from the last read, the live sessions and the pending requests.</summary>
    void Project() {
        var items = _data?.WorkItems ?? [];
        var now   = _time.GetUtcNow();
        var needs = new List<(DateTimeOffset At, DriverNeedViewModel Need, string? WorkItemId)>();

        var workItemOfSession = new Dictionary<string, (string Id, string Title)>(StringComparer.Ordinal);
        foreach (var item in items) {
            foreach (var s in item.Sessions) workItemOfSession.TryAdd(s.SessionId, (item.WorkItemId, item.Title));
            foreach (var part in item.Parts)
                foreach (var s in part.Sessions) workItemOfSession.TryAdd(s.SessionId, (item.WorkItemId, item.Title));
        }

        var askedAgents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in _pending) {
            askedAgents.Add(request.AgentId);
            var on    = workItemOfSession.GetValueOrDefault(request.SessionId);
            var title = request.IsQuestion ? QuestionText(request) : $"{request.ToolName} needs your approval";
            var agent = request.AgentId;
            needs.Add((request.RequestedAt, new DriverNeedViewModel(request.IsQuestion ? "Question" : "Permission", title,
                Context(on.Title, SessionTitle(request.SessionId)), Age(now, request.RequestedAt), "Answer",
                _openAgent is null ? null : () => _openAgent(agent)), on.Id));
        }

        foreach (var row in _live.Values.Where(r => DriverActivities.Of(r) == DriverActivity.WaitingOnYou && !askedAgents.Contains(r.Id))) {
            var on    = workItemOfSession.GetValueOrDefault(row.SessionId!);
            var at    = row.LastActivityAt is { } last ? new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)) : now;
            var agent = row.Id;
            needs.Add((at, new DriverNeedViewModel("Your turn", row.Title ?? "An agent finished its turn",
                Context(on.Title, null), Age(now, at), "Open", _openAgent is null ? null : () => _openAgent(agent)), on.Id));
        }

        foreach (var item in items)
            foreach (var part in item.Parts.Where(p => p.Status == "in_review")) {
                var pr  = part.Links.FirstOrDefault(l => l.Kind is "pr" or "pull_request" && l.Url is not null);
                var url = pr?.Url;
                needs.Add((item.LastActivityAt ?? now, new DriverNeedViewModel("Review", $"{part.Title}{(pr is null ? "" : $" · {pr.ShortKey}")}",
                    item.Title, "", url is null ? "Open" : "Open PR", url is null || _openUrl is null ? null : () => _openUrl(url)),
                    item.WorkItemId));
            }

        var needsByItem = needs.Where(n => n.WorkItemId is not null).GroupBy(n => n.WorkItemId!).ToDictionary(g => g.Key, g => g.Count());

        WorkItems = items
            .Select(item => Build(item, needsByItem.GetValueOrDefault(item.WorkItemId)))
            .OrderByDescending(w => w.Needs)
            .ThenByDescending(w => w.Working)
            .ThenByDescending(w => w.LastActivityAt)
            .ToList();
        NeedsYou = needs.OrderBy(n => n.At).Select(n => n.Need).ToList();
        this.RaisePropertyChanged(nameof(Selected));
        this.RaisePropertyChanged(nameof(IsOverview));
        this.RaisePropertyChanged(nameof(IsDetail));
        this.RaisePropertyChanged(nameof(LiveSummary));
    }

    DriverWorkItemViewModel Build(DriverWorkItemsDto.WorkItem item, int needs) {
        var titles = item.Parts.ToDictionary(p => p.WorkItemId, p => p.Title, StringComparer.Ordinal);
        var parts  = item.Parts.Select(part => {
            var ids      = part.Sessions.Select(s => s.SessionId).ToList();
            var activity = DriverActivities.Of(ids, _live);
            var agent    = ids.Select(id => _live.GetValueOrDefault(id)).FirstOrDefault(r => r is not null)?.Id;
            return new DriverPartViewModel(part, titles, activity, agent, _openAgent);
        }).ToList();
        var own = DriverActivities.Of(item.Sessions.Select(s => s.SessionId), _live);
        return new DriverWorkItemViewModel(item, parts, own, needs);
    }

    string? SessionTitle(string sessionId) => _live.GetValueOrDefault(sessionId)?.Title;

    static string Context(string? workItem, string? session) =>
        (workItem, session) switch {
            ({ } w, { } s) => $"{w} · {s}",
            ({ } w, null)  => w,
            (null, { } s)  => s,
            _              => "Not on a work item"
        };

    static string QuestionText(PendingPermissionRequest request) =>
        request.Questions?.Questions.FirstOrDefault()?.Question
     ?? request.AcpQuestion?.Prompt
     ?? "An agent asked you a question";

    static string Age(DateTimeOffset now, DateTimeOffset at) {
        var span = now - at;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h";
        return $"{(int)span.TotalDays} d";
    }

    public void Dispose() {
        _polling.Dispose();
        _subscriptions.Dispose();
    }
}
