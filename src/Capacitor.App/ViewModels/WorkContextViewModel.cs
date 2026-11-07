using System.ComponentModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Collections;
using Capacitor.App.Services;
using Capacitor.App.Views;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests.Readers;
using Capacitor.Cli.Core.WorkItems;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public enum WorkContextPhase { WaitingForSession, Loading, Ready, NoWorkItem, SignedOut, NotInPlan, Unreachable, SessionUnknown }

/// The work-context sidebar for one session: facts from the daemon's dto, the work item from the
/// server. Ctor-scoped; TeardownAsync is the one exit.
///
/// The session id is the read's identity. A lease owns one id, its cancellation and its pending
/// read; a result applies only for the current lease, every lease is kept until its read settles
/// so teardown can await them all, and every lease transition happens on the UI thread.
public sealed partial class WorkContextViewModel : ReactiveObject {
    PullRequestContextViewModel? _pullRequests;
    public PullRequestContextViewModel? PullRequests {
        get => _pullRequests;
        internal set {
            _pullRequests = value;
            if (value is not null) {
                value.PropertyChanged += OnPullRequestChanged;
                _disposables.Add(Disposable.Create(() => value.PropertyChanged -= OnPullRequestChanged));
            }
            this.RaisePropertyChanged(nameof(PullRequests));
            this.RaisePropertyChanged(nameof(IsStale));
            RebuildLinks();
            RaisePlanProgress();
        }
    }
    public bool ShowsLegacyLinks => PullRequests is null;
    PullRequestRepository? _primaryRepository;
    public PullRequestRepository? PrimaryRepository {
        get => _primaryRepository;
        private set {
            var changed = value is not null && value != _primaryRepository;
            _primaryRepository = value;
            if (changed) PullRequests?.PrimaryRepositoryChanged();
        }
    }
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    internal const double CompactThreshold = 80;
    internal static readonly TimeSpan CompactPollInterval = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan CompactPollLimit = TimeSpan.FromMinutes(2);
    static readonly IReadOnlyList<HarnessOption> DefaultHarnessOptions = HostedHarnessCatalog.Build(null);

    internal const string WaitingNote      = "Waiting for session…";
    internal const string LoadingNote      = "Loading…";
    internal const string NoWorkItemNote   = "No work item yet. Ask the agent to link this session to an issue or task.";
    internal const string NoRepositoryNote = "No repository on this session, so a work item cannot attach.";
    internal const string SignedOutNote    = "Sign in to see the work context.";
    internal const string NotInPlanNote    = "Work items are not in this workspace's plan.";
    internal const string UnreachableNote  = "Couldn't reach the server.";

    readonly bool _isRemote;
    readonly IWorkContextSource _source;
    readonly SingleFlightReader<string, WorkContextRead> _reader;
    readonly SingleFlightReader<(string? Path, string? Reported), string> _branchReader;
    readonly IUrlOpener _opener;
    readonly TimeProvider _time;
    readonly Action<string>? _openWorkItem;
    readonly SessionRuns _runs;
    readonly Func<bool> _canSendToAgent;
    readonly Func<string, Task<bool>> _sendToAgent;
    readonly BehaviorSubject<bool> _canOpenWorkItem = new(false);
    readonly CompositeDisposable _disposables = new();
    ITimer? _timer;
    bool _tornDown;
    bool _serverDown;
    bool _compactPending;
    long _compactStartedAt;
    AgentStatusDto? _dto;

