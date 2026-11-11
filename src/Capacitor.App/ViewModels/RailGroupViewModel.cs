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

/// One rail group and its sessions: a repository, a day, a status, or the single headerless group
/// of an ungrouped rail. Streams arrive on the UI thread from the rail root.
public sealed class RailGroupViewModel : ReactiveObject, IDisposable {
    public string Key { get; }
    public RailGrouping Kind { get; }
    readonly string _rootDisplay;
    /// The repository key in a form worth showing a user, for the header tooltip; the label elsewhere.
    public string RootDisplay => Kind == RailGrouping.Repository ? _rootDisplay : Label;
    string _label;
    public string Label { get => _label; private set => this.RaiseAndSetIfChanged(ref _label, value); }
    public bool ShowsHeader => Kind != RailGrouping.None;
    public bool IsNoRepository { get; }
    /// Grouped by its folder rather than by a repository's remote, so the header must not read as one.
    public bool IsFolder => Kind == RailGrouping.Repository && !IsNoRepository && !Key.StartsWith("repo:", StringComparison.Ordinal);
    bool _isExpanded;
    public bool IsExpanded {
        get => _isExpanded;
        private set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }
    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }
    public string ToggleAccessibleName => $"{(IsExpanded ? "Collapse" : "Expand")} {(IsFolder ? "folder " : "")}{Label}{(HeaderStatus is { } status ? $", {status.AccessibleName}" : "")}";
    readonly ObservableAsPropertyHelper<AgentStatusPresentation?> _headerStatus;
    public AgentStatusPresentation? HeaderStatus => _headerStatus.Value;
    readonly ObservableAsPropertyHelper<bool> _showsHeaderBadge;
    public bool ShowsHeaderBadge => _showsHeaderBadge.Value;
    public string Tooltip {
        get {
            var root = IsFolder ? $"{RootDisplay}\nFolder, not a repository with a remote" : RootDisplay;
            if (_countIsPartial) root += "\nCounts cover the ended sessions loaded so far";
            return HeaderStatus is { } status ? $"{root}\n\n{status.Tip}" : root;
        }
    }

    readonly ObservableAsPropertyHelper<string> _countText;
    public string CountText => _countText.Value;
    bool _countIsPartial;

    readonly ObservableCollectionExtended<RailSessionViewModel> _sessionsSource = new();
    public ReadOnlyObservableCollection<RailSessionViewModel> Sessions { get; }

    readonly CompositeDisposable _disposables = new();
    readonly BehaviorSubject<bool> _expanded;
    readonly Action<bool> _setExpanded;

    public RailGroupViewModel(
            IGroup<AgentRow, string, string> group, RailGrouping kind, string label,
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
            IObservable<bool>? endedListPartial = null) {
        Key = group.Key;
        Kind = kind;
        _label = label;
        IsNoRepository = kind == RailGrouping.Repository && label == "No repository";
        _rootDisplay = kind == RailGrouping.Repository ? DisplayFor(Key, label) : label;
        _isExpanded = isExpanded;
        _setExpanded = setExpanded;
        _expanded = new BehaviorSubject<bool>(isExpanded).DisposeWith(_disposables);
        ToggleCommand = ReactiveCommand.Create(() => SetExpanded(!IsExpanded)).DisposeWith(_disposables);

        var query = group.Cache.Connect().QueryWhenChanged().Replay(1).RefCount();
        var pendingHere = query.CombineLatest(agentsWithPending, (rows, pending) =>
                rows.Items.Where(row => pending.Contains(row.Id)).Select(row => row.Id).ToHashSet(StringComparer.Ordinal))
            .DistinctUntilChanged(EqualityComparer<HashSet<string>>.Create((a, b) => a!.SetEquals(b!)));
        var answeringHere = query.CombineLatest(agentsAwaitingAnswer, (rows, answering) =>
                rows.Items.Where(row => answering.Contains(row.Id)).Select(row => row.Id).ToHashSet(StringComparer.Ordinal))
            .DistinctUntilChanged(EqualityComparer<HashSet<string>>.Create((a, b) => a!.SetEquals(b!)));
        var workHere = query.CombineLatest(agentsRunningWork, (rows, work) =>
                rows.Items.Where(row => work.ContainsKey(row.Key))
                    .ToDictionary(row => row.Key, row => work[row.Key], StringComparer.Ordinal))
            .DistinctUntilChanged(EqualityComparer<Dictionary<string, RunningWork>>.Create((a, b) => a!.Count == b!.Count && a.All(item => b.GetValueOrDefault(item.Key) == item.Value)));
        var header = query.CombineLatest(pendingHere, answeringHere, workHere, _expanded,
                (rows, pending, answering, work, visible) => visible ? null
                    : SessionStatusDots.Rollup(rows.Items, pending, answering, work))
            .Replay(1).RefCount();
        _headerStatus = header.ToProperty(this, x => x.HeaderStatus, initialValue: null).DisposeWith(_disposables);
        _showsHeaderBadge = header.Select(status => status is not null)
            .ToProperty(this, x => x.ShowsHeaderBadge, initialValue: false).DisposeWith(_disposables);
        header.Subscribe(_ => {
            this.RaisePropertyChanged(nameof(Tooltip));
            this.RaisePropertyChanged(nameof(ToggleAccessibleName));
        }).DisposeWith(_disposables);

        var counts = query
            .CombineLatest((endedListPartial ?? Observable.Return(false)).DistinctUntilChanged(),
                (rows, partial) => CountFor(rows.Items, partial))
            .Replay(1).RefCount();
        _countText = counts.Select(count => count.Text)
            .ToProperty(this, x => x.CountText, initialValue: "")
            .DisposeWith(_disposables);
        counts.Select(count => count.Partial).DistinctUntilChanged().Subscribe(partial => {
            _countIsPartial = partial;
            this.RaisePropertyChanged(nameof(Tooltip));
        }).DisposeWith(_disposables);

        Sessions = new ReadOnlyObservableCollection<RailSessionViewModel>(_sessionsSource);
        group.Cache.Connect()
            .TransformWithInlineUpdate(row => new RailSessionViewModel(row, selectedSessionKey, agentsWithPending, remoteStale,
                openLocal, openRemote, time, agentsAwaitingAnswer, agentsRunningWork,
                pullRequests, localOnAppServer, openPullRequest, ageTicks, openServer, grouping, sort), (vm, row) => vm.Update(row))
            .DisposeMany()
            .SortAndBind(_sessionsSource, rowComparer)
            .Subscribe()
            .DisposeWith(_disposables);
    }

    internal void Expand() => SetExpanded(true);

    /// The rail's remembered fold for this key, applied without persisting it again.
    internal void ApplyExpanded(bool expanded) {
        if (IsExpanded == expanded) return;
        IsExpanded = expanded;
        _expanded.OnNext(expanded);
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

    void SetExpanded(bool expanded) {
        if (IsExpanded == expanded) return;
        IsExpanded = expanded;
        _setExpanded(expanded);
        _expanded.OnNext(expanded);
        this.RaisePropertyChanged(nameof(ToggleAccessibleName));
    }

    /// Mixed groups count each kind; a group of one kind reads as it always did. A local row whose
    /// agent completed is ended, as its row and its workspace header already say. The ended list loads
    /// a page at a time, so while pages remain an ended count is only a floor and reads "N+".
    static (string Text, bool Partial) CountFor(IEnumerable<AgentRow> rows, bool endedListPartial) {
        int live = 0, ended = 0;
        foreach (var row in rows) {
            if (row.IsEndedSession || row is { Origin: AgentOrigin.Local, Status: "Completed" }) ended++;
            else live++;
        }
        var partial = endedListPartial && ended > 0;
        var more = partial ? "+" : "";
        if (ended > 0 && live > 0) return ($"{live} live · {ended}{more} ended", partial);
        var count = live + ended;
        return (count == 1 && !partial ? "1 session" : $"{count}{more} sessions", partial);
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
