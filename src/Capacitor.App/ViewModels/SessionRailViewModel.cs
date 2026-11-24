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
using Capacitor.App.Services;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.PullRequests;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The session rail's root: groups → sessions over directory.Rows, grouped and sorted the user's way.
/// Ctor-scoped and disposable like HomeViewModel — the tree must be live from construction.
/// ONE ObserveOn at the top of the pipeline: nested group caches are mutated by this outer
/// pipeline, so every inner Connect() below already fires on the UI thread.
public sealed class SessionRailViewModel : ReactiveObject, IDisposable {
    public const string CapNote = "Loading limit reached · search or narrow the scope for older sessions";
    public const string ScopeLockedTip = "Local version only stores your sessions. Upgrade to work with your team.";

    readonly CompositeDisposable _disposables = new();
    readonly IAgentDirectory _directory;
    readonly IServerSessionsService? _sessions;
    readonly Action<RailPreferences>? _persist;
    readonly TimeProvider _time;
    // Keep the user's folds when the directory empties/re-forms a group during refresh.
    HashSet<string> _collapsed;
    bool _applying;

    // SelectedSessionKey is fed to the nested VMs via this subject, not this.WhenAnyValue: that
    // call routes through ReactiveUI's ObservableForProperty/RxAppBuilder global init, which is
    // only reliably primed when some other test has already pumped the headless dispatcher
    // first — AvaloniaSession.cs documents the MainThreadScheduler initialization race.
    readonly BehaviorSubject<string?> _selectedSessionKeyChanges = new(null);
    readonly BehaviorSubject<Func<AgentRow, bool>> _stateFilter;
    readonly BehaviorSubject<RailState> _stateChanges;
    HashSet<string> _hiddenHarnesses;
    readonly Subject<Unit> _regroup = new();
    IReadOnlyDictionary<string, RunningWork> _work = FrozenDictionary<string, RunningWork>.Empty;
    readonly BehaviorSubject<IComparer<RailSessionViewModel>> _rowComparer;
    readonly BehaviorSubject<IComparer<RailGroupViewModel>> _groupComparer;
    readonly BehaviorSubject<RailGrouping> _groupingChanges;
    readonly BehaviorSubject<RailSort> _sortChanges;
    DateOnly _today;