    string _repository = "—";
    public string Repository { get => _repository; private set => this.RaiseAndSetIfChanged(ref _repository, value); }
    string? _repositoryPath;
    public string? RepositoryPath {
        get => _repositoryPath;
        private set {
            this.RaiseAndSetIfChanged(ref _repositoryPath, value);
            this.RaisePropertyChanged(nameof(CanCopyRepository));
        }
    }
    public bool CanCopyRepository => !string.IsNullOrEmpty(_repositoryPath);
    string _worktree = "—";
    public string Worktree { get => _worktree; private set => this.RaiseAndSetIfChanged(ref _worktree, value); }
    string? _worktreePath;
    public string? WorktreePath {
        get => _worktreePath;
        private set {
            this.RaiseAndSetIfChanged(ref _worktreePath, value);
            this.RaisePropertyChanged(nameof(CanCopyWorktree));
        }
    }
    public bool CanCopyWorktree => !string.IsNullOrEmpty(_worktreePath);
    string _branch = "—";
    public string Branch {
        get => _branch;
        private set {
            this.RaiseAndSetIfChanged(ref _branch, value);
            this.RaisePropertyChanged(nameof(CanCopyBranch));
        }
    }
    public bool CanCopyBranch => _branch != "—";
    string _harness = "—";
    public string Harness { get => _harness; private set => this.RaiseAndSetIfChanged(ref _harness, value); }
    string _tokens = SessionFactText.Unknown;
    public string Tokens { get => _tokens; private set => this.RaiseAndSetIfChanged(ref _tokens, value); }
    string _cost = SessionFactText.Unknown;
    public string Cost { get => _cost; private set => this.RaiseAndSetIfChanged(ref _cost, value); }
    string _contextUse = SessionFactText.Unknown;
    public string ContextUse { get => _contextUse; private set => this.RaiseAndSetIfChanged(ref _contextUse, value); }
    string? _currentContextUse;
    /// The context in use now, or "compacted" while a compaction awaits its next reading; null
    /// when the server reports neither.
    public string? CurrentContextUse {
        get => _currentContextUse;
        private set {
            if (_currentContextUse == value) return;
            this.RaiseAndSetIfChanged(ref _currentContextUse, value);
            this.RaisePropertyChanged(nameof(ShowsCurrentContext));
        }
    }
    public bool ShowsCurrentContext => _currentContextUse is not null;
    public bool CurrentContextWarns => IsLive && ContextPercent >= CompactThreshold;
    public bool ShowsCompact =>
        CurrentContextWarns && _stats?.AwaitingContextReading != true && !_compactPending
        && _dto is { Vendor: var vendor } && (vendor.Equals("claude", StringComparison.OrdinalIgnoreCase) || vendor.Equals("codex", StringComparison.OrdinalIgnoreCase))
        && _canSendToAgent();
    public ReactiveCommand<Unit, Unit> CompactCommand { get; }

    SessionStatsDto? _stats;
    bool IsLive => _dto is not null && !SessionStatusDots.IsTerminal(_dto.Status);
    double? ContextPercent => SessionFactText.CurrentContextPercent(_stats);

    string _sessionSharing = SessionFactText.Unknown;
    public string SessionSharing { get => _sessionSharing; private set => this.RaiseAndSetIfChanged(ref _sessionSharing, value); }
    string _agentSharing = SessionFactText.Unknown;
    public string AgentSharing { get => _agentSharing; private set => this.RaiseAndSetIfChanged(ref _agentSharing, value); }
    string _sessionIdText = "resolving…";
    public string SessionIdText {
        get => _sessionIdText;
        private set {
            this.RaiseAndSetIfChanged(ref _sessionIdText, value);
            this.RaisePropertyChanged(nameof(SessionIdDisplay));
            this.RaisePropertyChanged(nameof(CanCopySessionId));
        }
    }
    /// Head and tail of a long id, so a 32-hex session id stays one line in the 320px pane.
    public string SessionIdDisplay => MiddleTruncate(_sessionIdText);
    public bool CanCopySessionId => _sessionIdText.Length > 0 && _sessionIdText != "resolving…";

