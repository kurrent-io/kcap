using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core.PullRequests;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One rail group and its sessions: a repository, a day, a status, an open work item of the picked
/// project, or the single headerless group of an ungrouped rail. Streams arrive on the UI thread from
/// the rail root.
public sealed class RailGroupViewModel : ReactiveObject, IDisposable {
    public string Key { get; }
    public RailGrouping Kind { get; }
    readonly string _rootDisplay;
    /// The repository key in a form worth showing a user, for the header tooltip; the label elsewhere.
    public string RootDisplay => Kind == RailGrouping.Repository ? _rootDisplay : Label;
    string _label;
    public string Label { get => _label; private set => this.RaiseAndSetIfChanged(ref _label, value); }
    public bool ShowsHeader => Kind is not (RailGrouping.None or RailGrouping.WorkItem);
    public bool IsWorkItem => Kind == RailGrouping.WorkItem;
    public string? WorkItemId { get; }
    readonly ObservableAsPropertyHelper<bool> _isSelected;
    public bool IsSelected => _isSelected.Value;
    int _partsDone;
    public int PartsDone { get => _partsDone; private set => this.RaiseAndSetIfChanged(ref _partsDone, value); }
    int _partsTotal;
    public int PartsTotal { get => _partsTotal; private set => this.RaiseAndSetIfChanged(ref _partsTotal, value); }
    bool _isDone;
    public bool IsDone { get => _isDone; private set => this.RaiseAndSetIfChanged(ref _isDone, value); }
    public bool IsNoRepository { get; }
    /// Grouped by its folder rather than by a repository's remote, so the header must not read as one.
    public bool IsFolder => Kind == RailGrouping.Repository && !IsNoRepository && !Key.StartsWith("repo:", StringComparison.Ordinal);
    bool _isExpanded;
    public bool IsExpanded {
        get => _isExpanded;
        private set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }
    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }
    public string ToggleAccessibleName => $"{(IsExpanded ? "Collapse" : "Expand")} {(IsFolder ? "folder " : "")}{Label}";
    public string Tooltip {
        get {
            if (IsWorkItem) return IsDone ? $"{Label}\nDone" : $"{Label}\n{PartsDone} of {PartsTotal} parts done";
            return IsFolder ? $"{RootDisplay}\nFolder, not a repository with a remote" : RootDisplay;
        }
    }

    const int WorkItemKeeps = 5;

    readonly ObservableAsPropertyHelper<string?> _moreText;
    /// Counts a work item's sessions past its most recent few that are neither live nor open; its page lists them all.
    public string? MoreText => _moreText.Value;

    readonly ObservableAsPropertyHelper<int> _sessionCount;
    public int SessionCount => _sessionCount.Value;

    readonly ObservableCollectionExtended<RailSessionViewModel> _sessionsSource = new();
    public ReadOnlyObservableCollection<RailSessionViewModel> Sessions { get; }
    public IObservable<bool> HasSessions { get; }

    readonly CompositeDisposable _disposables = new();
    readonly BehaviorSubject<RailWorkItem?> _workItem;
    readonly Action<bool> _setExpanded;

    public RailGroupViewModel(
            string key, IObservable<IChangeSet<AgentRow, string>> rows, RailGrouping kind, string label,
            IObservable<string?> selectedSessionKey, IObservable<IReadOnlySet<string>> agentsWithPending,
            IObservable<bool> remoteStale,
            Action<string> openLocal, Action<string> openRemote, TimeProvider time,
            IObservable<IReadOnlySet<string>> agentsAwaitingAnswer,
            IObservable<IReadOnlyDictionary<string, RunningWork>> agentsRunningWork,
            IObservable<IReadOnlyDictionary<string, RailPullRequestSummary>> pullRequests,
            IObservable<bool> localOnAppServer, Action<string, AgentOrigin, PullRequestSubjectDto?>? openPullRequest,
            bool isExpanded, Action<bool> setExpanded, IObservable<Unit>? ageTicks,
            Action<string>? openServer, IObservable<RailGrouping> grouping,
            IObservable<IComparer<RailSessionViewModel>> rowComparer, IObservable<RailSort> sort,
            RailWorkItem? workItem = null,
            IObservable<IReadOnlySet<string>>? suggested = null,
            IObservable<IReadOnlySet<string>>? marked = null,
            IObservable<string?>? selectedWorkItem = null, IObservable<string?>? openSessionKey = null) {
        Key = key;
        Kind = kind;
        WorkItemId = workItem?.Id;
        _isSelected = (selectedWorkItem ?? Observable.Return<string?>(null))
            .Select(id => id is not null && id == WorkItemId)
            .ToProperty(this, x => x.IsSelected, initialValue: false)
            .DisposeWith(_disposables);
        _label = label;
        IsNoRepository = kind == RailGrouping.Repository && label == "No repository";
        _rootDisplay = kind == RailGrouping.Repository ? DisplayFor(Key, label) : label;
        _isExpanded = isExpanded;
        _setExpanded = setExpanded;
        ToggleCommand = ReactiveCommand.Create(() => SetExpanded(!IsExpanded)).DisposeWith(_disposables);
        _workItem = new BehaviorSubject<RailWorkItem?>(workItem);
        if (workItem is not null) UpdateWorkItem(workItem);

        var group = rows.AsObservableCache().DisposeWith(_disposables);
        HasSessions = group.CountChanged.Select(count => count > 0).DistinctUntilChanged();
        _sessionCount = group.CountChanged.ToProperty(this, x => x.SessionCount, initialValue: 0).DisposeWith(_disposables);
        var query = group.Connect().QueryWhenChanged().Replay(1).RefCount();
        var kept = query.CombineLatest(openSessionKey ?? selectedSessionKey, (rows, open) => (Keys: KeptOfWorkItem(rows.Items, open), rows.Count))
            .Replay(1).RefCount();
        _moreText = (workItem is null ? Observable.Return(0) : kept.Select(k => k.Count - k.Keys.Count))
            .Select(hidden => hidden > 0 ? $"{hidden} more" : null).DistinctUntilChanged()
            .ToProperty(this, x => x.MoreText, initialValue: null)
            .DisposeWith(_disposables);
        var shown = workItem is null ? group.Connect()
            : group.Connect().Filter(kept.Select(k => (Func<AgentRow, bool>)(row => k.Keys.Contains(row.Key))));

        Sessions = new ReadOnlyObservableCollection<RailSessionViewModel>(_sessionsSource);
        shown
            .TransformWithInlineUpdate(row => new RailSessionViewModel(row, selectedSessionKey, agentsWithPending, remoteStale,
                openLocal, openRemote, time, agentsAwaitingAnswer, agentsRunningWork,
                pullRequests, localOnAppServer, openPullRequest, ageTicks, openServer, grouping, sort,
                workItem is null ? null : _workItem.Select(item => item!.PartBySession.GetValueOrDefault(row.SessionId ?? "", RailWorkItem.NoPart)).DistinctUntilChanged(),
                workItem is null ? suggested : null, marked),
                (vm, row) => vm.Update(row))
            .DisposeMany()
            .SortAndBind(_sessionsSource, rowComparer)
            .Subscribe()
            .DisposeWith(_disposables);
        _workItem.DisposeWith(_disposables);
    }

    /// A later read of the same work item: its title, progress and the parts its sessions sit in.
    internal void UpdateWorkItem(RailWorkItem item) {
        Relabel(item.Title);
        PartsDone = item.PartsDone;
        PartsTotal = item.PartsTotal;
        IsDone = item.Done;
        this.RaisePropertyChanged(nameof(Tooltip));
        _workItem.OnNext(item);
    }

    internal void Expand() => SetExpanded(true);

    /// The rail's remembered fold for this key, applied without persisting it again.
    internal void ApplyExpanded(bool expanded) {
        if (IsExpanded == expanded) return;
        IsExpanded = expanded;
        this.RaisePropertyChanged(nameof(ToggleAccessibleName));
    }

    /// A day group's name moves with the calendar; the key does not.
    internal void Relabel(string label) {
        if (Label == label) return;
        Label = label;
        this.RaisePropertyChanged(nameof(RootDisplay));
        this.RaisePropertyChanged(nameof(Tooltip));
        this.RaisePropertyChanged(nameof(ToggleAccessibleName));
    }

    internal void SetExpanded(bool expanded) {
        if (IsExpanded == expanded) return;
        IsExpanded = expanded;
        _setExpanded(expanded);
        this.RaisePropertyChanged(nameof(ToggleAccessibleName));
    }

    static bool IsEnded(AgentRow row) => row.IsEndedSession || row is { Origin: AgentOrigin.Local, Status: "Completed" };

    static HashSet<string> KeptOfWorkItem(IEnumerable<AgentRow> rows, string? openKey) {
        var all = rows.ToList();
        var kept = all.OrderByDescending(row => row.LastActivityAt ?? row.CreatedAt).Take(WorkItemKeeps).Select(row => row.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var row in all)
            if (!IsEnded(row) || RailSessionViewModel.SelectionKey(row) == openKey) kept.Add(row.Key);
        return kept;
    }

    // Key's own prefixes ("repo:", "path:", "daemon:{owner}/{daemon}:{path}") are a group identity,
    // not display text (RepoIdentity's own rule) — this is the one place that reads them anyway,
    // to turn the key into something worth a tooltip.
    static string DisplayFor(string rootPath, string label) {
        if (rootPath.StartsWith("repo:", StringComparison.Ordinal))
            return rootPath["repo:".Length..];
        if (rootPath.StartsWith("path:", StringComparison.Ordinal))
            return rootPath["path:".Length..];
        if (rootPath.StartsWith("daemon:", StringComparison.Ordinal)) {
            var rest = rootPath["daemon:".Length..]; // "{owner}/{daemon}:{path}"
            var pathAt = rest.IndexOf(':');
            var ownerDaemon = pathAt < 0 ? rest : rest[..pathAt];
            var path = pathAt < 0 ? "" : rest[(pathAt + 1)..];
            var daemon = ownerDaemon[(ownerDaemon.LastIndexOf('/') + 1)..];
            return path.Length == 0 ? label : $"{path} on {daemon}";
        }
        return rootPath;
    }

    public void Dispose() => _disposables.Dispose();
}
