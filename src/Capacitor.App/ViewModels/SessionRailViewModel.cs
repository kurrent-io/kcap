using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests;
using DynamicData;
using DynamicData.Binding;
using Lucide.Avalonia;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The session rail's root: groups → sessions over directory.Rows, grouped and sorted the user's way.
/// Ctor-scoped and disposable like HomeViewModel — the tree must be live from construction.
/// ONE ObserveOn at the top of the pipeline: nested group caches are mutated by this outer
/// pipeline, so every inner Connect() below already fires on the UI thread.
public sealed class SessionRailViewModel : ReactiveObject, IDisposable {
    public const string ScopeLockedTip = "Local version only stores your sessions. Upgrade to work with your team.";

    readonly CompositeDisposable _disposables = new();
    readonly IAgentDirectory _directory;
    readonly IServerSessionsService? _sessions;
    readonly Action<RailPreferences>? _persist;
    readonly TimeProvider _time;
    const int MaxArrivals = 2;
    static readonly TimeSpan StartedWithin = TimeSpan.FromMinutes(1);
    static readonly TimeSpan ArrivalWindow = TimeSpan.FromSeconds(2);
    readonly Dictionary<string, DateTimeOffset> _arrivals = new(StringComparer.Ordinal);
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    // Keep the user's folds when the directory empties/re-forms a group during refresh.
    HashSet<string> _collapsed;
    // Session ids and work item keys removed from the rail.
    HashSet<string> _removed;
    // The sessions of a removed work item, which leave the rail with it.
    FrozenSet<string> _hiddenByWorkItem = FrozenSet<string>.Empty;
    readonly Action? _closeWorkspace;
    readonly IReadOnlyList<RailAction> _actions;
    readonly Func<string, string, Task<bool>>? _fileSession;
    readonly Func<string, string, Task<bool>>? _unfileSession;
    // Drops shown ahead of the server: session id → the work item it was dropped on, null when dropped out. Each holds
    // until a read agrees or the server refuses it.
    readonly Dictionary<string, string?> _dropped = new(StringComparer.Ordinal);
    // Renames shown ahead of the server: work item id → its new title, until a read agrees.
    readonly Dictionary<string, string> _retitled = new(StringComparer.Ordinal);
    readonly Func<AgentRow, string, CancellationToken, Task<SessionRenameResult>>? _renameSession;
    readonly Func<string, string, Task<bool>>? _renameWorkItem;
    readonly Action<string>? _openWorkItem;
    readonly Func<string, string, Task<string?>>? _createWorkItem;
    SessionTitleEditorViewModel? _renaming;
    Action? _endRename;
    readonly IAppNotifier? _notifier;
    readonly BehaviorSubject<IReadOnlySet<string>> _marked = new(FrozenSet<string>.Empty);
    string? _markAnchor;
    bool _applying;

    // The selection is fed to the nested VMs via subjects, not this.WhenAnyValue: that
    // call routes through ReactiveUI's ObservableForProperty/RxAppBuilder global init, which is
    // only reliably primed when some other test has already pumped the headless dispatcher
    // first — AvaloniaSession.cs documents the MainThreadScheduler initialization race.
    readonly BehaviorSubject<string?> _selectedSessionKeyChanges = new(null);
    readonly BehaviorSubject<string?> _selectedWorkItemChanges = new(null);
    readonly BehaviorSubject<string?> _openSessionKeyChanges = new(null);
    // Project, removed and open row: what every row passes. Sessions outside work items also pass the Status filter.
    readonly BehaviorSubject<Func<AgentRow, bool>> _baseFilter;
    readonly BehaviorSubject<Func<AgentRow, bool>> _stateFilter;
    readonly Subject<Unit> _regroup = new();
    IReadOnlyDictionary<string, RunningWork> _work = FrozenDictionary<string, RunningWork>.Empty;
    readonly BehaviorSubject<IComparer<RailSessionViewModel>> _rowComparer;
    readonly BehaviorSubject<IComparer<RailGroupViewModel>> _groupComparer;
    readonly BehaviorSubject<IComparer<RailGroupViewModel>> _workItemComparer;
    readonly BehaviorSubject<RailGrouping> _groupingChanges;
    readonly BehaviorSubject<RailSort> _sortChanges;
    readonly BehaviorSubject<RailProject?> _projectChanges;
    readonly SourceCache<RailWorkItem, string> _workItems = new(item => WorkItemKey(item.Id));
    // Session id to the work item it sits in, for the work items shown.
    readonly BehaviorSubject<IReadOnlyDictionary<string, string>> _workItemOf = new(FrozenDictionary<string, string>.Empty);
    // Work item rows start folded; the ones opened stay open for the life of the app.
    readonly HashSet<string> _openWorkItems = new(StringComparer.Ordinal);
    // The last read of a project's open work items, in the server's order.
    IReadOnlyList<RailWorkItem> _read = [];
    string? _readProject;
    IReadOnlyList<string> _workItemOrder = [];
    bool _workItemsOffered;
    DateOnly _today;

    string? _openSessionKey;
    string? _openWorkItemId;
    int _opening;
    readonly Action<Action> _afterPaint;

    /// What the window has open; the rail selects the front one, a work item's page over a session.
    public string? OpenSessionKey {
        get => _openSessionKey;
        set {
            if (_disposables.IsDisposed) return;
            this.RaiseAndSetIfChanged(ref _openSessionKey, value);
            _openSessionKeyChanges.OnNext(value);
            ShowOpen();
            Refilter();
            _sessions?.Keep(value is { } key && key.StartsWith("server:", StringComparison.Ordinal) ? key["server:".Length..] : null);
        }
    }

    public string? OpenWorkItemId {
        get => _openWorkItemId;
        set {
            if (_disposables.IsDisposed) return;
            this.RaiseAndSetIfChanged(ref _openWorkItemId, value);
            ShowOpen();
        }
    }

    void ShowOpen() => ShowSelected(_openWorkItemId is null ? _openSessionKey : null, _openWorkItemId);

    void ShowSelected(string? sessionKey, string? workItemId) {
        _selectedSessionKeyChanges.OnNext(sessionKey);
        _selectedWorkItemChanges.OnNext(workItemId);
    }

    // A dispatcher priority is not enough: the frame is held back while the previous one is still drawing.
    static void PostAfterPaint(Action job) =>
        (Compositor.TryGetDefaultCompositor()?.RequestCommitAsync() ?? Task.CompletedTask)
            .ContinueWith(_ => Dispatcher.UIThread.Post(job), TaskScheduler.Default);

    Action<string> SelectingFirst(string keyPrefix, Action<string> open) => id => {
        if (_disposables.IsDisposed) return;
        var opening = ++_opening;
        ShowSelected(keyPrefix + id, null);
        _afterPaint(() => {
            if (opening != _opening || _disposables.IsDisposed) return;
            open(id);
            ShowOpen();
        });
    };

    RailScope _scope;
    public RailScope Scope {
        get => _scope;
        set {
            if (_disposables.IsDisposed) return;
            if (IsScopeLocked) value = RailScope.Mine;
            if (_scope == value) return;
            this.RaiseAndSetIfChanged(ref _scope, value);
            this.RaisePropertyChanged(nameof(IsMine));
            this.RaisePropertyChanged(nameof(IsTeam));
            if (_sessions is not null) _sessions.Scope = value;
            this.RaisePropertyChanged(nameof(FiltersDiffer));
            Persist();
        }
    }