    /// The session's subagents, shared with the chat tab; a session-local fact like the ones
    /// under SESSION, so it renders in every pane phase. Background commands are listed above the
    /// composer instead.
    public IAvaloniaReadOnlyList<RunRow> Subagents => _runs.Agents;
    public bool HasSubagents => _runs.Agents.Count > 0;
    /// What the collapsed section lists.
    public IAvaloniaReadOnlyList<RunRow> RunningSubagents => _runs.RunningAgents;
    public bool HasRunningSubagents => _runs.RunningAgents.Count > 0;
    public string SubagentsHeader {
        get {
            var running = _runs.RunningAgents.Count;
            var total = _runs.Agents.Count;
            return running switch {
                0                     => $"{total}",
                var r when r == total => $"{r} running",
                var r                 => $"{r} of {total} running",
            };
        }
    }

    static readonly RunState[] RunCountOrder =
        [RunState.Running, RunState.Done, RunState.Failed, RunState.Stopped];

    /// What the collapsed header shows: one entry per state something is in, so the numbers
    /// add up to the list.
    public IReadOnlyList<RunCount> SubagentCounts =>
        [.. RunCountOrder.Select(state => new RunCount(state, _runs.Agents.Count(row => row.State == state))).Where(c => c.Count > 0)];

    /// A subagent row the user chose to read; the workspace opens its transcript in place of the chat.
    public event Action<RunRow>? RunOpened;

    public ReactiveCommand<RunRow, Unit> OpenRunCommand { get; }

    /// The plan the session works from. Server-derived, and read on its own lease so a slow plan
    /// read never holds the work item back.
    public PlanSectionViewModel Plan { get; }

    void RefreshRuns() {
        this.RaisePropertyChanged(nameof(HasSubagents));
        this.RaisePropertyChanged(nameof(HasRunningSubagents));
        this.RaisePropertyChanged(nameof(SubagentsHeader));
        this.RaisePropertyChanged(nameof(SubagentCounts));
    }

    internal static string MiddleTruncate(string value, int head = 8, int tail = 8) {
        if (value.Length <= head + tail + 1) return value;
        return string.Concat(value.AsSpan(0, head), "…", value.AsSpan(value.Length - tail));
    }

    WorkContextPhase _phase = WorkContextPhase.WaitingForSession;
    public WorkContextPhase Phase {
        get => _phase;
        private set {
            if (_phase == value) return;
            this.RaiseAndSetIfChanged(ref _phase, value);
            this.RaisePropertyChanged(nameof(PhaseNote));
            this.RaisePropertyChanged(nameof(IsReady));
            this.RaisePropertyChanged(nameof(ShowsSignIn));
            this.RaisePropertyChanged(nameof(ShowsRetry));
            RaiseRelated();
        }
    }

    public string PhaseNote => Phase switch {
        WorkContextPhase.WaitingForSession or WorkContextPhase.SessionUnknown => WaitingNote,
        WorkContextPhase.Loading     => LoadingNote,
        WorkContextPhase.NoWorkItem  => _dto?.RepoPath is null && _storedRepository is null ? NoRepositoryNote : NoWorkItemNote,
        WorkContextPhase.SignedOut   => SignedOutNote,
        WorkContextPhase.NotInPlan   => NotInPlanNote,
        WorkContextPhase.Unreachable => UnreachableNote,
        _                            => "",
    };

    public bool IsReady     => Phase == WorkContextPhase.Ready;
    public bool ShowsSignIn => Phase == WorkContextPhase.SignedOut;
    public bool ShowsRetry  => Phase == WorkContextPhase.Unreachable;