    string? _selectedSessionKey;
    public string? SelectedSessionKey {
        get => _selectedSessionKey;
        set {
            if (_disposables.IsDisposed) return;
            this.RaiseAndSetIfChanged(ref _selectedSessionKey, value);
            _selectedSessionKeyChanges.OnNext(value);
            _stateFilter.OnNext(StateFilter());
            _sessions?.Keep(value is { } key && key.StartsWith("server:", StringComparison.Ordinal) ? key["server:".Length..] : null);
        }
    }

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
            _stateFilter.OnNext(StateFilter());
            _stateChanges.OnNext(value);
            this.RaisePropertyChanged(nameof(FiltersDiffer));
            Persist();
        }
    }

    public bool IsAll => _state == RailState.All;
    public bool IsLive => _state == RailState.Live;
    public bool IsEnded => _state == RailState.Ended;

    RailGrouping _grouping;
    public RailGrouping Grouping {
        get => _grouping;
        set {
            if (_disposables.IsDisposed) return;
            if (_grouping == value) return;
            this.RaiseAndSetIfChanged(ref _grouping, value);
            this.RaisePropertyChanged(nameof(GroupingLabel));
            _groupComparer.OnNext(GroupComparer(value));
            _groupingChanges.OnNext(value);
            _regroup.OnNext(Unit.Default);
            this.RaisePropertyChanged(nameof(FiltersDiffer));
            Persist();
        }
    }

    public string GroupingLabel => GroupingName(_grouping);

    public static string GroupingName(RailGrouping grouping) => grouping switch {
        RailGrouping.None => "No grouping",
        RailGrouping.Repository => "Repository",
        RailGrouping.Date => "Date",
        _ => "Status",
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

    /// Every harness the filter offers, in the registry's order.
    public IReadOnlyList<RailHarnessOption> Harnesses { get; }

    /// True with every harness shown, false with none, null in between.
    public bool? AllHarnessesChecked =>
        _hiddenHarnesses.Count == 0 ? true : _hiddenHarnesses.Count >= Harnesses.Count ? false : null;

    public bool IsHarnessNarrowed => _hiddenHarnesses.Count > 0;

    /// The trigger's word: All, the one harness shown, or how many are.
    public string HarnessLabel {
        get {
            var shown = Harnesses.Count - _hiddenHarnesses.Count;
            return shown == Harnesses.Count ? "All" : shown == 0 ? "None" : shown == 1 ? Harnesses.First(IsShown).Label
                : shown.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// The one harness shown, whose mark leads the trigger beside its name; null otherwise.
    public string? HarnessMark =>
        Harnesses.Count - _hiddenHarnesses.Count == 1 ? Harnesses.First(IsShown).Vendor : null;

    public bool HasHarnessMark => HarnessMark is not null;

    public string HarnessTip => !IsHarnessNarrowed ? "Filter by harness"
        : _hiddenHarnesses.Count == Harnesses.Count ? "Harness: none"
        : "Harness: " + string.Join(", ", Harnesses.Where(IsShown).Select(h => h.Label));

    public void SetAllHarnessesShown(bool shown) =>
        SetHiddenHarnesses(shown ? [] : Harnesses.Select(h => h.Vendor));

    /// The All row's click: from all shown it hides every harness, from any other state it shows them all.
    public void ToggleAllHarnesses() => SetAllHarnessesShown(AllHarnessesChecked != true);

    void ShowOnlyHarness(string vendor) => SetHiddenHarnesses(Harnesses.Select(h => h.Vendor).Where(v => v != vendor));

    bool IsShown(RailHarnessOption option) => !_hiddenHarnesses.Contains(option.Vendor);

    void SetHarnessShown(string vendor, bool shown) =>
        SetHiddenHarnesses(shown ? _hiddenHarnesses.Where(v => !string.Equals(v, vendor, StringComparison.OrdinalIgnoreCase))
            : _hiddenHarnesses.Append(vendor));

    void SetHiddenHarnesses(IEnumerable<string> hidden) {
        if (_disposables.IsDisposed) return;
        var next = KnownHarnesses(hidden);
        if (next.SetEquals(_hiddenHarnesses)) return;
        _hiddenHarnesses = next;
        foreach (var option in Harnesses) option.Sync(IsShown(option));
        this.RaisePropertyChanged(nameof(AllHarnessesChecked));
        this.RaisePropertyChanged(nameof(IsHarnessNarrowed));
        this.RaisePropertyChanged(nameof(HarnessLabel));
        this.RaisePropertyChanged(nameof(HarnessMark));
        this.RaisePropertyChanged(nameof(HasHarnessMark));
        this.RaisePropertyChanged(nameof(HarnessTip));
        _stateFilter.OnNext(StateFilter());
        this.RaisePropertyChanged(nameof(FiltersDiffer));
        Persist();
    }

    // A vendor this build does not offer is dropped: it could never be shown again from the list.
    HashSet<string> KnownHarnesses(IEnumerable<string>? vendors) {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var vendor in vendors ?? [])
            if (Harnesses.FirstOrDefault(h => string.Equals(h.Vendor, vendor, StringComparison.OrdinalIgnoreCase)) is { } option)
                known.Add(option.Vendor);
        return known;
    }

    /// True when any control differs from the defaults, however the panel is collapsed.
    public bool FiltersDiffer {
        get {
            var defaults = RailPreferences.Default;
            return _scope != defaults.Scope || _state != defaults.State || _grouping != defaults.Grouping
                || _sort != defaults.Sort || _descending != defaults.Descending || _hiddenHarnesses.Count > 0;
        }
    }

    /// Starts collapsed on every launch; never persisted.
    public bool FiltersOpen {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, Unit> ToggleFiltersCommand { get; }
    public ReactiveCommand<RailScope, Unit> SetScopeCommand { get; }
    public ReactiveCommand<RailState, Unit> SetStateCommand { get; }
    public ReactiveCommand<RailGrouping, Unit> SetGroupingCommand { get; }
    public ReactiveCommand<RailSort, Unit> SetSortCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleDirectionCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleAllHarnessesCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowAllHarnessesCommand { get; }

    public RailPreferences Preferences =>
        new(Scope, State, Grouping, Sort, Descending, [.. _collapsed.Order(StringComparer.Ordinal)],
            _hiddenHarnesses.Count == 0 ? null : [.. _hiddenHarnesses.Order(StringComparer.Ordinal)]);

    readonly ObservableAsPropertyHelper<bool> _isEmpty;
    public bool IsEmpty => _isEmpty.Value;

    readonly ObservableAsPropertyHelper<string> _loadedText;
    /// Ended-session loading progress; hidden for live-only or fully loaded listings.
    public string LoadedText => _loadedText.Value;
    readonly ObservableAsPropertyHelper<bool> _showsShowMore;
    public bool ShowsShowMore => _showsShowMore.Value;
    readonly ObservableAsPropertyHelper<bool> _atCap;
    public bool AtCap => _atCap.Value;
    readonly ObservableAsPropertyHelper<string?> _liveNotShownText;
    public string? LiveNotShownText => _liveNotShownText.Value;
    public ReactiveCommand<Unit, Unit> ShowMoreCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenPreviousSessionCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenNextSessionCommand { get; }

    readonly ObservableCollectionExtended<RailGroupViewModel> _groupsSource = new();
    public ReadOnlyObservableCollection<RailGroupViewModel> Groups { get; }

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
            IRailSessionActions? sessionActions = null,
            Func<string, Task<RailPreferences>>? bindUserPreferences = null, IObservable<bool>? statusPreview = null) {
        _directory = directory;
        _sessions = sessions;
        _persist = persistPreferences;
        _time = time;
        var initial = preferences ?? RailPreferences.Default;
        IsScopeLocked = scopeLocked;
        _scope = scopeLocked ? RailScope.Mine : initial.Scope;
        _state = initial.State;
        _grouping = initial.Grouping;
        _sort = initial.Sort;
        _descending = initial.Descending;
        _collapsed = new HashSet<string>(initial.Collapsed, StringComparer.Ordinal);
        Harnesses = [.. HarnessRegistry.Identities.Select(h => new RailHarnessOption(
            h.VendorId, h.Label, SetHarnessShown, ShowOnlyHarness))];
        foreach (var option in Harnesses) option.DisposeWith(_disposables);
        _hiddenHarnesses = KnownHarnesses(initial.HiddenHarnesses);
        foreach (var option in Harnesses) option.Sync(IsShown(option));
        _today = LocalDay(_time.GetUtcNow());
        if (sessions is not null) sessions.Scope = _scope;
        SetScopeCommand = ReactiveCommand.Create<RailScope>(scope => Scope = scope).DisposeWith(_disposables);
        SetStateCommand = ReactiveCommand.Create<RailState>(state => State = state).DisposeWith(_disposables);
        SetGroupingCommand = ReactiveCommand.Create<RailGrouping>(grouping => Grouping = grouping).DisposeWith(_disposables);
        SetSortCommand = ReactiveCommand.Create<RailSort>(sort => Sort = sort).DisposeWith(_disposables);
        ToggleDirectionCommand = ReactiveCommand.Create(() => { Descending = !Descending; }).DisposeWith(_disposables);
        ToggleAllHarnessesCommand = ReactiveCommand.Create(ToggleAllHarnesses).DisposeWith(_disposables);
        ShowAllHarnessesCommand = ReactiveCommand.Create(() => SetAllHarnessesShown(true)).DisposeWith(_disposables);
        ToggleFiltersCommand = ReactiveCommand.Create(() => { FiltersOpen = !FiltersOpen; }).DisposeWith(_disposables);
        OpenPreviousSessionCommand = ReactiveCommand.Create(() => { OpenAdjacent(-1); }).DisposeWith(_disposables);
        OpenNextSessionCommand = ReactiveCommand.Create(() => { OpenAdjacent(1); }).DisposeWith(_disposables);
        _stateFilter = new BehaviorSubject<Func<AgentRow, bool>>(StateFilter()).DisposeWith(_disposables);
        if (shownRows is not null) _stateFilter.Subscribe(shownRows.OnNext).DisposeWith(_disposables);
        _stateChanges = new BehaviorSubject<RailState>(_state).DisposeWith(_disposables);
        _rowComparer = new BehaviorSubject<IComparer<RailSessionViewModel>>(RowComparer(_sort, _descending)).DisposeWith(_disposables);
        _groupComparer = new BehaviorSubject<IComparer<RailGroupViewModel>>(GroupComparer(_grouping)).DisposeWith(_disposables);
        _groupingChanges = new BehaviorSubject<RailGrouping>(_grouping).DisposeWith(_disposables);
        _sortChanges = new BehaviorSubject<RailSort>(_sort).DisposeWith(_disposables);
        _disposables.Add(_regroup);

        var ageTicks = new Subject<Unit>().DisposeWith(_disposables);
        time.CreateTimer(_ => RxSchedulers.MainThreadScheduler.Schedule(() => { if (!ageTicks.IsDisposed) ageTicks.OnNext(Unit.Default); }),
            null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)).DisposeWith(_disposables);
        // The day groups are named against today; at midnight the names move, the keys do not.
        ageTicks.Subscribe(_ => {
            var today = LocalDay(_time.GetUtcNow());
            if (today == _today) return;
            _today = today;
            foreach (var group in _groupsSource.Where(g => g.Kind == RailGrouping.Date)) group.Relabel(LabelFor(group.Key, RailGrouping.Date));
        }).DisposeWith(_disposables);
        IObservable<string?> selected = _selectedSessionKeyChanges;
        // PermissionService.AgentsWithPending emits from background continuations; marshal once
        // here so every nested OAPH downstream sees
        // it on the UI thread without adding its own ObserveOn.
        var pending = (agentsWithPending ?? Observable.Return<IReadOnlySet<string>>(FrozenSet<string>.Empty))
            .ObserveOn(RxSchedulers.MainThreadScheduler).Replay(1).RefCount();
        var answering = (agentsAwaitingAnswer ?? Observable.Return<IReadOnlySet<string>>(FrozenSet<string>.Empty))
            .ObserveOn(RxSchedulers.MainThreadScheduler).Replay(1).RefCount();
        var attentionFilter = pending.CombineLatest(answering, (permissions, answers) =>
            (Func<AgentRow, bool>)(row => SessionStatusDots.NeedsAttention(row) || permissions.Contains(row.Id) || answers.Contains(row.Id)));
        _attention = directory.Rows.Connect()
            .Filter(attentionFilter)
            .QueryWhenChanged(rows => (rows.Count, Failed: rows.Items.Count(row => row.Status == "Failed")))
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
        _loadedText = endedPaging.Select(p => p.Visible && p.Page.Loaded < p.Page.Total
                ? $"{p.Page.Loaded} of {p.Page.Total} ended sessions loaded" : "")
            .ToProperty(this, x => x.LoadedText, initialValue: "").DisposeWith(_disposables);
        _showsShowMore = endedPaging.Select(p => p.Visible && p.Page.CanShowMore)
            .ToProperty(this, x => x.ShowsShowMore, initialValue: false).DisposeWith(_disposables);
        _atCap = endedPaging.Select(p => p.Visible && p.Page.AtCap && p.Page.Loaded < p.Page.Total)
            .ToProperty(this, x => x.AtCap, initialValue: false).DisposeWith(_disposables);
        _liveNotShownText = (sessions?.LiveNotShown ?? Observable.Return(0))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Select(n => n > 0 ? $"{n} more live session{(n == 1 ? "" : "s")} · narrow the scope" : null)
            .ToProperty(this, x => x.LiveNotShownText, initialValue: null).DisposeWith(_disposables);
        ShowMoreCommand = ReactiveCommand.CreateFromObservable(
            ShowMore, endedPaging.Select(p => p.Visible && p.Page.CanShowMore)).DisposeWith(_disposables);

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

        Groups = new ReadOnlyObservableCollection<RailGroupViewModel>(_groupsSource);
        Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                handler => _groupsSource.CollectionChanged += handler, handler => _groupsSource.CollectionChanged -= handler)
            .Subscribe(_ => QualifyRepositoryLabels())
            .DisposeWith(_disposables);
        directory.Rows.Connect()
            .Filter(_stateFilter)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Group(GroupKeyOf, _regroup)
            .Transform(g => new RailGroupViewModel(
                g, _grouping, LabelFor(g.Key, _grouping, g), selected, pending, stale, openLocalSession, openRemoteSession, time, answering, work,
                prs, localOnAppServer, openPullRequest, !_collapsed.Contains(g.Key), expanded => {
                    if (expanded) _collapsed.Remove(g.Key);
                    else _collapsed.Add(g.Key);
                    Persist();
                }, ageTicks, openServerSession, _groupingChanges, _rowComparer, _sortChanges, sessionActions, endedListPartial, statusPreview))
            .DisposeMany()
            .SortAndBind(_groupsSource, _groupComparer)
            .Subscribe()
            .DisposeWith(_disposables);
        directory.Rows.Connect()
            .Filter(_stateChanges.Select(state => (Func<AgentRow, bool>)(row => StateMatches(state, row))))
            .QueryWhenChanged(rows => rows.Items.CountBy(row => row.Vendor, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(StringComparer.OrdinalIgnoreCase))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(counts => {
                foreach (var option in Harnesses) option.Count = counts.GetValueOrDefault(option.Vendor);
            })
            .DisposeWith(_disposables);
        work.Subscribe(next => {
            _work = next;
            if (_grouping == RailGrouping.Status) _regroup.OnNext(Unit.Default);
        }).DisposeWith(_disposables);
        // The groups, not the directory: a state filter that leaves nothing is an empty rail too.
        _isEmpty = Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                handler => _groupsSource.CollectionChanged += handler, handler => _groupsSource.CollectionChanged -= handler)
            .Select(_ => _groupsSource.Count == 0)
            .DistinctUntilChanged()
            .ToProperty(this, x => x.IsEmpty, initialValue: _groupsSource.Count == 0)
            .DisposeWith(_disposables);
    }

    IObservable<Unit> ShowMore() {
        var loaded = _directory.Rows.Keys.ToHashSet(StringComparer.Ordinal);
        return Observable.FromAsync(() => _sessions?.ShowMoreAsync() ?? Task.CompletedTask)
            // The directory queues its rows before the read completes; reveal them on the UI thread
            // after those changes reach the groups. Background refreshes keep the user's folds.
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Do(_ => {
                if (_disposables.IsDisposed) return;
                foreach (var group in Groups.Where(g => g.Sessions.Any(s => s is { IsServer: true, IsEnded: true } && !loaded.Contains(s.Key))))
                    group.Expand();
            });
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
            SetHiddenHarnesses(preferences.HiddenHarnesses ?? []);
            _collapsed = new HashSet<string>(preferences.Collapsed, StringComparer.Ordinal);
            foreach (var group in Groups) group.ApplyExpanded(!_collapsed.Contains(group.Key));
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
        var groupKey = GroupKeyOf(row.Value);
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

    void Persist() {
        if (!_applying) _persist?.Invoke(Preferences);
    }

    Func<AgentRow, bool> StateFilter() {
        var state = _state;
        var selected = _selectedSessionKey;
        IReadOnlySet<string>? shown = _hiddenHarnesses.Count == 0 ? null
            : Harnesses.Where(IsShown).Select(h => h.Vendor).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        return row => Admits(state, shown, row, selected);
    }

    /// A row the workspace is on is always shown, whatever the filters say. A null harness set shows
    /// every vendor, one this build has never heard of included; a narrowed one shows only its own.
    internal static bool Admits(RailState state, IReadOnlySet<string>? shownHarnesses, AgentRow row, string? selectedKey) =>
        row.Key == selectedKey || (StateMatches(state, row) && (shownHarnesses is null || shownHarnesses.Contains(row.Vendor)));

    static bool StateMatches(RailState state, AgentRow row) => state switch {
        RailState.Live => !row.IsEndedSession,
        RailState.Ended => row.IsEndedSession,
        _ => true,
    };

    string GroupKeyOf(AgentRow row) => GroupKey(row, _grouping, _time.LocalTimeZone, _work.GetValueOrDefault(row.Key));

    internal static string GroupKey(AgentRow row, RailGrouping grouping, TimeZoneInfo zone, RunningWork work = default) => grouping switch {
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

    void QualifyRepositoryLabels() {
        var repositories = _groupsSource.Where(g => g.Kind == RailGrouping.Repository).ToList();
        var shown = RailGroupLabels.Disambiguate([.. repositories.Select(g => (g.Key, g.BaseLabel))]);
        foreach (var group in repositories) group.Relabel(shown[group.Key], qualified: true);
    }

    string DayLabel(string key) {
        if (!DateOnly.TryParseExact(key["day:".Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return key;
        if (day == _today) return "Today";
        if (day == _today.AddDays(-1)) return "Yesterday";
        return day.ToString("ddd d MMM", CultureInfo.InvariantCulture);
    }

    DateOnly LocalDay(DateTimeOffset instant) => LocalDay(instant.UtcDateTime, _time.LocalTimeZone);

    static DateOnly LocalDay(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));

    internal static IComparer<RailSessionViewModel> RowComparer(RailSort sort, bool descending) =>
        Comparer<RailSessionViewModel>.Create((a, b) => {
            var order = sort switch {
                RailSort.Created => a.CreatedAt.CompareTo(b.CreatedAt),
                RailSort.Title => StringComparer.OrdinalIgnoreCase.Compare(a.Primary ?? a.Vendor, b.Primary ?? b.Vendor),
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

    static int CompareRepositories(RailGroupViewModel a, RailGroupViewModel b) {
        var byNoRepo = a.IsNoRepository.CompareTo(b.IsNoRepository);
        if (byNoRepo != 0) return byNoRepo;
        // The label before qualification: a qualified label changes as groups come and go, and the
        // groups it tells apart stay side by side, in key order, which is their folder path's order.
        var byLabel = StringComparer.OrdinalIgnoreCase.Compare(a.BaseLabel, b.BaseLabel);
        return byLabel != 0 ? byLabel : string.CompareOrdinal(a.Key, b.Key);
    }

    /// A quit keeps driving the setters and commands after this, so each one ignores a disposed rail.
    public void Dispose() => _disposables.Dispose();
}