    public bool IsMine => _scope == RailScope.Mine;
    public bool IsTeam => _scope == RailScope.Team;
    /// The local edition holds only the owner's sessions, so Team has nothing to show.
    public bool IsScopeLocked { get; }
    public bool CanChangeScope => !IsScopeLocked;
    public string? ScopeTip => IsScopeLocked ? ScopeLockedTip : null;

    RailState _state;
    public RailState State {
        get => _state;
        set {
            if (_disposables.IsDisposed) return;
            if (_state == value) return;
            this.RaiseAndSetIfChanged(ref _state, value);
            this.RaisePropertyChanged(nameof(IsAll));
            this.RaisePropertyChanged(nameof(IsLive));
            this.RaisePropertyChanged(nameof(IsEnded));
            Refilter();
            this.RaisePropertyChanged(nameof(FiltersDiffer));
            Persist();
        }
    }

    public bool IsAll => _state == RailState.All;
    public bool IsLive => _state == RailState.Live;
    public bool IsEnded => _state == RailState.Ended;

    RailGrouping _grouping;
    /// How the sessions outside work items are grouped.
    public RailGrouping Grouping {
        get => _grouping;
        set {
            if (_disposables.IsDisposed) return;
            value = Readable(value);
            if (_grouping == value) return;
            this.RaiseAndSetIfChanged(ref _grouping, value);
            this.RaisePropertyChanged(nameof(GroupingLabel));
            this.RaisePropertyChanged(nameof(FiltersDiffer));
            _groupComparer.OnNext(GroupComparer(value));
            _groupingChanges.OnNext(value);
            _regroup.OnNext(Unit.Default);
            Persist();
        }
    }

    static RailGrouping Readable(RailGrouping stored) => stored == RailGrouping.WorkItem ? RailGrouping.Repository : stored;

    /// The work items section shows once a project is picked on a server with work items version 2.
    public bool OffersWorkItem => _shownProject is not null && _workItemsOffered;
    public bool ShowsWorkItems => OffersWorkItem && _workItems.Count > 0;

    public string GroupingLabel => GroupingName(_grouping);

    public static string GroupingName(RailGrouping grouping) => grouping switch {
        RailGrouping.None => "No grouping",
        RailGrouping.Date => "Date",
        RailGrouping.Status => "Status",
        _ => "Repository",
    };

    RailSort _sort;
    public RailSort Sort {
        get => _sort;
        set {
            if (_disposables.IsDisposed) return;
            if (_sort == value) return;
            this.RaiseAndSetIfChanged(ref _sort, value);
            this.RaisePropertyChanged(nameof(SortLabel));
            _rowComparer.OnNext(RowComparer(value, Descending));
            _sortChanges.OnNext(value);
            this.RaisePropertyChanged(nameof(FiltersDiffer));
            Persist();
        }
    }

    public string SortLabel => SortName(_sort);

    public static string SortName(RailSort sort) => sort switch {
        RailSort.LastUpdated => "Last updated",
        RailSort.Created => "Created",
        _ => "Title",
    };

    bool _descending;
    public bool Descending {
        get => _descending;
        set {
            if (_disposables.IsDisposed) return;
            if (_descending == value) return;
            this.RaiseAndSetIfChanged(ref _descending, value);
            this.RaisePropertyChanged(nameof(DirectionLabel));
            _rowComparer.OnNext(RowComparer(Sort, value));
            this.RaisePropertyChanged(nameof(FiltersDiffer));
            Persist();
        }
    }

    public string DirectionLabel => _descending ? "Descending" : "Ascending";

    public const string AllProjectsLabel = "All projects";

    RailProject? _project;
    /// Null shows every session.
    public RailProject? Project {
        get => _project;
        set {
            if (_disposables.IsDisposed) return;
            if (Equals(_project, value)) return;
            this.RaiseAndSetIfChanged(ref _project, value);
            this.RaisePropertyChanged(nameof(ProjectLabel));
            this.RaisePropertyChanged(nameof(ProjectInitial));
            this.RaisePropertyChanged(nameof(IsAllProjects));
            ProjectOptions = Options(_projects, value);
            _projectChanges.OnNext(value);
            if (_sessions is null) ShowProject();
            else {
                _sessions.Project = value?.Id;
                // A read that fails or never goes out still shows the project, from what is already listed.
                _showProjectFallback.Disposable = Observable.FromAsync(ct => Task.Delay(ShowProjectFallbackAfter, _time, ct))
                    .ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(_ => ShowProject());
            }
            Persist();
        }
    }

    /// The project the list shows: the previous one until the picked one's sessions land, so it changes once instead of emptying and refilling.
    RailProject? _shownProject;
    readonly SerialDisposable _showProjectFallback = new();
    static readonly TimeSpan ShowProjectFallbackAfter = TimeSpan.FromSeconds(2);

    /// Raised before the list changes project, which lands over several posted passes.
    public event Action? ProjectShowing;

    void ShowProject() {
        _showProjectFallback.Disposable = null;
        if (_disposables.IsDisposed || Equals(_shownProject, _project)) return;
        ProjectShowing?.Invoke();
        _shownProject = _project;
        Refilter();
        ApplyWorkItems();
    }

    public string ProjectLabel => _project?.Name ?? AllProjectsLabel;
    public string? ProjectInitial => _project?.Initial;
    public bool IsAllProjects => _project is null;

    // Null until the first read: a remembered project is kept as it was saved until then.
    IReadOnlyList<RailProject>? _projects;

    IReadOnlyList<RailProjectOption> _projectOptions = [];
    public IReadOnlyList<RailProjectOption> ProjectOptions {
        get => _projectOptions;
        private set {
            this.RaiseAndSetIfChanged(ref _projectOptions, value);
            this.RaisePropertyChanged(nameof(HasProjects));
        }
    }

    public bool HasProjects => _projectOptions.Count > 0;

    /// True when any of the five controls differs from the defaults, or sessions are removed, however the panel
    /// is collapsed.
    public bool FiltersDiffer {
        get {
            var defaults = RailPreferences.Default;
            return _scope != defaults.Scope || _state != defaults.State || _grouping != defaults.Grouping
                || _sort != defaults.Sort || _descending != defaults.Descending || HasRemoved;
        }
    }

    public ReactiveCommand<RailScope, Unit> SetScopeCommand { get; }
    public ReactiveCommand<RailState, Unit> SetStateCommand { get; }
    public ReactiveCommand<RailGrouping, Unit> SetGroupingCommand { get; }
    public ReactiveCommand<RailSort, Unit> SetSortCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleDirectionCommand { get; }
    public ReactiveCommand<RailProject?, Unit> SetProjectCommand { get; }
    public ReactiveCommand<Unit, Unit> RestoreRemovedCommand { get; }
    /// Folds every group while any is open, else opens them all.
    public ReactiveCommand<Unit, Unit> ToggleAllExpandedCommand { get; }

