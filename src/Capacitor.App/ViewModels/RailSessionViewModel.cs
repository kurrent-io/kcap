using System.Collections.Frozen;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One session row of the rail, retaining commands and bindings across directory revisions.
public sealed class RailSessionViewModel : ReactiveObject, IDisposable {
    public const string TerminalTooltip = "Started in a terminal, not hosted by a daemon";
    public const string AttachedLabel = AttachedAgentCopy.TerminalSession;
    public const string AttachedTooltip = AttachedAgentCopy.TerminalSessionTip;

    public string Id { get; }
    public string Key { get; }
    public DateTime CreatedAt { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    /// The server's last activity for a listed session; null for a row only a daemon knows.
    DateTime? _lastActivityAt;
    public DateTime? LastActivityAt { get => _lastActivityAt; private set => this.RaiseAndSetIfChanged(ref _lastActivityAt, value); }
    public string? CheckoutPath { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    /// Null when the row has no title, in which case the chips line stands alone as the row.
    string? _primary;
    public string? Primary { get => _primary; private set => this.RaiseAndSetIfChanged(ref _primary, value); }
    public bool HasTitle { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string SessionAccessibleName => Primary ?? (HasVendor ? Vendor : "Session");
    public string Vendor { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public bool HasVendor { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string? Model { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string Meta { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string Tooltip => MachineBadge is { Length: > 0 } machine
        ? $"{Status.Tip}\n\n{machine}\nMachine · remote daemon" : Status.Tip;
    /// The daemon name badge for a remote row; null for a local one.
    string? _machineBadge;
    public string? MachineBadge { get => _machineBadge; private set => this.RaiseAndSetIfChanged(ref _machineBadge, value); }
    public string MachineLabel => $"on {MachineBadge}";
    public string MachineTooltip => $"This session runs on the remote daemon ‘{MachineBadge}’.";
    public bool IsRemote { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    /// A session the server lists with no daemon row behind it.
    bool _isServer;
    public bool IsServer { get => _isServer; private set => this.RaiseAndSetIfChanged(ref _isServer, value); }
    /// A live server row no daemon hosts: started in a terminal.
    bool _isTerminalSession;
    public bool IsTerminalSession { get => _isTerminalSession; private set => this.RaiseAndSetIfChanged(ref _isTerminalSession, value); }
    /// A terminal session a daemon steers: labelled as one, never as an agent a daemon hosts.
    bool _isAttached;
    public bool IsAttached { get => _isAttached; private set => this.RaiseAndSetIfChanged(ref _isAttached, value); }
    public bool IsEnded { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    /// Up to two initials of the owner's name, for a row that is not the signed-in user's.
    string? _ownerInitials;
    public string? OwnerInitials { get => _ownerInitials; private set => this.RaiseAndSetIfChanged(ref _ownerInitials, value); }
    public string? OwnerAvatarUrl { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool ShowsOwner { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string? OwnerName { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string OwnerTooltip { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string RepoLabel { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    readonly ObservableAsPropertyHelper<bool> _showsRepo;
    /// The repository name moves onto the row when the groups no longer say it.
    public bool ShowsRepo => _showsRepo.Value;
    /// A launch the daemon has not published yet: Meta carries its stage instead of an age.
    bool _isStarting;
    public bool IsStarting { get => _isStarting; private set => this.RaiseAndSetIfChanged(ref _isStarting, value); }
    public AgentRow Row => _row.Value;
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenPullRequestCommand { get; }
    public ReactiveCommand<RailPullRequestMenuItem, Unit> OpenPullRequestChoiceCommand { get; }
    RailPullRequestSummary? _pullRequest;
    public RailPullRequestSummary? PullRequest => _pullRequest;
    public string PullRequestLabel => PullRequest?.Label ?? "";
    public PullRequestStatus? PullRequestStatus => PullRequest?.Status;
    public bool HasPullRequestLifecycle => PullRequest?.HasLifecycle == true;
    public bool HasMultiplePullRequests => PullRequest?.Count > 1;
    public string PullRequestTooltip => $"{PullRequest?.Description}\n\nLinked to this session: {SessionAccessibleName}\n{(HasMultiplePullRequests ? "Choose a pull request" : "Open pull request details")}";
    public bool HasPullRequest => PullRequest is not null;
    public string PullRequestAccessibleName => $"{(HasMultiplePullRequests ? "Choose from" : "Open")} {PullRequestLabel}{(HasPullRequestLifecycle ? $", {PullRequest?.Lifecycle}" : "")} for {Primary ?? Vendor}";
    /// The right-click menu's items; each disables itself on a row it cannot act on.
    public bool HasMenu { get; private set; }
    public ReactiveCommand<Unit, Unit> RenameCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> MakePrivateCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> TerminateCommand { get; private set; } = null!;
    public string TerminateLabel => IsAttached ? AttachedAgentCopy.DisconnectSteering : "Terminate";
    readonly ObservableAsPropertyHelper<bool> _isSelected;
    public bool IsSelected => _isSelected.Value;

    readonly ObservableAsPropertyHelper<AgentStatusPresentation> _status;
    public AgentStatusPresentation Status => _status.Value;

    readonly ObservableAsPropertyHelper<bool> _isStale;
    /// A remote or server row greys out while the lane is stale, a server row also on the server's
    /// own idle flag; a local row is never stale.
    public bool IsStale => _isStale.Value;

    readonly CompositeDisposable _disposables = new();
    readonly BehaviorSubject<AgentRow> _row;
    readonly TimeProvider _time;
    RailSort _sort = RailSort.LastUpdated;
    RunningWork _work;

    public RailSessionViewModel(
            AgentRow row, IObservable<string?> selectedSessionKey,
            IObservable<IReadOnlySet<string>> agentsWithPending, IObservable<bool> remoteStale,
            Action<string> openLocal, Action<string> openRemote, TimeProvider time,
            IObservable<IReadOnlySet<string>>? agentsAwaitingAnswer = null,
            IObservable<IReadOnlyDictionary<string, RunningWork>>? agentsRunningWork = null,
            IObservable<IReadOnlyDictionary<string, RailPullRequestSummary>>? pullRequests = null,
            IObservable<bool>? localOnAppServer = null, Action<string, AgentOrigin, PullRequestSubjectDto?>? openPullRequest = null,
            IObservable<Unit>? ageTicks = null,
            Action<string>? openServer = null, IObservable<RailGrouping>? grouping = null,
            IRailSessionActions? sessionActions = null,
            IObservable<RailSort>? sort = null) {
        _time = time;
        _row = new BehaviorSubject<AgentRow>(row).DisposeWith(_disposables);
        Id = row.Id;
        Key = row.Key;
        UpdateFields(row);
        ageTicks?.Subscribe(_ => RefreshAge()).DisposeWith(_disposables);
        sort?.DistinctUntilChanged().Subscribe(next => {
            _sort = next;
            RefreshAge();
        }).DisposeWith(_disposables);
        (pullRequests ?? Observable.Return<IReadOnlyDictionary<string, RailPullRequestSummary>>(
                FrozenDictionary<string, RailPullRequestSummary>.Empty))
            .CombineLatest(localOnAppServer ?? Observable.Return(true), _row, (map, localReadable, current) =>
                current.SessionId is { Length: > 0 } sid && (current.Origin != AgentOrigin.Local || localReadable)
                    ? map.GetValueOrDefault(sid) : null)
            .DistinctUntilChanged()
            .Subscribe(summary => {
                this.RaiseAndSetIfChanged(ref _pullRequest, summary, nameof(PullRequest));
                this.RaisePropertyChanged(nameof(PullRequestLabel));
                this.RaisePropertyChanged(nameof(PullRequestStatus));
                this.RaisePropertyChanged(nameof(HasPullRequestLifecycle));
                this.RaisePropertyChanged(nameof(HasMultiplePullRequests));
                this.RaisePropertyChanged(nameof(PullRequestTooltip));
                this.RaisePropertyChanged(nameof(HasPullRequest));
                this.RaisePropertyChanged(nameof(PullRequestAccessibleName));
            }).DisposeWith(_disposables);
        // Pending launches open a local workspace; publication must keep that row selected.
        var selectionKey = row.Origin == AgentOrigin.Pending ? $"local:{row.Id}" : row.Key;
        _isSelected = selectedSessionKey.Select(sel => sel == selectionKey)
            .ToProperty(this, x => x.IsSelected, initialValue: false)
            .DisposeWith(_disposables);

        var answering = agentsAwaitingAnswer ?? Observable.Return<IReadOnlySet<string>>(FrozenSet<string>.Empty);
        var work = agentsRunningWork ?? Observable.Return<IReadOnlyDictionary<string, RunningWork>>(FrozenDictionary<string, RunningWork>.Empty);
        _row.CombineLatest(work, (current, running) => running.GetValueOrDefault(current.Key))
            .DistinctUntilChanged()
            .Subscribe(next => {
                _work = next;
                RefreshMeta();
            }).DisposeWith(_disposables);
        _status = _row.CombineLatest(agentsWithPending, answering, work,
                (current, pending, asked, running) => (Row: current, Pending: pending.Contains(current.Id), Asked: asked.Contains(current.Id), Work: running.GetValueOrDefault(current.Key)))
            .DistinctUntilChanged()
            .Select(state => SessionStatusDots.ForRow(state.Row, state.Pending, state.Asked, state.Work))
            .ToProperty(this, x => x.Status, initialValue: SessionStatusDots.ForRow(row, pending: false))
            .DisposeWith(_disposables);

        _isStale = _row.CombineLatest(remoteStale, (current, laneStale) =>
                current.Origin is AgentOrigin.Remote or AgentOrigin.Server && (laneStale || current.IsStale))
            .ToProperty(this, x => x.IsStale, initialValue: false)
            .DisposeWith(_disposables);

        _showsRepo = (grouping ?? Observable.Return(RailGrouping.Repository)).CombineLatest(_row, (mode, current) =>
                mode != RailGrouping.Repository && current.RepoGroupLabel is { Length: > 0 } label && label != RepoIdentity.NoRepository.Label)
            .ToProperty(this, x => x.ShowsRepo, initialValue: false)
            .DisposeWith(_disposables);

        OpenCommand = ReactiveCommand.Create(() => {
            if (IsServer) openServer?.Invoke(row.Id);
            else (IsRemote ? openRemote : openLocal)(row.Id);
        });
        _disposables.Add(OpenCommand);
        OpenPullRequestCommand = ReactiveCommand.Create(() => {
            if (HasPullRequest && !HasMultiplePullRequests)
                openPullRequest?.Invoke(Id, Origin(), PullRequest!.Subject);
        });
        _disposables.Add(OpenPullRequestCommand);
        OpenPullRequestChoiceCommand = ReactiveCommand.Create<RailPullRequestMenuItem>(item => {
            if (PullRequest?.MenuItems.Any(current => current.Subject == item.Subject) == true)
                openPullRequest?.Invoke(Id, Origin(), item.Subject);
        });
        _disposables.Add(OpenPullRequestChoiceCommand);
        BuildMenu(sessionActions);
    }

    void BuildMenu(IRailSessionActions? actions) {
        HasMenu = actions is not null;
        var owned = _row.Select(r => actions is not null && RailSessionActions.OwnsSession(r));
        var running = _row.Select(r => actions is not null && RailSessionActions.CanTerminate(r));
        var deletable = _row.Select(r => actions is not null && RailSessionActions.CanDelete(r));
        RenameCommand = ReactiveCommand.Create(() => actions?.Rename(_row.Value), owned).DisposeWith(_disposables);
        MakePrivateCommand = ReactiveCommand.CreateFromTask(
            () => actions?.MakePrivateAsync(_row.Value, SessionAccessibleName) ?? Task.CompletedTask, owned).DisposeWith(_disposables);
        DeleteCommand = ReactiveCommand.CreateFromTask(
            () => actions?.DeleteAsync(_row.Value, SessionAccessibleName) ?? Task.CompletedTask, deletable).DisposeWith(_disposables);
        TerminateCommand = ReactiveCommand.Create(() => actions?.Terminate(_row.Value, SessionAccessibleName), running).DisposeWith(_disposables);
    }

    AgentOrigin Origin() => IsServer ? AgentOrigin.Server : IsRemote ? AgentOrigin.Remote : AgentOrigin.Local;

    internal void Update(AgentRow row) {
        UpdateFields(row);
        _row.OnNext(row);
    }

    void UpdateFields(AgentRow row) {
        var oldPrimary = Primary;
        var oldVendor = Vendor;
        var oldMachine = MachineBadge;
        CreatedAt = row.CreatedAt;
        LastActivityAt = row.LastActivityAt;
        CheckoutPath = (row.WorkLocation == WorkLocationText.Borrowed ? row.BorrowedFrom : row.WorktreePath) ?? row.RepoPath;
        Primary = string.IsNullOrEmpty(row.Title) ? null : row.Title;
        HasTitle = Primary is not null;
        Vendor = row.Vendor;
        HasVendor = !string.IsNullOrEmpty(row.Vendor);
        Model = string.IsNullOrEmpty(row.Model) ? null : row.Model;
        IsStarting = row.Origin == AgentOrigin.Pending;
        IsServer = row.Origin == AgentOrigin.Server;
        IsTerminalSession = row.IsTerminalSession;
        IsAttached = row.IsAttached;
        IsEnded = row.IsEndedSession;
        OwnerInitials = Initials(row.OwnerName);
        var avatar = string.IsNullOrEmpty(row.OwnerAvatarUrl) ? null : row.OwnerAvatarUrl;
        ShowsOwner = (OwnerInitials is not null || avatar is not null) && row.OwnerIsViewer != true;
        // A hidden badge's avatar still fetches, so only a shown one gets the URL.
        OwnerAvatarUrl = ShowsOwner ? avatar : null;
        OwnerName = string.IsNullOrWhiteSpace(row.OwnerName) ? null : row.OwnerName;
        OwnerTooltip = row.OwnerName is { Length: > 0 } owner ? $"{owner}\nOwner" : "";
        RepoLabel = row.RepoGroupLabel;
        RefreshMeta(row);
        MachineBadge = row.MachineBadge;
        IsRemote = row.Origin == AgentOrigin.Remote;

        if (oldPrimary != Primary || oldVendor != Vendor) {
            this.RaisePropertyChanged(nameof(SessionAccessibleName));
            this.RaisePropertyChanged(nameof(PullRequestTooltip));
            this.RaisePropertyChanged(nameof(PullRequestAccessibleName));
        }
        if (oldMachine != MachineBadge) {
            this.RaisePropertyChanged(nameof(MachineLabel));
            this.RaisePropertyChanged(nameof(MachineTooltip));
            this.RaisePropertyChanged(nameof(Tooltip));
        }
    }

    void RefreshMeta() => RefreshMeta(_row.Value);

    void RefreshMeta(AgentRow row) => Meta = IsStarting ? LaunchStages.Label(row.LaunchStage) : MetaFor(row);

    /// Every row shows the time the rail sorts by, in one format: when the session started under
    /// Created, when it was last active otherwise. An ended row adds how long it ran; a live one its
    /// kind and borrow notes, and its subagents. A remote registry row's registration time is neither,
    /// so it shows no time.
    string MetaFor(AgentRow row) {
        var created = AsUtc(row.CreatedAt);
        if (row is { IsEndedSession: true, EndedAt: { } endedRaw }) {
            var ended = AsUtc(endedRaw);
            var shown = _sort == RailSort.Created ? created : AsUtc(row.LastActivityAt ?? endedRaw);
            return $"{Since(shown)} · {UptimeFormat.Format(ended - created)}";
        }
        var kind = row.Kind is "agent" or "session" or AgentActionService.AttachedKind ? null : row.Kind;
        var borrowed = row.WorkLocation == WorkLocationText.Borrowed ? "borrowed" : null;
        var age = row.Origin == AgentOrigin.Remote ? null
            : Since(_sort == RailSort.Created ? created : AsUtc(row.LastActivityAt ?? row.CreatedAt));
        var live = SessionStatusDots.LiveSubagents(row.LiveSubagents, _work);
        var subagents = live > 0 ? $"{live} subagent{(live == 1 ? "" : "s")}" : null;
        return Join(kind, borrowed, age, subagents);
    }

    /// Exact to the minute under a day and to the hour under a week, so neighbouring rows a few
    /// hours apart never print the same label. No seconds: the rail refreshes once a minute.
    string Since(DateTime utc) {
        var now = _time.GetUtcNow();
        var at = new DateTimeOffset(utc);
        var age = now - at;
        if (age < TimeSpan.FromMinutes(1)) return "<1m ago";
        return age < TimeSpan.FromDays(7) ? $"{UptimeFormat.Format(age)} ago" : RelativeTime.Ago(at, now, _time.LocalTimeZone);
    }

    /// A local stamp is converted rather than relabelled: relabelling shifts it by the UTC offset,
    /// enough to move an age across a day boundary.
    static DateTime AsUtc(DateTime at) =>
        at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : DateTime.SpecifyKind(at, DateTimeKind.Utc);

    void RefreshAge() {
        var row = _row.Value;
        if (row.Origin is not (AgentOrigin.Local or AgentOrigin.Server)) return;
        Meta = MetaFor(row);
    }

    internal static string? Initials(string? name) {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Concat(words.Take(2).Select(word => char.ToUpperInvariant(word[0])));
    }

    static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    public void Dispose() => _disposables.Dispose();
}