    void OnPullRequestChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName is nameof(PullRequestContextViewModel.MergedPullRequests)) RaisePlanProgress();
        if (e.PropertyName is nameof(PullRequestContextViewModel.HasChoice)
            or nameof(PullRequestContextViewModel.HasNotice)
            or nameof(PullRequestContextViewModel.HasReaderNote)
            or nameof(PullRequestContextViewModel.HasPullRequest)
            or nameof(PullRequestContextViewModel.HasListed))
            RaiseRelated();
        if (e.PropertyName == nameof(PullRequestContextViewModel.HasStaleOverview))
            this.RaisePropertyChanged(nameof(IsStale));
    }

    string? _reportedBranch;
    /// The summary's branch, for a remote row whose listing carries none: the listing keeps the
    /// branch first seen with the repository, the primary repository row a later one.
    string? _storedBranch;
    /// The summary's repository name, for a remote row whose listing carries no cwd.
    string? _storedRepository;

    void ResolveRepository(string? repoPath) =>
        Repository = repoPath is null && _isRemote && _storedRepository is { } stored ? stored : RepoLabel.Leaf(repoPath);

    // The daemon reports the branch the worktree was cut on; HEAD knows a switch made since.
    void ResolveBranch(bool refresh = false) {
        if (_isRemote) {
            Branch = FirstNonBlank(_reportedBranch, _storedBranch) ?? "—";
            return;
        }
        var state = (Path: WorktreePath, Reported: _reportedBranch);
        if (_branchReader.HasCurrent && _branchReader.CurrentState == state) {
            if (refresh) _branchReader.Refresh();
            return;
        }
        Branch = string.IsNullOrWhiteSpace(_reportedBranch) ? "—" : _reportedBranch;
        _branchReader.Switch(state);
    }

    bool _isStale;
    public bool IsStale {
        get => _isStale || _pullRequests?.HasStaleOverview == true;
        private set {
            if (_isStale == value) return;
            _isStale = value;
            this.RaisePropertyChanged(nameof(IsStale));
        }
    }
    bool _isReading;
    public bool IsReading {
        get => _isReading;
        private set {
            if (_isReading == value) return;
            this.RaiseAndSetIfChanged(ref _isReading, value);
            if (!value) IsRefreshing = false;
            this.RaisePropertyChanged(nameof(RefreshTip));
        }
    }
    bool _isRefreshing;
    /// A refresh someone asked for, still reading; the 30s poll runs without it.
    public bool IsRefreshing {
        get => _isRefreshing;
        private set { if (_isRefreshing != value) { this.RaiseAndSetIfChanged(ref _isRefreshing, value); this.RaisePropertyChanged(nameof(RefreshTip)); } }
    }
    bool _hasSession;
    // Subject, not WhenAnyValue — same RxAppBuilder init trap as SessionRailViewModel.SelectedSessionKey.
    readonly BehaviorSubject<bool> _hasSessionChanges = new(false);
    public bool HasSession {
        get => _hasSession;
        private set {
            if (_hasSession == value) return;
            this.RaiseAndSetIfChanged(ref _hasSession, value);
            _hasSessionChanges.OnNext(value);
            this.RaisePropertyChanged(nameof(RefreshTip));
        }
    }

    bool _hasAgent;
    /// Until the daemon reports the agent, every session fact and the requester are placeholders.
    public bool HasAgent { get => _hasAgent; private set => this.RaiseAndSetIfChanged(ref _hasAgent, value); }

    /// Tip on the header refresh control — bound with ShowOnDisabled so a greyed icon still explains itself.
    public string RefreshTip => HasSession
        ? IsRefreshing ? "Refreshing…" : "Reloads the work item, its pull requests and the plan"
        : "Waiting for the session ID";

    public static string RefreshShortcutCaption =>
        RefreshShortcut.FromTerminalLabel is { } fromTerminal
            ? $"Refresh this work · {RefreshShortcut.Label} or {fromTerminal}"
            : $"Refresh this work · {RefreshShortcut.Label}";

    /// The Artefacts tab's model, when this pane's workspace has one; the pane switches and
    /// refreshes its session beside the plan's.
    public ArtefactsTabViewModel? Artefacts { get; }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    /// The item's own page in the web UI; enabled once a read has named the item.
    public ReactiveCommand<Unit, Unit> OpenWorkItemCommand { get; }
    public ReactiveCommand<Unit, Unit> SignInCommand { get; }

    /// Test-only seam: the current lease's read, or the last one started.
    internal Task? PendingReadForTesting => _reader.PendingReadForTesting;

    public WorkContextViewModel(
            IObservable<AgentStatusDto?> presence, IWorkContextSource source, TimeProvider time, IUrlOpener opener,
            SessionRuns runs, Action? requestSignIn = null, IObservable<Unit>? signInCompleted = null,
            Action<string>? openWorkItem = null, IPlanSource? plans = null, PlanActivity? planActivity = null,
            ArtefactsTabViewModel? artefacts = null, bool isRemote = false,
            IObservable<ServerLaneStatus>? serverLane = null, Func<bool>? canSendToAgent = null,
            Func<string, Task<bool>>? sendToAgent = null, WorkContextLayout? layout = null) {
        _isRemote = isRemote;
        _canSendToAgent = canSendToAgent ?? (() => false);
        _sendToAgent = sendToAgent ?? (_ => Task.FromResult(false));
        _layout = layout ?? new WorkContextLayout();
        _source = source;
        _branchReader = new SingleFlightReader<(string? Path, string? Reported), string>(
            (state, ct) => Task.Run(() => {
                var branch = GitRepository.CurrentBranch(state.Path) ?? state.Reported;
                return string.IsNullOrWhiteSpace(branch) ? "—" : branch;
            }, ct),
            _ => string.IsNullOrWhiteSpace(_reportedBranch) ? "—" : _reportedBranch,
            branch => Branch = branch,
            "checkout branch");
        _reader = new SingleFlightReader<string, WorkContextRead>(
            _source.ReadAsync,
            ex => WorkContextRead.Of(WorkContextReadKind.Unreachable, ex.Message),
            Apply,
            "work context",
            reading => IsReading = reading,
            cached: _source.Cached);
        Plan = new PlanSectionViewModel(plans, planActivity ?? new PlanActivity(), time);
        Plan.PropertyChanged += OnPlanProgressChanged;
        _disposables.Add(Disposable.Create(() => Plan.PropertyChanged -= OnPlanProgressChanged));
        Artefacts = artefacts;
        _opener = opener;
        _time = time;
        _openWorkItem = openWorkItem;
        _runs = runs;
        _runs.Changed += RefreshRuns;
        InitializeProjections();
        OpenRunCommand = ReactiveCommand.Create<RunRow>(row => { if (row.CanOpen) RunOpened?.Invoke(row); });
        _disposables.Add(OpenRunCommand);
        _disposables.Add(_hasSessionChanges);
        _disposables.Add(_canOpenWorkItem);

        // The pane's one refresh: the linked pull requests reload with the work item. Enabled for
        // any known session id. A click while a read is in flight queues one follow-up
        // (RefreshPending) instead of disabling the control — a greyed icon looked broken and ate
        // the click with no feedback.
        RefreshCommand = ReactiveCommand.Create(
            () => {
                ResolveBranch(refresh: true);
                PullRequests?.Refresh();
                Plan.Refresh();
                Artefacts?.Refresh();
                if (!_reader.HasCurrent) return;
                IsRefreshing = true;
                _reader.Refresh();
            },
            _hasSessionChanges);
        _disposables.Add(RefreshCommand);
        OpenWorkItemCommand = ReactiveCommand.Create(
            () => { if (WorkContextIds.ValidWorkItemId(PrimaryId) is { } id) _openWorkItem?.Invoke(id); },
            _canOpenWorkItem);
        _disposables.Add(OpenWorkItemCommand);
        SignInCommand = ReactiveCommand.Create(() => { requestSignIn?.Invoke(); });
        _disposables.Add(SignInCommand);
        CompactCommand = ReactiveCommand.CreateFromTask(CompactAsync);
        _disposables.Add(CompactCommand);

        presence.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(OnDto).DisposeWith(_disposables);
        if (serverLane is not null) {
            // Only an attempt nothing answered is evidence the server is down; a read in flight then ends in the
            // unreachable state rather than loading for as long as the outage lasts.
            serverLane.Select(s => s.State == ServerLaneState.Retrying && s.Failure is not null)
                .DistinctUntilChanged()
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(OnServerDown)
                .DisposeWith(_disposables);
            serverLane.Select(s => s.State).DistinctUntilChanged().Skip(1)
                .Where(state => state == ServerLaneState.Connected)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => Reconnected())
                .DisposeWith(_disposables);
        }
        signInCompleted?
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => OnSignInCompleted())
            .DisposeWith(_disposables);
        _timer = time.CreateTimer(_ => RxSchedulers.MainThreadScheduler.Schedule(OnTick), null, PollInterval, PollInterval);
    }

    void OnDto(AgentStatusDto? dto) {
        if (_tornDown || dto is null) return;
        var previous = _dto;
        _dto = dto;
        RaiseCompactState();
        // The chat reads a local agent's transcript by its path and a remote one by its session id.
        if (Artefacts is not null) Artefacts.TranscriptKey = _isRemote ? dto.SessionId : dto.TranscriptPath;
        UpdateFacts(dto);
        HasAgent = true;
        this.RaisePropertyChanged(nameof(ShowsPeople));
        if (dto.SessionId is { Length: > 0 } id && !string.Equals(_reader.CurrentState, id, StringComparison.Ordinal))
            SwitchSession(id);
        else if (_isRemote && string.IsNullOrEmpty(dto.SessionId) && HasSession) {
            _reader.ClearCurrent();
            HasSession = false;
            IsReading = false;
            SessionIdText = "resolving…";
            PrimaryRepository = null;
            ClearServerProjections();
            ApplySessionFacts(null);
            IsStale = false;
            Phase = WorkContextPhase.WaitingForSession;
            Plan.SwitchSession(null);
            Artefacts?.SwitchSession(null, null);
            _runs.Clear();
        }
        else if (_reader.HasCurrent && previous is not null
                 && ((dto.AwaitingInput == true && previous.AwaitingInput != true) || previous.Status != dto.Status))
            _reader.Refresh();
        this.RaisePropertyChanged(nameof(PhaseNote));
    }

    internal void AgentInputAvailabilityChanged() {
        if (!_tornDown) RaiseCompactState();
    }

    void RaiseCompactState() {
        this.RaisePropertyChanged(nameof(CurrentContextWarns));
        this.RaisePropertyChanged(nameof(ShowsCompact));
    }

    async Task CompactAsync() {
        if (_tornDown || !ShowsCompact) return;
        _compactPending = true;
        _compactStartedAt = _time.GetTimestamp();
        RaiseCompactState();
        bool sent;
        try { sent = await _sendToAgent("/compact"); }
        catch { sent = false; }
        if (_tornDown) return;
        if (!sent) {
            _compactPending = false;
            RaiseCompactState();
            return;
        }
        if (_compactPending) _timer?.Change(CompactPollInterval, CompactPollInterval);
    }

    void EndCompact() {
        if (!_compactPending) return;
        _compactPending = false;
        _timer?.Change(PollInterval, PollInterval);
        RaiseCompactState();
    }

    void UpdateFacts(AgentStatusDto dto) {
        ResolveRepository(dto.RepoPath);
        RepositoryPath = dto.RepoPath;
        var checkout = _isRemote ? dto.WorktreePath : CheckoutLabel.CheckoutPathFor(dto);
        WorktreePath = checkout;
        Worktree = checkout is null
            ? "—"
            : CheckoutLabel.Format(checkout, dto.RepoPath ?? "") + (dto.WorkLocation == WorkLocationText.Borrowed ? " · borrowed" : "");
        _reportedBranch = dto.Branch;
        ResolveBranch();
        var vendorLabel = HostedHarnessCatalog.LabelFor(DefaultHarnessOptions, dto.Vendor);
        Harness = $"{vendorLabel} · {HostedHarnessCatalog.ModelLabelFor(dto.Vendor, dto.Model ?? "")}";
        if (!_reader.HasCurrent) SessionIdText = SessionIds.Canonical(dto.SessionId) ?? "resolving…";
        UpdateRequester(dto, vendorLabel);
    }

    void SwitchSession(string id) {
        HasSession = true;
        SessionIdText = SessionIds.Canonical(id) ?? id;
        PrimaryRepository = null;
        ClearServerProjections();
        ApplySessionFacts(null);
        IsStale = false;
        Phase = WorkContextPhase.Loading;
        _reader.Switch(id);
        if (_serverDown && Phase == WorkContextPhase.Loading) Phase = WorkContextPhase.Unreachable;
        Plan.SwitchSession(id);
        Artefacts?.SwitchSession(id, _isRemote ? null : WorktreePath ?? RepositoryPath);
    }

    void OnServerDown(bool down) {
        if (_tornDown) return;
        _serverDown = down;
        Plan.SetServerDown(down);
        if (down && Phase == WorkContextPhase.Loading) Phase = WorkContextPhase.Unreachable;
    }

    void OnTick() {
        if (_tornDown) return;
        if (_compactPending && _time.GetElapsedTime(_compactStartedAt) >= CompactPollLimit) EndCompact();
        if (_dto is not null) ResolveBranch(refresh: true);
        Plan.Refresh();
        if (Artefacts is { IsShown: true } or { LastReadFailed: true }) Artefacts.Refresh();
        else Artefacts?.RefreshPages();
        _reader.StartIfIdle();
    }

    internal void Reconnected() {
        if (_tornDown) return;
        ForgetViewer();
        Plan.Refresh();
        Artefacts?.Refresh();
        _reader.Refresh();
    }

    void OnSignInCompleted() {
        if (_tornDown) return;
        ForgetViewer();
        Plan.Refresh();
        Artefacts?.Refresh();
        _reader.Refresh();
    }

    /// Applies one read for the current lease. Section-level merging lives in the projections half.
    void Apply(WorkContextRead read) {
        switch (read.Kind) {
            case WorkContextReadKind.SignedOut:      ApplyTerminal(WorkContextPhase.SignedOut);      return;
            case WorkContextReadKind.NotInPlan:
                // The plan gates work items only; a failed summary keeps the last session facts, stale.
                ApplyTerminal(WorkContextPhase.NotInPlan, keepSessionFacts: true);
                if (!read.SummaryFailed) ApplySessionFacts(read.Summary);
                else IsStale = _hasSessionFacts;
                return;
            case WorkContextReadKind.SessionUnknown: ApplyTerminal(WorkContextPhase.SessionUnknown); return;
            case WorkContextReadKind.Unreachable:
                // A plan-gated pane that holds session facts goes stale rather than blank.
                if (Phase is WorkContextPhase.Ready or WorkContextPhase.NoWorkItem
                    || (Phase == WorkContextPhase.NotInPlan && _hasSessionFacts)) IsStale = true;
                else Phase = WorkContextPhase.Unreachable;
                return;
            case WorkContextReadKind.Ready:
                ApplyReady(read);
                return;
            default:
                Phase = WorkContextPhase.Unreachable;
                return;
        }
    }

    /// The server has just said the viewer may not have this data, so nothing it gates stays visible.
    void ApplyTerminal(WorkContextPhase phase, bool keepSessionFacts = false) {
        ClearServerProjections();
        if (!keepSessionFacts) ApplySessionFacts(null);
        ForgetViewer();
        Phase = phase;
        IsStale = false;
    }

    public async Task TeardownAsync() {
        if (_tornDown) return;
        _tornDown = true;
        _runs.Changed -= RefreshRuns;
        _timer?.Dispose();
        _timer = null;
        _disposables.Dispose();
        await _reader.TeardownAsync();
        await _branchReader.TeardownAsync();
        await Plan.TeardownAsync();
    }
}