    public bool HasRemoved => _removed.Count > 0;
    public string RestoreRemovedText {
        get {
            var workItems = _removed.Count(IsWorkItemKey);
            var sessions = _removed.Count - workItems;
            return "Show " + string.Join(" and ", new[] { Counted(sessions, "removed session"), Counted(workItems, "removed work item") }.OfType<string>());

            static string? Counted(int count, string noun) => count == 0 ? null : $"{count} {noun}{(count == 1 ? "" : "s")}";
        }
    }

    public RailPreferences Preferences =>
        new(Scope, State, Grouping, Sort, Descending, [.. _collapsed.Order(StringComparer.Ordinal)], Project,
            [.. _removed.Order(StringComparer.Ordinal)]);

    readonly ObservableAsPropertyHelper<bool> _isEmpty;
    public bool IsEmpty => _isEmpty.Value;
    readonly ObservableAsPropertyHelper<bool> _anyExpanded;
    public bool AnyExpanded => _anyExpanded.Value;

    readonly ObservableAsPropertyHelper<bool> _showsViewAll;
    /// The listing holds ended sessions older than the ones the rail shows.
    public bool ShowsViewAll => _showsViewAll.Value;
    readonly ObservableAsPropertyHelper<string?> _liveNotShownText;
    public string? LiveNotShownText => _liveNotShownText.Value;
    public ReactiveCommand<Unit, Unit> OpenPreviousSessionCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenNextSessionCommand { get; }

    readonly ObservableCollectionExtended<RailGroupViewModel> _workItemGroupsSource = new();
    /// The picked project's open work items with a session worked on in the last three days that the rail lists, in the
    /// server's order.
    public ReadOnlyObservableCollection<RailGroupViewModel> WorkItemGroups { get; }
    readonly ObservableCollectionExtended<RailGroupViewModel> _sessionGroupsSource = new();
    /// The sessions outside those work items, grouped, sorted and filtered the user's way.
    public ReadOnlyObservableCollection<RailGroupViewModel> SessionGroups { get; }
    /// Both sections in the order the rail draws them.
    public IReadOnlyList<RailGroupViewModel> Groups => [.. _workItemGroupsSource, .. _sessionGroupsSource];

    /// The picked project's open work items, read again on every change to them; the Work items page shows the same read.
    /// Null for a read that failed, only when the project has had no read yet.
    public IObservable<(RailProject? Project, RailWorkItems? Read)> ProjectWorkItems { get; }

    readonly ObservableAsPropertyHelper<(int Count, int Failed)> _attention;
    /// Independent of display filters: a hidden row can still need the user.
    public (int Count, int Failed) Attention => _attention.Value;

    public SessionRailViewModel(
            IAgentDirectory directory,
            Action<string> openLocalSession, Action<string> openRemoteSession, TimeProvider time,
            IObservable<IReadOnlySet<string>>? agentsWithPending = null,
            IObservable<IReadOnlySet<string>>? agentsAwaitingAnswer = null,
            IObservable<IReadOnlyDictionary<string, RunningWork>>? agentsRunningWork = null,
            IObservable<IReadOnlyDictionary<string, RailPullRequestSummary>>? pullRequests = null,
            Action<string, AgentOrigin, PullRequestSubjectDto?>? openPullRequest = null,
            Action<string>? openServerSession = null,
            IServerSessionsService? sessions = null,
            RailPreferences? preferences = null,
            Action<RailPreferences>? persistPreferences = null,
            bool scopeLocked = false,
            IObserver<Func<AgentRow, bool>>? shownRows = null,
            Func<string, Task<RailPreferences>>? bindUserPreferences = null,
            IObservable<IReadOnlyList<RailProject>>? projects = null,
            Func<string, CancellationToken, Task<RailWorkItems?>>? readWorkItems = null,
            IObservable<Unit>? workItemsChanged = null,
            IObservable<IReadOnlySet<string>>? suggestedSessions = null,
            Action<AgentRow>? stopSession = null, Action? closeWorkspace = null,
            Action<Action>? afterPaint = null,
            Func<string, string, Task<bool>>? fileSession = null, Func<string, string, Task<bool>>? unfileSession = null,
            IAppNotifier? notifier = null,
            Func<AgentRow, string, CancellationToken, Task<SessionRenameResult>>? renameSession = null,
            Func<AgentRow, bool>? canRenameSession = null,
            Func<string, string, Task<bool>>? renameWorkItem = null,
            Func<string, string, Task<string?>>? createWorkItem = null,
            Action<string>? openWorkItem = null) {
        _directory = directory;
        _createWorkItem = createWorkItem;
        _renameSession = renameSession;
        _renameWorkItem = renameWorkItem;
        _openWorkItem = openWorkItem;
        _fileSession = fileSession;
        _unfileSession = unfileSession;
        _notifier = notifier;
        _closeWorkspace = closeWorkspace;
        _afterPaint = afterPaint ?? PostAfterPaint;
        openLocalSession = SelectingFirst("local:", openLocalSession);
        openRemoteSession = SelectingFirst("remote:", openRemoteSession);
        if (openServerSession is not null) openServerSession = SelectingFirst("server:", openServerSession);
        _actions = [
            new(LucideIconKind.Pencil, _ => "Rename",
                row => renameSession is not null && canRenameSession is not null && row.SessionId is { Length: > 0 } && canRenameSession(row),
                rows => BeginRename(rows[0]), SingleRow: true),
            new(LucideIconKind.FolderPlus, _ => "Convert to work item",
                row => createWorkItem is not null && OffersWorkItem && row.Title is { Length: > 0 } && row.SessionId is { Length: > 0 } id && !IsFiled(id),
                rows => _ = ConvertToWorkItemAsync(rows[0]), SingleRow: true),
            new(LucideIconKind.CircleStop, rows => rows[0].IsAttached ? AttachedAgentCopy.DisconnectSteering : "Stop",
                row => stopSession is not null && RailSessionViewModel.Stoppable(row), rows => stopSession!(rows[0]), SingleRow: true),
            new(LucideIconKind.EyeOff, rows => rows.Count == 1 ? "Remove from sidebar" : $"Remove {rows.Count} from sidebar",
                row => SessionIds.Canonical(row.SessionId) is not null, Remove),
        ];
        _disposables.Add(_marked);
        _disposables.Add(_showProjectFallback);
        _sessions = sessions;
        _persist = persistPreferences;
        _time = time;
        var initial = preferences ?? RailPreferences.Default;
        IsScopeLocked = scopeLocked;
        _scope = scopeLocked ? RailScope.Mine : initial.Scope;
        _state = initial.State;
        _grouping = Readable(initial.Grouping);
        _sort = initial.Sort;
        _descending = initial.Descending;
        _project = initial.Project;
        _shownProject = _project;
        // Assumed until a read says otherwise, so a project opens in its work items rather than flickering through repositories.
        _workItemsOffered = readWorkItems is not null;
        _collapsed = new HashSet<string>(initial.Collapsed, StringComparer.Ordinal);
        _removed = new HashSet<string>(initial.Removed ?? [], StringComparer.Ordinal);
        _today = LocalDay(_time.GetUtcNow());
        if (sessions is not null) {
            sessions.Scope = _scope;
            sessions.Project = _project?.Id;
        }
        SetScopeCommand = ReactiveCommand.Create<RailScope>(scope => Scope = scope).DisposeWith(_disposables);
        SetStateCommand = ReactiveCommand.Create<RailState>(state => State = state).DisposeWith(_disposables);
        SetGroupingCommand = ReactiveCommand.Create<RailGrouping>(grouping => Grouping = grouping).DisposeWith(_disposables);
        SetSortCommand = ReactiveCommand.Create<RailSort>(sort => Sort = sort).DisposeWith(_disposables);
        ToggleDirectionCommand = ReactiveCommand.Create(() => { Descending = !Descending; }).DisposeWith(_disposables);
        SetProjectCommand = ReactiveCommand.Create<RailProject?>(project => Project = project).DisposeWith(_disposables);
        RestoreRemovedCommand = ReactiveCommand.Create(() => SetRemoved([], persist: true)).DisposeWith(_disposables);
        ToggleAllExpandedCommand = ReactiveCommand.Create(ToggleAllExpanded).DisposeWith(_disposables);
        OpenPreviousSessionCommand = ReactiveCommand.Create(() => { OpenAdjacent(-1); }).DisposeWith(_disposables);
        OpenNextSessionCommand = ReactiveCommand.Create(() => { OpenAdjacent(1); }).DisposeWith(_disposables);
        _baseFilter = new BehaviorSubject<Func<AgentRow, bool>>(BaseFilter()).DisposeWith(_disposables);
        _stateFilter = new BehaviorSubject<Func<AgentRow, bool>>(StateFilter()).DisposeWith(_disposables);
        _rowComparer = new BehaviorSubject<IComparer<RailSessionViewModel>>(RowComparer(_sort, _descending)).DisposeWith(_disposables);
        _groupComparer = new BehaviorSubject<IComparer<RailGroupViewModel>>(GroupComparer(_grouping)).DisposeWith(_disposables);
        _workItemComparer = new BehaviorSubject<IComparer<RailGroupViewModel>>(WorkItemComparer([])).DisposeWith(_disposables);
        _groupingChanges = new BehaviorSubject<RailGrouping>(_grouping).DisposeWith(_disposables);
        _sortChanges = new BehaviorSubject<RailSort>(_sort).DisposeWith(_disposables);
        _projectChanges = new BehaviorSubject<RailProject?>(_project).DisposeWith(_disposables);
        _disposables.Add(_regroup);
        _disposables.Add(_workItems);
        _disposables.Add(_workItemOf);
        if (shownRows is not null)
            _stateFilter.CombineLatest(_baseFilter, _workItemOf, (state, all, workItemOf) => (Func<AgentRow, bool>)(row =>
                    state(row) || all(row) && row.SessionId is { } id && workItemOf.ContainsKey(id)))
                .Subscribe(shownRows.OnNext).DisposeWith(_disposables);
        directory.Rows.Connect().Subscribe(NoteArrivals).DisposeWith(_disposables);

        var ageTicks = new Subject<Unit>().DisposeWith(_disposables);
        time.CreateTimer(_ => RxSchedulers.MainThreadScheduler.Schedule(() => { if (!ageTicks.IsDisposed) ageTicks.OnNext(Unit.Default); }),
            null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)).DisposeWith(_disposables);
        // The day groups are named against today; at midnight the names move, the keys do not.
        ageTicks.Subscribe(_ => {
            var today = LocalDay(_time.GetUtcNow());
            if (today == _today) return;
            _today = today;
            foreach (var group in _sessionGroupsSource.Where(g => g.Kind == RailGrouping.Date)) group.Relabel(LabelFor(group.Key, RailGrouping.Date));
        }).DisposeWith(_disposables);
        IObservable<string?> selected = _selectedSessionKeyChanges;
        // PermissionService.AgentsWithPending emits from background continuations; marshal once
        // here so every nested OAPH downstream sees
        // it on the UI thread without adding its own ObserveOn.
        var pending = (agentsWithPending ?? Observable.Return<IReadOnlySet<string>>(FrozenSet<string>.Empty))
            .ObserveOn(RxSchedulers.MainThreadScheduler).Replay(1).RefCount();
        var answering = (agentsAwaitingAnswer ?? Observable.Return<IReadOnlySet<string>>(FrozenSet<string>.Empty))
            .ObserveOn(RxSchedulers.MainThreadScheduler).Replay(1).RefCount();
        var suggested = suggestedSessions?.ObserveOn(RxSchedulers.MainThreadScheduler).Replay(1).RefCount();
        var attentionFilter = pending.CombineLatest(answering, (permissions, answers) =>
            (Func<AgentRow, bool>)(row => SessionStatusDots.NeedsAttention(row) || permissions.Contains(row.Id) || answers.Contains(row.Id)));
        _attention = directory.Rows.Connect()
            .Filter(attentionFilter)
            .QueryWhenChanged(rows => (Count: rows.Count, Failed: rows.Items.Count(row => row.Status == "Failed")))
            .DistinctUntilChanged()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.Attention, initialValue: (0, 0))
            .DisposeWith(_disposables);
        // Marshaled once here, like `pending` above, so every nested OAPH downstream sees it on
        // the UI thread without its own ObserveOn.
        var stale = directory.RemoteStale.ObserveOn(RxSchedulers.MainThreadScheduler).Replay(1).RefCount();
        var prs = (pullRequests ?? Observable.Return<IReadOnlyDictionary<string, RailPullRequestSummary>>(
                FrozenDictionary<string, RailPullRequestSummary>.Empty)).ObserveOn(RxSchedulers.MainThreadScheduler).Replay(1).RefCount();
        var localOnAppServer = directory.LocalDaemonOnAppServer.ObserveOn(RxSchedulers.MainThreadScheduler).Replay(1).RefCount();
        var work = (agentsRunningWork ?? Observable.Return<IReadOnlyDictionary<string, RunningWork>>(
                FrozenDictionary<string, RunningWork>.Empty)).ObserveOn(RxSchedulers.MainThreadScheduler).Replay(1).RefCount();

        var paging = (sessions?.Paging ?? Observable.Return(new ServerSessionsPaging(0, 0, 1, false, false)))
            .ObserveOn(RxSchedulers.MainThreadScheduler).Replay(1).RefCount();
        var endedPaging = paging.CombineLatest(_stateFilter.Select(_ => State).DistinctUntilChanged(),
                (p, state) => (Page: p, Visible: state != RailState.Live))
            .Replay(1).RefCount();
        var endedListPartial = endedPaging.Select(p => p.Visible && p.Page.Loaded < p.Page.Total)
            .DistinctUntilChanged().Replay(1).RefCount();
        _showsViewAll = endedListPartial.ToProperty(this, x => x.ShowsViewAll, initialValue: false).DisposeWith(_disposables);
        _liveNotShownText = (sessions?.LiveNotShown ?? Observable.Return(0))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Select(n => n > 0 ? $"{n} more live session{(n == 1 ? "" : "s")} · narrow the scope" : null)
            .ToProperty(this, x => x.LiveNotShownText, initialValue: null).DisposeWith(_disposables);

        // The lane names the viewer after the rail has opened in the last user's shape; a read that
        // fails keeps the shape already showing.
        if (sessions is not null && bindUserPreferences is not null)
            Observable.Switch(sessions.ViewerUserId
                    .Where(id => id is { Length: > 0 })
                    .DistinctUntilChanged()
                    .Select(id => Observable.FromAsync(() => bindUserPreferences(id!)).Catch(Observable.Empty<RailPreferences>())))
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(ApplyPreferences)
                .DisposeWith(_disposables);

        projects?.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(list => {
            if (_disposables.IsDisposed) return;
            _projects = list;
            ProjectOptions = Options(list, _project);
            Project = Current(_project);
        }).DisposeWith(_disposables);

        ProjectWorkItems = readWorkItems is null ? Observable.Never<(RailProject?, RailWorkItems?)>()
            : Observable.Switch(_projectChanges.DistinctUntilChanged(project => project?.Id)
                    .Select(project => project is null ? Observable.Return<(RailProject?, RailWorkItems?)>((null, null))
                        : Observable.Switch((workItemsChanged ?? Observable.Never<Unit>()).StartWith(Unit.Default)
                                .Select(_ => Observable.FromAsync(ct => readWorkItems(project.Id, ct)).Catch(Observable.Return<RailWorkItems?>(null))))
                            // A failed re-read keeps the last one showing.
                            .Where((read, at) => at == 0 || read is not null)
                            .Select(read => ((RailProject?)project, read))))
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Replay(1).RefCount();
        ProjectWorkItems.Subscribe(next => {
            if (_disposables.IsDisposed || next.Project is not { } project || project.Id != _project?.Id || next.Read is not { } read) return;
            _workItemsOffered = read.Available;
            _readProject = project.Id;
            _read = read.Items;
            foreach (var item in _read)
                if (_retitled.GetValueOrDefault(item.Id) == item.Title) _retitled.Remove(item.Id);
            foreach (var drop in _dropped.Where(drop => drop.Value == _read.FirstOrDefault(item => item.PartBySession.ContainsKey(drop.Key))?.Id).ToList())
                _dropped.Remove(drop.Key);
            ApplyWorkItems();
        }).DisposeWith(_disposables);

        sessions?.Snapshots.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(snapshot => {
            if (!_disposables.IsDisposed && !Equals(_shownProject, _project) && snapshot.ListedProject == _project?.Id) ShowProject();
        }).DisposeWith(_disposables);

        WorkItemGroups = new ReadOnlyObservableCollection<RailGroupViewModel>(_workItemGroupsSource);
        SessionGroups = new ReadOnlyObservableCollection<RailGroupViewModel>(_sessionGroupsSource);
        var outsideWorkItems = _stateFilter.CombineLatest(_workItemOf, (state, workItemOf) =>
            (Func<AgentRow, bool>)(row => state(row) && !(row.SessionId is { } id && workItemOf.ContainsKey(id))));
        directory.Rows.Connect()
            .Filter(outsideWorkItems)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Group(GroupKeyOf, _regroup)
            .Transform(g => new RailGroupViewModel(
                g.Key, g.Cache.Connect(), _grouping, LabelFor(g.Key, _grouping, g), selected, pending, stale, openLocalSession, openRemoteSession, time,
                answering, work, prs, localOnAppServer, openPullRequest, !_collapsed.Contains(g.Key), expanded => {
                    if (expanded) _collapsed.Remove(g.Key);
                    else _collapsed.Add(g.Key);
                    Persist();
                }, ageTicks, openServerSession, _groupingChanges, _rowComparer, _sortChanges, suggested: suggested, marked: _marked))
            .DisposeMany()
            .SortAndBind(_sessionGroupsSource, _groupComparer)
            .Subscribe()
            .DisposeWith(_disposables);
        _workItems.Connect()
            .TransformWithInlineUpdate(item => {
                var key = WorkItemKey(item.Id);
                var rows = directory.Rows.Connect()
                    .Filter(_baseFilter.CombineLatest(_workItemOf, (all, workItemOf) => (Func<AgentRow, bool>)(row =>
                        all(row) && row.SessionId is { } id && workItemOf.GetValueOrDefault(id) == item.Id)))
                    .ObserveOn(RxSchedulers.MainThreadScheduler);
                return new RailGroupViewModel(
                    key, rows, RailGrouping.None, item.Title, selected, pending, stale, openLocalSession, openRemoteSession, time, answering, work,
                    prs, localOnAppServer, openPullRequest, _openWorkItems.Contains(key), expanded => {
                        if (expanded) _openWorkItems.Add(key);
                        else _openWorkItems.Remove(key);
                    }, ageTicks, openServerSession, Observable.Return(RailGrouping.None), _rowComparer, _sortChanges, item,
                    marked: _marked, selectedWorkItem: _selectedWorkItemChanges, openSessionKey: _openSessionKeyChanges);
            }, (group, item) => group.UpdateWorkItem(item))
            .DisposeMany()
            // A work item whose sessions sit outside the project's repositories has none for the rail to list.
            .FilterOnObservable(group => group.HasSessions)
            .SortAndBind(_workItemGroupsSource, _workItemComparer)
            .Subscribe()
            .DisposeWith(_disposables);
        work.Subscribe(next => {
            _work = next;
            if (_grouping == RailGrouping.Status) _regroup.OnNext(Unit.Default);
        }).DisposeWith(_disposables);
        // The groups, not the directory: a state filter that leaves nothing is an empty rail too.
        _isEmpty = Observable.Merge(Changed(_workItemGroupsSource), Changed(_sessionGroupsSource))
            .Select(_ => Groups.Count == 0)
            .DistinctUntilChanged()
            .ToProperty(this, x => x.IsEmpty, initialValue: Groups.Count == 0)
            .DisposeWith(_disposables);
        _anyExpanded = _sessionGroupsSource.ToObservableChangeSet()
            .AutoRefresh(group => group.IsExpanded)
            .ToCollection()
            .Select(groups => groups.Any(group => group.IsExpanded))
            .DistinctUntilChanged()
            .ToProperty(this, x => x.AnyExpanded, initialValue: _sessionGroupsSource.Any(group => group.IsExpanded))
            .DisposeWith(_disposables);

        static IObservable<Unit> Changed(INotifyCollectionChanged groups) =>
            Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                handler => groups.CollectionChanged += handler, handler => groups.CollectionChanged -= handler).Select(_ => Unit.Default);
    }

    void ToggleAllExpanded() {
        var expand = !AnyExpanded;
        _applying = true;
        try {
            foreach (var group in SessionGroups.ToList()) group.SetExpanded(expand);
        } finally {
            _applying = false;
        }
        Persist();
    }

    /// Another user's remembered shape, or this user's set now that the lane has named them: applied
    /// without writing it back.
    public void ApplyPreferences(RailPreferences preferences) {
        if (_disposables.IsDisposed) return;
        _applying = true;
        try {
            Scope = preferences.Scope;
            State = preferences.State;
            Grouping = preferences.Grouping;
            Sort = preferences.Sort;
            Descending = preferences.Descending;
            Project = Current(preferences.Project);
            _collapsed = new HashSet<string>(preferences.Collapsed, StringComparer.Ordinal);
            SetRemoved(preferences.Removed ?? [], persist: false);
            foreach (var group in SessionGroups) group.ApplyExpanded(!_collapsed.Contains(group.Key));
        } finally {
            _applying = false;
        }
    }

    public void NotifySessionOpened(string agentId, AgentOrigin origin) {
        var key = origin switch {
            AgentOrigin.Remote => $"remote:{agentId}",
            AgentOrigin.Server => $"server:{agentId}",
            _ => $"local:{agentId}",
        };
        var row = _directory.Rows.Lookup(key);
        if (!row.HasValue && origin == AgentOrigin.Local)
            row = _directory.Rows.Lookup($"pending:{agentId}");
        if (!row.HasValue) return;
        var groupKey = row.Value.SessionId is { } id && _workItemOf.Value.GetValueOrDefault(id) is { } workItem
            ? WorkItemKey(workItem) : GroupKeyOf(row.Value);
        _collapsed.Remove(groupKey);
        Groups.FirstOrDefault(group => group.Key == groupKey)?.Expand();
    }

    /// Opens the row `step` places from the selected one in the order the rail draws, skipping rows
    /// in folded groups and stopping at either end. With nothing selected it starts from the end it
    /// moves away from.
    internal bool OpenAdjacent(int step) {
        if (_disposables.IsDisposed) return false;
        var rows = Groups.SelectMany(group => group.Sessions.Select(row => (Row: row, Shown: group.IsExpanded))).ToList();
        var at = rows.FindIndex(r => r.Row.IsSelected);
        if (at < 0) at = step > 0 ? -1 : rows.Count;
        for (var i = at + step; i >= 0 && i < rows.Count; i += step) {
            if (!rows[i].Shown) continue;
            rows[i].Row.OpenCommand.Execute().Subscribe();
            return true;
        }
        return false;
    }

    /// Hides the session for this user, by its id, so it stays gone when its daemon row hands over to the server's.
    void Remove(IReadOnlyList<AgentRow> rows) {
        if (_disposables.IsDisposed) return;
        if (rows.Any(row => row.Key == _openSessionKey)) _closeWorkspace?.Invoke();
        SetRemoved(_removed.Concat(rows.Select(row => SessionIds.Canonical(row.SessionId)).OfType<string>()), persist: true);
    }

    void RemoveWorkItem(string id) {
        if (_disposables.IsDisposed) return;
        if (_openSessionKey is { } open && _directory.Rows.Lookup(open) is { HasValue: true } row
            && row.Value.SessionId is { } session && _workItemOf.Value.GetValueOrDefault(session) == id)
            _closeWorkspace?.Invoke();
        SetRemoved(_removed.Append(WorkItemKey(id)), persist: true);
    }

    public bool HasMarks => _marked.Value.Count > 0;

    /// Adds the row to the marked rows, or takes it out.
    public void ToggleMark(RailSessionViewModel row) {
        var marked = new HashSet<string>(_marked.Value, StringComparer.Ordinal);
        if (!marked.Remove(row.Key)) marked.Add(row.Key);
        _markAnchor = row.Key;
        SetMarks(marked);
    }

    /// Marks every shown row from the last one toggled, or the open one, through this one.
    public void ExtendMarks(RailSessionViewModel row) {
        var shown = ShownRows();
        var to = shown.IndexOf(row);
        var from = shown.FindIndex(r => r.Key == (_markAnchor ?? _openSessionKey));
        if (to < 0) return;
        if (from < 0) from = to;
        var marked = new HashSet<string>(_marked.Value, StringComparer.Ordinal);
        for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++) marked.Add(shown[i].Key);
        _markAnchor ??= row.Key;
        SetMarks(marked);
    }

    public void ClearMarks() {
        _markAnchor = null;
        if (HasMarks) SetMarks([]);
    }

    void SetMarks(IEnumerable<string> keys) {
        if (_disposables.IsDisposed) return;
        _marked.OnNext(keys.ToFrozenSet(StringComparer.Ordinal));
        this.RaisePropertyChanged(nameof(HasMarks));
    }

    /// The menu for a row: over the marked rows when it is one of them, else over it alone.
    public IReadOnlyList<RowMenuItem> MenuFor(RailSessionViewModel row) {
        if (!_marked.Value.Contains(row.Key)) ClearMarks();
        IReadOnlyList<AgentRow> targets = HasMarks
            ? [.. ShownRows().Where(r => _marked.Value.Contains(r.Key)).Select(r => r.Row)]
            : [row.Row];
        var items = new List<RowMenuItem>();
        foreach (var action in _actions) {
            if (action.SingleRow && targets.Count > 1) continue;
            IReadOnlyList<AgentRow> rows = [.. targets.Where(action.AppliesTo)];
            if (rows.Count == 0) continue;
            items.Add(new(action.Icon, action.Label(rows), () => {
                action.Run(rows);
                ClearMarks();
            }));
        }
        return items;
    }

    /// The menu for a work item's header.
    public IReadOnlyList<RowMenuItem> MenuFor(RailGroupViewModel group) {
        ClearMarks();
        if (group.WorkItemId is not { } id) return [];
        var items = new List<RowMenuItem>();
        if (_openWorkItem is { } open) items.Add(new(LucideIconKind.FolderOpen, "Open", () => open(id)));
        if (_renameWorkItem is { } rename) {
            var original = group.Label;
            // Relabels the header alone: regrouping the whole rail holds the frame that closes the field.
            items.Add(new(LucideIconKind.Pencil, "Rename", () => BeginRename(original, "work item", shown => {
                if (shown is null) _retitled.Remove(id);
                else _retitled[id] = shown;
                group.Relabel(shown ?? original);
            }, async title => new SessionRenameResult(await rename(id, title), null, null), editor => group.TitleEditor = editor)));
        }
        items.Add(new(LucideIconKind.EyeOff, "Remove from sidebar", () => RemoveWorkItem(id)));
        return items;
    }

    void BeginRename(AgentRow row) {
        if (_renameSession is not { } rename || Groups.SelectMany(g => g.Sessions).FirstOrDefault(r => r.Key == row.Key) is not { } shown) return;
        BeginRename(row.Title ?? "", "session", shown.ShowRenamed, title => rename(row, title, CancellationToken.None),
            editor => shown.TitleEditor = editor);
    }

    /// One rename at a time: starting another ends the one open. Saving shows the new title at once and puts the old one
    /// back only when the server refuses it.
    void BeginRename(string title, string subject, Action<string?> showTitle, Func<string, Task<SessionRenameResult>> send,
            Action<SessionTitleEditorViewModel?> show) {
        if (_disposables.IsDisposed) return;
        EndRename();
        _renaming?.Dispose();
        var editor = new SessionTitleEditorViewModel(Observable.Return(title), Observable.Return(true), (next, ct) => {
            showTitle(next);
            // Off the UI thread: the client's setup runs before its first await and would hold the frame that closes the field.
            _ = SettleRenameAsync(Task.Run(() => send(next)), subject, () => showTitle(null));
            return Task.FromResult(new SessionRenameResult(true, next, null));
        }, _notifier ?? new AppNotifier(), subject);
        _renaming = editor;
        _endRename = () => show(null);
        editor.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(SessionTitleEditorViewModel.IsEditing) && !editor.IsEditing && ReferenceEquals(editor, _renaming)) EndRename();
        };
        show(editor);
        editor.BeginEditCommand.Execute().Subscribe();
    }

    async Task SettleRenameAsync(Task<SessionRenameResult> sent, string subject, Action revert) {
        SessionRenameResult result;
        try {
            result = await sent;
        } catch (Exception) {
            result = new SessionRenameResult(false, null, null);
        }
        if (result.Applied || _disposables.IsDisposed) return;
        revert();
        _notifier?.Notify(SessionTitleEditorViewModel.FailureCopy(result.Reason, subject));
    }

    void EndRename() {
        var end = _endRename;
        _endRename = null;
        end?.Invoke();
    }

    /// The rows a drag starting on this one carries: the marked rows when it is one of them, else it alone.
    public IReadOnlyList<RailSessionViewModel> Picked(RailSessionViewModel row) =>
        _marked.Value.Contains(row.Key) ? [.. ShownRows().Where(r => _marked.Value.Contains(r.Key))] : [row];

    /// Dropping the rows under a work item files them there; a null work item takes them out of theirs.
    /// Null when the drop would change nothing.
    public Action? DropOn(IReadOnlyList<RailSessionViewModel> rows, string? workItemId) {
        var workItemOf = _workItemOf.Value;
        var sessions = rows.Select(r => r.Row.SessionId).OfType<string>();
        if (workItemId is { } target) {
            List<string> moving = [.. sessions.Where(id => workItemOf.GetValueOrDefault(id) != target)];
            return _fileSession is not { } file || moving.Count == 0 ? null : () => {
                Drop(moving, target, id => file(target, id));
                Groups.FirstOrDefault(group => group.WorkItemId == target)?.Expand();
            };
        }
        List<string> leaving = [.. sessions.Where(workItemOf.ContainsKey)];
        return _unfileSession is not { } unfile || leaving.Count == 0 ? null : () => Drop(leaving, null, id => unfile(workItemOf[id], id));
    }

    void Drop(IReadOnlyList<string> sessions, string? workItem, Func<string, Task<bool>> send) {
        foreach (var session in sessions) _dropped[session] = workItem;
        ClearMarks();
        ApplyWorkItems();
        foreach (var session in sessions) _ = SettleDropAsync(session, workItem, send(session));
    }

    async Task SettleDropAsync(string session, string? workItem, Task<bool> sent) {
        if (await sent || _disposables.IsDisposed) return;
        _notifier?.Notify(workItem is null ? "Couldn't remove the session from the work item" : "Couldn't move the session");
        if (!_dropped.TryGetValue(session, out var shown) || shown != workItem) return;
        _dropped.Remove(session);
        ApplyWorkItems();
    }

    // From every open work item read, not only the recent ones the rail shows.
    bool IsFiled(string sessionId) =>
        _dropped.TryGetValue(sessionId, out var dropped) ? dropped is not null : _read.Any(item => item.PartBySession.ContainsKey(sessionId));

    async Task ConvertToWorkItemAsync(AgentRow row) {
        var session = row.SessionId!;
        var created = await _createWorkItem!(session, row.Title!);
        if (_disposables.IsDisposed) return;
        if (created is null) {
            _notifier?.Notify("Couldn't convert the session to a work item");
            return;
        }
        _dropped[session] = created;
        _openWorkItems.Add(WorkItemKey(created));
    }

    List<RailSessionViewModel> ShownRows() =>
        [.. Groups.Where(group => group.IsExpanded).SelectMany(group => group.Sessions)];

    void SetRemoved(IEnumerable<string> ids, bool persist) {
        if (_disposables.IsDisposed) return;
        _removed = new HashSet<string>(ids, StringComparer.Ordinal);
        Refilter();
        ApplyWorkItems();
        this.RaisePropertyChanged(nameof(HasRemoved));
        this.RaisePropertyChanged(nameof(RestoreRemovedText));
        this.RaisePropertyChanged(nameof(FiltersDiffer));
        if (persist) Persist();
    }

    void Persist() {
        if (!_applying) _persist?.Invoke(Preferences);
    }

    internal static string WorkItemKey(string workItemId) => $"workitem:{workItemId}";

    static bool IsWorkItemKey(string key) => key.StartsWith("workitem:", StringComparison.Ordinal);

    // A session the rail has never shown, started moments ago and not part of a bulk load, fades in once.
    void NoteArrivals(IChangeSet<AgentRow, string> changes) {
        var now = _time.GetUtcNow();
        lock (_arrivals) {
            var settled = _seen.Count > 0;
            var fresh = changes.Where(change => change.Reason == ChangeReason.Add && !Seen(change.Current)).Select(change => change.Current).ToList();
            foreach (var change in changes) {
                _seen.Add(change.Current.Id);
                if (change.Current.SessionId is { } id) _seen.Add(id);
            }
            if (!settled || fresh.Count > MaxArrivals) return;
            foreach (var row in fresh)
                if (now.UtcDateTime - RailSessionViewModel.AsUtc(row.CreatedAt) < StartedWithin) _arrivals[row.Key] = now;
        }

        bool Seen(AgentRow row) => _seen.Contains(row.Id) || row.SessionId is { } id && _seen.Contains(id);
    }

    /// True once for a row that just arrived, for its view to fade it in.
    public bool TakeArrival(string key) {
        var now = _time.GetUtcNow();
        lock (_arrivals) return _arrivals.Remove(key, out var at) && now - at < ArrivalWindow;
    }

    /// Settles the work items shown, and the session each holds, after a read, the project, a removal or a drop moved.
    void ApplyWorkItems() {
        IReadOnlyList<RailWorkItem> read = !OffersWorkItem || _readProject != _shownProject?.Id ? [] : _read;
        // Ahead of the work item map, or a removed item's sessions flash into the sessions section.
        var hidden = read.Where(item => _removed.Contains(WorkItemKey(item.Id))).SelectMany(item => item.PartBySession.Keys)
            .ToFrozenSet(StringComparer.Ordinal);
        if (!hidden.SetEquals(_hiddenByWorkItem)) {
            _hiddenByWorkItem = hidden;
            Refilter();
        }
        var since = _time.GetUtcNow() - WorkItemActiveFor;
        IReadOnlyList<RailWorkItem> items = [.. read.Where(item => Recent(item, since) && !_removed.Contains(WorkItemKey(item.Id)))
            .Select(item => _retitled.TryGetValue(item.Id, out var title) ? item with { Title = title } : item)];
        var workItemOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items)
            foreach (var session in item.PartBySession.Keys) workItemOf.TryAdd(session, item.Id);
        foreach (var (session, workItem) in _dropped) {
            workItemOf.Remove(session);
            if (workItem is not null && items.Any(item => item.Id == workItem)) workItemOf[session] = workItem;
        }
        _workItemOf.OnNext(workItemOf.ToFrozenDictionary(StringComparer.Ordinal));
        _workItems.EditDiff(items, (a, b) => a == b);
        // A new comparer clears and re-adds every group, so it goes out only when the order moved.
        var order = items.Select(item => WorkItemKey(item.Id)).ToList();
        if (!order.SequenceEqual(_workItemOrder)) {
            _workItemOrder = order;
            _workItemComparer.OnNext(WorkItemComparer(order));
        }
        this.RaisePropertyChanged(nameof(OffersWorkItem));
        this.RaisePropertyChanged(nameof(ShowsWorkItems));
    }

    static readonly TimeSpan WorkItemActiveFor = TimeSpan.FromDays(3);

    // A server older than the sessions' last activity sends none, so its items all count as recent.
    static bool Recent(RailWorkItem item, DateTimeOffset since) =>
        item.PartBySession.Count > 0 && (item.LastActivityAt is not { } at || at > since);

    void Refilter() {
        _baseFilter.OnNext(BaseFilter());
        _stateFilter.OnNext(StateFilter());
    }

    Func<AgentRow, bool> BaseFilter() {
        var selected = _openSessionKey;
        var project = _shownProject;
        var removed = _removed.ToFrozenSet(StringComparer.Ordinal);
        var hidden = _hiddenByWorkItem;
        return row => row.Key == selected
            || (!(SessionIds.Canonical(row.SessionId) is { } id && removed.Contains(id)) && !(row.SessionId is { } session && hidden.Contains(session))
                && (project is null || project.Holds(row)));
    }

    Func<AgentRow, bool> StateFilter() {
        var all = BaseFilter();
        var state = _state;
        return row => all(row) && StateAdmits(state, row, null);
    }

    /// The listed copy of a remembered project, or all projects once a read no longer lists it.
    RailProject? Current(RailProject? project) =>
        project is null || _projects is null ? project : _projects.FirstOrDefault(p => p.Id == project.Id);

    static IReadOnlyList<RailProjectOption> Options(IReadOnlyList<RailProject>? projects, RailProject? current) =>
        projects is null ? [] : [.. projects.Select(p => new RailProjectOption(p, p.Id == current?.Id))];

    /// A row the workspace is on is always shown, whatever the filter says.
    internal static bool StateAdmits(RailState state, AgentRow row, string? selectedKey) =>
        row.Key == selectedKey || state switch {
            RailState.Live => !row.IsEndedSession,
            RailState.Ended => row.IsEndedSession,
            _ => true,
        };

    string GroupKeyOf(AgentRow row) => GroupKey(row, _grouping, _today, _time.LocalTimeZone, _work.GetValueOrDefault(row.Key));

    internal static string GroupKey(AgentRow row, RailGrouping grouping, DateOnly today, TimeZoneInfo zone, RunningWork work = default) => grouping switch {
        RailGrouping.Repository => row.RepoGroupKey,
        // Invariant: a Buddhist or Hijri culture would write its own year into a key the label parses
        // back as Gregorian and the preferences persist.
        RailGrouping.Date => $"day:{LocalDay(row.LastActivityAt ?? row.CreatedAt, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
        RailGrouping.Status => $"status:{StatusBucket(row, work)}",
        _ => "all",
    };

    /// Waiting on you, Running, Failed, Ended: the order the status groups keep. A row whose chip
    /// says Working is Running even when its parent turn is waiting, or the group contradicts the chip.
    static int StatusBucket(AgentRow row, RunningWork work) {
        if (row.IsEndedSession) return 3;
        if (row.Status == "Failed") return 2;
        if (SessionStatusDots.ForRow(row, pending: false, work: work).Kind == AgentStatusKind.Working) return 1;
        return SessionStatusDots.NeedsAttention(row) ? 0 : 1;
    }

    string LabelFor(string key, RailGrouping grouping, IGroup<AgentRow, string, string>? group = null) => grouping switch {
        // Rows in one repository group share RepoGroupLabel by construction — any member names it.
        RailGrouping.Repository => group?.Cache.Items[0].RepoGroupLabel ?? key,
        RailGrouping.Date => DayLabel(key),
        RailGrouping.Status => key switch {
            "status:0" => "Waiting on you",
            "status:1" => "Running",
            "status:2" => "Failed",
            _ => "Ended",
        },
        _ => "",
    };

    string DayLabel(string key) =>
        DateOnly.TryParseExact(key["day:".Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? DayLabel(day, _today) : key;

    internal static string DayLabel(DateOnly day, DateOnly today) {
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        return day.ToString("ddd d MMM", CultureInfo.InvariantCulture);
    }

    DateOnly LocalDay(DateTimeOffset instant) => LocalDay(instant.UtcDateTime, _time.LocalTimeZone);

    static DateOnly LocalDay(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));

    internal static IComparer<RailSessionViewModel> RowComparer(RailSort sort, bool descending) =>
        Comparer<RailSessionViewModel>.Create((a, b) => {
            var order = sort switch {
                RailSort.Created => a.CreatedAt.CompareTo(b.CreatedAt),
                RailSort.Title => StringComparer.OrdinalIgnoreCase.Compare(a.Primary, b.Primary),
                _ => (a.LastActivityAt ?? a.CreatedAt).CompareTo(b.LastActivityAt ?? b.CreatedAt),
            };
            if (descending) order = -order;
            if (order != 0) return order;
            var id = StringComparer.Ordinal.Compare(a.Id, b.Id);
            return id != 0 ? id : StringComparer.Ordinal.Compare(a.Key, b.Key);
        });

    /// Repositories by label with "No repository" last; days newest first; statuses in their fixed order.
    internal static IComparer<RailGroupViewModel> GroupComparer(RailGrouping grouping) =>
        Comparer<RailGroupViewModel>.Create((a, b) => grouping switch {
            RailGrouping.Repository => CompareRepositories(a, b),
            RailGrouping.Date => string.CompareOrdinal(b.Key, a.Key),
            _ => string.CompareOrdinal(a.Key, b.Key),
        });

    static IComparer<RailGroupViewModel> WorkItemComparer(IReadOnlyList<string> order) {
        var at = order.Select((key, index) => (key, index)).ToFrozenDictionary(entry => entry.key, entry => entry.index, StringComparer.Ordinal);
        return Comparer<RailGroupViewModel>.Create((a, b) => at.GetValueOrDefault(a.Key).CompareTo(at.GetValueOrDefault(b.Key)));
    }

    static int CompareRepositories(RailGroupViewModel a, RailGroupViewModel b) {
        var byNoRepo = a.IsNoRepository.CompareTo(b.IsNoRepository);
        if (byNoRepo != 0) return byNoRepo;
        var byLabel = StringComparer.OrdinalIgnoreCase.Compare(a.Label, b.Label);
        return byLabel != 0 ? byLabel : string.CompareOrdinal(a.Key, b.Key);
    }

    internal static string WorktreeKeyFor(AgentStatusDto dto) =>
        PlatformPaths.Normalize(CheckoutLabel.CheckoutPathFor(dto) ?? dto.RepoPath ?? "");

    internal static string WorktreeKeyFor(AgentRow row) => row.CheckoutKey;

    /// A quit keeps driving the setters and commands after this, so each one ignores a disposed rail.
    public void Dispose() {
        _renaming?.Dispose();
        _disposables.Dispose();
    }
}
