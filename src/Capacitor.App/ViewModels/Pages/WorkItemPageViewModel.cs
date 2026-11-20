using System.Collections.Frozen;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using Lucide.Avalonia;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Pages;

/// A work item's page in the main area: its goal and slices grouped by status, with its links and Pages beside them. Statuses and progress
/// are the server's, the same for every viewer; sessions and "Needs you" cover only what this viewer can see.
public sealed class WorkItemPageViewModel : ReactiveObject, IPage {
    const string LooseEndsKey = "loose_ends";
    const string OnWorkItemKey = "on_work_item";
    const string BoardKey = "board";
    // The server refuses a longer slice title.
    const int MaxSliceTitle = 200;
    static readonly WorkStatus[] BoardOrder = [WorkStatus.Open, WorkStatus.InProgress, WorkStatus.InReview, WorkStatus.Blocked, WorkStatus.Done];

    readonly IWorkItemPageApi _api;
    readonly IAgentDirectory _directory;
    readonly TimeProvider _time;
    readonly ISet<string> _flipped;
    readonly HashSet<string> _openedParts = new(StringComparer.Ordinal);
    // A status the viewer just set, shown until a read reports it: the read right after the change can come back
    // before the server's read model has it.
    readonly Dictionary<string, string> _setStatuses = new(StringComparer.Ordinal);
    // A slice order the viewer just set, shown until a read reports it, for the same reason.
    List<string>? _setOrder;
    List<string> _order = [];
    // Sessions the viewer just moved, by id, shown where they went until a read reports them gone from where they were.
    // A null destination hides one until the slice it is going to appears.
    readonly Dictionary<string, (string From, string? To)> _sessionMoves = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _containerOf = new(StringComparer.Ordinal);
    // Rows that appear without being dragged there, marked as arriving the first time they are built.
    readonly HashSet<string> _arrivals = new(StringComparer.Ordinal);
    readonly Subject<Unit> _reread = new();
    readonly CompositeDisposable _disposables = new();
    WorkItemPartsDto? _read;
    bool _held;
    bool _heldRebuild;
    IReadOnlySet<string> _needsYou = FrozenSet<string>.Empty;

    public string WorkItemId { get; }
    public PageKey Key => new PageKey.WorkItem(WorkItemId);

    string _title = "";
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }
    string? _goal;
    public string? Goal { get => _goal; private set => this.RaiseAndSetIfChanged(ref _goal, value); }
    IReadOnlyList<WorkItemPageLink> _links = [];
    public IReadOnlyList<WorkItemPageLink> Links { get => _links; private set => this.RaiseAndSetIfChanged(ref _links, value); }
    IReadOnlyList<WorkItemPublishedPage> _pages = [];
    public IReadOnlyList<WorkItemPublishedPage> Pages { get => _pages; private set => this.RaiseAndSetIfChanged(ref _pages, value); }
    /// The done share of the progress ring, in degrees.
    double _progressSweep;
    public double ProgressSweep { get => _progressSweep; private set => this.RaiseAndSetIfChanged(ref _progressSweep, value); }
    string _progressText = "";
    public string ProgressText { get => _progressText; private set => this.RaiseAndSetIfChanged(ref _progressText, value); }
    IReadOnlyList<WorkItemPageGroupViewModel> _groups = [];
    public IReadOnlyList<WorkItemPageGroupViewModel> Groups { get => _groups; private set => this.RaiseAndSetIfChanged(ref _groups, value); }
    /// The board's columns: the sessions on no slice, the loose ends when there are any, then one per status in workflow
    /// order. A column that takes drops is there even when empty.
    IReadOnlyList<WorkItemPageGroupViewModel> _columns = [];
    public IReadOnlyList<WorkItemPageGroupViewModel> Columns { get => _columns; private set => this.RaiseAndSetIfChanged(ref _columns, value); }
    bool _isBoard;
    public bool IsBoard { get => _isBoard; private set => this.RaiseAndSetIfChanged(ref _isBoard, value); }
    bool _isLoading = true;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }
    string? _message;
    public string? Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }

    string _newPartTitle = "";
    public string NewPartTitle { get => _newPartTitle; set => this.RaiseAndSetIfChanged(ref _newPartTitle, value); }
    WorkStatus? _composing;
    /// The status group a new slice is being typed into; null when none is.
    public WorkStatus? Composing { get => _composing; private set => this.RaiseAndSetIfChanged(ref _composing, value); }
    public IReadOnlyList<WorkItemPageStatus> Statuses { get; } = [.. BoardOrder.Select(status => new WorkItemPageStatus(status))];
    string _newLinkReference = "";
    public string NewLinkReference { get => _newLinkReference; set => this.RaiseAndSetIfChanged(ref _newLinkReference, value); }

    PageViewerViewModel? _pageViewer;
    CancellationTokenSource? _pageLifetime;
    /// The Page shown in the main column in place of the slices; null shows the slices.
    public PageViewerViewModel? PageViewer { get => _pageViewer; private set => this.RaiseAndSetIfChanged(ref _pageViewer, value); }

    public ReactiveCommand<Unit, Unit> OpenInWebCommand { get; }
    public ReactiveCommand<WorkItemPageLink, Unit> OpenLinkCommand { get; }
    public ReactiveCommand<WorkItemPublishedPage, Unit> OpenPageCommand { get; }
    public ReactiveCommand<Unit, Unit> ClosePageCommand { get; }
    public ReactiveCommand<WorkItemPageSession, Unit> OpenSessionCommand { get; }
    public ReactiveCommand<WorkItemPageLooseEnd, Unit> MakePartCommand { get; }
    public ReactiveCommand<WorkStatus, Unit> ComposeCommand { get; }
    public ReactiveCommand<Unit, Unit> AddPartCommand { get; }
    public ReactiveCommand<Unit, Unit> AddLinkCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowListCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowBoardCommand { get; }
    public SessionTitleEditorViewModel TitleEditor { get; }
    /// Edits the goal in place; a blank one clears it.
    public SessionTitleEditorViewModel GoalEditor { get; }

    /// <param name="flipped">What the viewer turned from its default on work item pages, kept for the life of the app:
    /// the groups they opened or closed, and the board when they chose it over the list.</param>
    /// <param name="needsYou">The agents waiting on the viewer, such as for a permission.</param>
    /// <param name="viewPage">A viewer for a Page by id and URL, or null when Pages open in the browser right now.</param>
    public WorkItemPageViewModel(string workItemId, IWorkItemPageApi api, IAgentDirectory directory, TimeProvider time,
            ISet<string> flipped, IObservable<IReadOnlySet<string>>? needsYou = null, IObservable<Unit>? changed = null,
            Action<string>? openSession = null, Action<string>? openUrl = null, Action? openInWeb = null,
            Func<string, string, PageViewerViewModel?>? viewPage = null) {
        WorkItemId = workItemId;
        _api = api;
        _directory = directory;
        _time = time;
        _flipped = flipped;
        _disposables.Add(_reread);
        _isBoard = flipped.Contains(BoardKey);

        OpenInWebCommand = ReactiveCommand.Create(() => openInWeb?.Invoke(), Observable.Return(openInWeb is not null)).DisposeWith(_disposables);
        OpenLinkCommand = ReactiveCommand.Create<WorkItemPageLink>(link => { if (link.Url is { } url) openUrl?.Invoke(url); }).DisposeWith(_disposables);
        OpenPageCommand = ReactiveCommand.Create<WorkItemPublishedPage>(page => {
            if (PageViewer?.ArtefactId == page.ArtefactId) return;
            if (viewPage?.Invoke(page.ArtefactId, page.Url) is { } viewer) ShowPage(viewer);
            else openUrl?.Invoke(page.Url);
        }).DisposeWith(_disposables);
        ClosePageCommand = ReactiveCommand.Create(ClosePage).DisposeWith(_disposables);
        OpenSessionCommand = ReactiveCommand.Create<WorkItemPageSession>(session => openSession?.Invoke(session.SessionId)).DisposeWith(_disposables);
        MakePartCommand = ReactiveCommand.CreateFromTask<WorkItemPageLooseEnd>(looseEnd =>
            ChangeAsync(ct => _api.MakePartAsync(WorkItemId, looseEnd.LooseEndId, ct), "Couldn't make a slice from that loose end")).DisposeWith(_disposables);
        ComposeCommand = ReactiveCommand.Create<WorkStatus>(Compose).DisposeWith(_disposables);
        // Completes at once, so the next title can be entered while the last is still being added.
        AddPartCommand = ReactiveCommand.Create(() => { _ = AddPartAsync(Composing!.Value); },
            this.WhenAnyValue(x => x.NewPartTitle, x => x.Composing, (title, status) => status is not null && !string.IsNullOrWhiteSpace(title)))
            .DisposeWith(_disposables);
        AddLinkCommand = ReactiveCommand.CreateFromTask(async () => {
            if (await ChangeAsync(ct => _api.AddLinkAsync(WorkItemId, NewLinkReference.Trim(), ct), "Couldn't add that link")) NewLinkReference = "";
        }, this.WhenAnyValue(x => x.NewLinkReference, reference => !string.IsNullOrWhiteSpace(reference))).DisposeWith(_disposables);

        ShowListCommand = ReactiveCommand.Create(() => ShowBoard(false)).DisposeWith(_disposables);
        ShowBoardCommand = ReactiveCommand.Create(() => ShowBoard(true)).DisposeWith(_disposables);

        var notifier = new AppNotifier();
        notifier.Messages.Subscribe(message => Message = message).DisposeWith(_disposables);
        TitleEditor = new SessionTitleEditorViewModel(this.WhenAnyValue(x => x.Title), Observable.Return(true),
            (title, _) => SaveAsync(() => _api.RenameAsync(WorkItemId, title, CancellationToken.None), () => Title = title, title),
            notifier, "work item").DisposeWith(_disposables);
        GoalEditor = new SessionTitleEditorViewModel(this.WhenAnyValue(x => x.Goal, goal => goal ?? ""), Observable.Return(true),
            (goal, _) => SaveAsync(() => _api.SetGoalAsync(WorkItemId, goal.Length == 0 ? null : goal, CancellationToken.None),
                () => Goal = goal.Length == 0 ? null : goal, goal),
            notifier, "goal", draft => draft.Trim(), "save").DisposeWith(_disposables);

        Observable.Switch((changed ?? Observable.Never<Unit>()).Merge(_reread).StartWith(Unit.Default)
                .Select(_ => Observable.FromAsync(ct => _api.ReadAsync(WorkItemId, ct)).Catch(Observable.Return<WorkItemPartsDto?>(null))))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(read => {
                IsLoading = false;
                if (read is null) {
                    if (_read is null) Message = "Couldn't load this work item";
                    return;
                }
                _read = read;
                Message = null;
                Rebuild();
            })
            .DisposeWith(_disposables);
        needsYou?.ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(agents => {
                _needsYou = agents;
                Rebuild();
            })
            .DisposeWith(_disposables);
    }

    public IReadOnlyList<RowMenuItem> MenuFor(WorkItemPageSession session) => [
        new(LucideIconKind.X, "Remove from work item",
            () => _ = ChangeAsync(ct => _api.RemoveSessionAsync(WorkItemId, session.SessionId, ct), "Couldn't remove that session")),
    ];

    public IReadOnlyList<RowMenuItem> MenuFor(WorkItemPagePart part) {
        var items = new List<RowMenuItem>();
        if (Neighbour(part, -1) is { } above && PlaceAt(part, above, after: false) is { } up) items.Add(new(LucideIconKind.ArrowUp, "Move up", up));
        if (Neighbour(part, 1) is { } below && PlaceAt(part, below, after: true) is { } down) items.Add(new(LucideIconKind.ArrowDown, "Move down", down));
        items.AddRange(Enum.GetValues<WorkStatus>().Where(status => status != part.Status)
            .Select(status => new RowMenuItem(status.Icon(), $"Move to {status.Label()}", () => part.SetStatusCommand.Execute(status.Wire()).Subscribe(), status.BrushKey())));
        return items;
    }

    /// What dropping a dragged slice or session on <paramref name="target"/>, a slice or a group, does; null when it does
    /// nothing there. <paramref name="after"/> puts a dropped slice below the target slice rather than above it, a
    /// slice dropped among another status's slices takes that status, a session dropped on a status becomes a new
    /// slice there, and a slice dropped on the other sessions hands them its sessions.
    public Action? DropOn(object dragged, object target, bool after) => (dragged, target) switch {
        (WorkItemPageSession session, WorkItemPagePart part) when part.Sessions.All(s => s.SessionId != session.SessionId) =>
            () => _ = MoveSessionAsync(session, part.Id),
        (WorkItemPageSession session, WorkItemPageGroupViewModel { Key: OnWorkItemKey } group) when group.Sessions.All(s => s.SessionId != session.SessionId) =>
            () => _ = MoveSessionAsync(session, WorkItemId),
        (WorkItemPageSession session, WorkItemPageGroupViewModel { Status: { } status }) => () => _ = SliceFromAsync(session, status),
        (WorkItemPagePart part, WorkItemPageGroupViewModel { Key: OnWorkItemKey }) when part.HasSessions => () => _ = MoveSessionsOutAsync(part),
        (WorkItemPagePart part, WorkItemPagePart onto) => PlaceAt(part, onto, after),
        (WorkItemPagePart part, WorkItemPageGroupViewModel { Status: { } status }) when status != part.Status =>
            () => part.SetStatusCommand.Execute(status.Wire()).Subscribe(),
        _ => null,
    };

    // Opens the slice it lands in, so the session is seen there.
    Task MoveSessionAsync(WorkItemPageSession session, string containerId) {
        if (containerId != WorkItemId) _openedParts.Add(containerId);
        return ShiftAsync([session.SessionId], containerId, ct => _api.MoveSessionAsync(containerId, session.SessionId, ct), "Couldn't move that session");
    }

    Task MoveSessionsOutAsync(WorkItemPagePart part) {
        _arrivals.UnionWith(part.Sessions.Select(session => ArrivalKey(WorkItemId, session.SessionId)));
        return ShiftAsync([.. part.Sessions.Select(session => session.SessionId)], WorkItemId, async ct => {
            foreach (var session in part.Sessions)
                if (!await _api.MoveSessionAsync(WorkItemId, session.SessionId, ct)) return false;
            return true;
        }, "Couldn't move those sessions");
    }

    async Task SliceFromAsync(WorkItemPageSession session, WorkStatus status) {
        var title = session.Title.Length > MaxSliceTitle ? session.Title[..MaxSliceTitle] : session.Title;
        string? sliceId = null;
        if (!await ShiftAsync([session.SessionId], null, async ct => (sliceId = await _api.AddPartAsync(WorkItemId, title, ct)) is not null
                && await _api.MoveSessionAsync(sliceId, session.SessionId, ct)
                && await _api.SetStatusAsync(sliceId, status.Wire(), ct),
            "Couldn't make a slice from that session")) return;
        _setStatuses[sliceId!] = status.Wire();
        _arrivals.Add(sliceId!);
        Rebuild();
    }

    void Compose(WorkStatus status) {
        if (ExpandedByDefault(status)) _flipped.Remove(status.Wire());
        else _flipped.Add(status.Wire());
        Composing = status;
        Rebuild();
    }

    public void CloseComposer() {
        if (Composing is null) return;
        Composing = null;
        NewPartTitle = "";
        Rebuild();
    }

    // The field clears at once for the next title; a refused one comes back unless another is already being typed.
    async Task AddPartAsync(WorkStatus status) {
        var title = NewPartTitle.Trim();
        if (title.Length > MaxSliceTitle) title = title[..MaxSliceTitle];
        NewPartTitle = "";
        string? sliceId = null;
        // A new slice reads as Open; setting that would pin it there instead of letting its links decide.
        if (!await ChangeAsync(async ct => (sliceId = await _api.AddPartAsync(WorkItemId, title, ct)) is not null
                && (status == WorkStatus.Open || await _api.SetStatusAsync(sliceId, status.Wire(), ct)),
            "Couldn't add that slice")) {
            if (sliceId is null && NewPartTitle.Length == 0) NewPartTitle = title;
            return;
        }
        if (status != WorkStatus.Open) _setStatuses[sliceId!] = status.Wire();
        _arrivals.Add(sliceId!);
        Rebuild();
    }

    static bool ExpandedByDefault(WorkStatus status) => status != WorkStatus.Done;

    // Shows the sessions where they go before the change is sent, so a drop lands where it was let go; a refusal puts them back.
    async Task<bool> ShiftAsync(IReadOnlyList<string> sessionIds, string? to, Func<CancellationToken, Task<bool>> change, string failure) {
        foreach (var id in sessionIds)
            _sessionMoves[id] = (_sessionMoves.TryGetValue(id, out var earlier) ? earlier.From : _containerOf[id], to);
        Rebuild();
        if (await ChangeAsync(change, failure)) return true;
        if (_disposables.IsDisposed) return false;
        foreach (var id in sessionIds) _sessionMoves.Remove(id);
        Rebuild();
        return false;
    }

    WorkItemPagePart? Neighbour(WorkItemPagePart part, int step) {
        if (Groups.FirstOrDefault(group => group.Parts.Contains(part))?.Parts.ToList() is not { } inGroup) return null;
        var at = inGroup.IndexOf(part) + step;
        return at >= 0 && at < inGroup.Count ? inGroup[at] : null;
    }

    Action? PlaceAt(WorkItemPagePart part, WorkItemPagePart onto, bool after) {
        if (part.Id == onto.Id) return null;
        var order = _order.Where(id => id != part.Id).ToList();
        order.Insert(order.IndexOf(onto.Id) + (after ? 1 : 0), part.Id);
        var status = onto.Status == part.Status ? (WorkStatus?)null : onto.Status;
        if (status is null && order.SequenceEqual(_order)) return null;
        return () => _ = PlaceAsync(part, order, status);
    }

    // Shown in its new place at once. A refused order puts the slice back, which says so without a message, and reads
    // again in case another slice came or went.
    async Task PlaceAsync(WorkItemPagePart part, List<string> order, WorkStatus? status) {
        var reorders = !order.SequenceEqual(_order);
        if (reorders) _setOrder = order;
        if (status is { } moved) await part.SetStatusCommand.Execute(moved.Wire());
        else Rebuild();
        if (!reorders) return;
        var recorded = await _api.OrderPartsAsync(WorkItemId, order, CancellationToken.None);
        if (_disposables.IsDisposed || _setOrder != order) return;
        if (!recorded) {
            _setOrder = null;
            Rebuild();
        }
        _reread.OnNext(Unit.Default);
    }

    void ShowBoard(bool board) {
        IsBoard = board;
        if (board) _flipped.Add(BoardKey);
        else _flipped.Remove(BoardKey);
    }

    void ShowPage(PageViewerViewModel viewer) {
        ClosePage();
        _pageLifetime = new CancellationTokenSource();
        PageViewer = viewer;
        _ = LoadPageAsync(viewer, _pageLifetime.Token);
    }

    static async Task LoadPageAsync(PageViewerViewModel viewer, CancellationToken ct) {
        try { await viewer.LoadAsync(ct); }
        catch (Exception ex) { Console.Error.WriteLine($"kcap: work item page load: {ex.Message}"); }
    }

    void ClosePage() {
        if (PageViewer is not { } viewer) return;
        _pageLifetime?.Cancel();
        _pageLifetime?.Dispose();
        _pageLifetime = null;
        viewer.Close();
        PageViewer = null;
    }

    // Not cancelled when the page closes, so leaving right after Enter keeps the edit. Shows the saved text at once: the
    // read that follows can come back before the server's read model has it.
    async Task<SessionRenameResult> SaveAsync(Func<Task<bool>> send, Action show, string saved) {
        Message = null;
        if (!await send()) return new SessionRenameResult(false, null, null);
        if (_disposables.IsDisposed) return new SessionRenameResult(true, saved, null);
        show();
        _reread.OnNext(Unit.Default);
        return new SessionRenameResult(true, saved, null);
    }

    async Task<bool> ChangeAsync(Func<CancellationToken, Task<bool>> change, string failure) {
        Message = null;
        var recorded = await change(CancellationToken.None);
        if (_disposables.IsDisposed) return recorded;
        if (recorded) _reread.OnNext(Unit.Default);
        else Message = failure;
        return recorded;
    }

    /// While a row is being dragged, rebuilds wait for it to end: a rebuild replaces the rows the drag is moving among.
    public void HoldRebuilds(bool held) {
        _held = held;
        if (held || !_heldRebuild) return;
        _heldRebuild = false;
        Rebuild();
    }

    void Rebuild() {
        if (_held) {
            _heldRebuild = true;
            return;
        }
        if (_read is not { } read) return;
        var rows = new Dictionary<string, AgentRow>(StringComparer.Ordinal);
        foreach (var row in _directory.Rows.Items)
            if (row.SessionId is { Length: > 0 } id) rows.TryAdd(id, row);

        Title = read.WorkItem.Title;
        Goal = string.IsNullOrWhiteSpace(read.Goal) ? null : read.Goal.Trim();
        Links = LinksOf(read.Links);
        Pages = [.. read.Pages.Select(page => new WorkItemPublishedPage(
            page.ArtefactId, string.IsNullOrWhiteSpace(page.Title) ? "Untitled page" : page.Title, $"v{page.LatestVersion} · {Ago(page.UpdatedAt)}", page.Url))];

        var located = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in read.Parts)
            foreach (var id in part.SessionIds) located[id] = part.WorkItemId;
        foreach (var id in read.SessionIds) located[id] = WorkItemId;
        foreach (var (id, move) in _sessionMoves.ToList())
            if (located.GetValueOrDefault(id) != move.From) _sessionMoves.Remove(id);
        var named = read.Sessions.Concat(read.Parts.SelectMany(part => part.Sessions)).ToList();
        _containerOf.Clear();

        var readOrder = read.Parts.Select(part => part.WorkItemId).ToList();
        if (_setOrder is { } set && (set.SequenceEqual(readOrder) || !set.ToHashSet(StringComparer.Ordinal).SetEquals(readOrder))) _setOrder = null;
        var parts = (_setOrder is { } order ? read.Parts.OrderBy(part => order.IndexOf(part.WorkItemId)) : read.Parts.AsEnumerable())
            .Select(part => PartOf(part, rows, named)).ToList();
        _order = [.. parts.Select(part => part.Id)];
        var done = parts.Count(part => part.Status == WorkStatus.Done);
        ProgressText = parts.Count == 0 ? "" : $"{done}/{parts.Count}";
        ProgressSweep = parts.Count == 0 ? 0 : 360.0 * done / parts.Count;

        var byStatus = Enum.GetValues<WorkStatus>().ToDictionary(status => status, status => Group(status.Wire(), status, status.Label(),
            status.Icon(), parts: [.. parts.Where(part => part.Status == status)], expandedByDefault: ExpandedByDefault(status),
            isComposing: status == Composing));
        var looseEnds = Group(LooseEndsKey, null, "Loose ends", LucideIconKind.Flag,
            looseEnds: [.. read.LooseEnds.Select(looseEnd => new WorkItemPageLooseEnd(looseEnd.LooseEndId, looseEnd.Text))]);
        var otherSessions = Group(OnWorkItemKey, null, "Other sessions", LucideIconKind.MessagesSquare,
            sessions: [.. Placed(WorkItemId, read.SessionIds).Select(id => SessionOf(WorkItemId, id, rows, named))]);
        Groups = [.. Enum.GetValues<WorkStatus>().Where(status => status != WorkStatus.Done).Select(status => byStatus[status])
            .Append(looseEnds).Append(byStatus[WorkStatus.Done]).Append(otherSessions).Where(group => group.Count > 0 || group.IsComposing)];
        Columns = [otherSessions, .. looseEnds.Count > 0 ? [looseEnds] : Array.Empty<WorkItemPageGroupViewModel>(),
            .. BoardOrder.Select(status => byStatus[status])];
    }

    WorkItemPageGroupViewModel Group(string key, WorkStatus? status, string label, LucideIconKind icon,
            IReadOnlyList<WorkItemPagePart>? parts = null, IReadOnlyList<WorkItemPageSession>? sessions = null,
            IReadOnlyList<WorkItemPageLooseEnd>? looseEnds = null, bool expandedByDefault = true, bool isComposing = false) =>
        new(key, status, label, icon, parts ?? [], sessions ?? [], looseEnds ?? [],
            expandedByDefault ^ _flipped.Contains(key), expanded => {
            if (expanded == expandedByDefault) _flipped.Remove(key);
            else _flipped.Add(key);
        }, isComposing);

    IEnumerable<string> Placed(string containerId, IEnumerable<string> read) =>
        read.Where(id => !_sessionMoves.ContainsKey(id)).Concat(_sessionMoves.Where(move => move.Value.To == containerId).Select(move => move.Key));

    WorkItemPagePart PartOf(WorkItemPartDetailDto part, Dictionary<string, AgentRow> rows, IReadOnlyList<WorkItemPartSessionDto> named) {
        if (_setStatuses.TryGetValue(part.WorkItemId, out var set) && set == part.Status) _setStatuses.Remove(part.WorkItemId);
        var status = WorkStatusExtensions.ParseWorkStatus(_setStatuses.GetValueOrDefault(part.WorkItemId, part.Status));
        var tip = status == WorkStatus.Blocked && part.WaitsOn.Count > 0
            ? $"Waits on {string.Join(", ", part.WaitsOn.Select(other => other.Title))}"
            : null;
        var id = part.WorkItemId;
        var sessionIds = Placed(id, part.SessionIds).ToList();
        var needsYou = sessionIds.Any(sessionId => rows.TryGetValue(sessionId, out var row) && _needsYou.Contains(row.Id));
        // A set status shows at once and a refused one is put back; handing the slice back to its links waits for the
        // server, since only it knows what they work out to.
        var setStatus = ReactiveCommand.CreateFromTask<string>(async wire => {
            var had = _setStatuses.TryGetValue(id, out var previous);
            if (wire.Length > 0) {
                _setStatuses[id] = wire;
                Rebuild();
            }
            if (await ChangeAsync(ct => _api.SetStatusAsync(id, wire.Length == 0 ? null : wire, ct), "Couldn't set that status")) {
                if (wire.Length > 0) return;
                _setStatuses.Remove(id);
            }
            else if (had) _setStatuses[id] = previous!;
            else _setStatuses.Remove(id);
            Rebuild();
        });
        return new WorkItemPagePart(id, part.Title, status, tip, needsYou, LinksOf(part.Links),
            [.. sessionIds.Select(sessionId => SessionOf(id, sessionId, rows, named))], _openedParts.Contains(id),
            expanded => { if (expanded) _openedParts.Add(id); else _openedParts.Remove(id); }, setStatus, _arrivals.Remove(id));
    }

    static IReadOnlyList<WorkItemPageLink> LinksOf(IEnumerable<WorkItemLinkDto> links) =>
        [.. links.Where(link => link.LinkClass != "reference")
            .Select(link => new WorkItemPageLink(link.ShortKey, link.Title, link.State, link.Url, link.Kind == "pr"))];

    static string ArrivalKey(string containerId, string sessionId) => $"{containerId}/{sessionId}";

    /// <param name="containerId">The slice, or the work item itself, the session sits in.</param>
    WorkItemPageSession SessionOf(string containerId, string sessionId, Dictionary<string, AgentRow> rows, IReadOnlyList<WorkItemPartSessionDto> named) {
        _containerOf[sessionId] = containerId;
        var arrives = _arrivals.Remove(ArrivalKey(containerId, sessionId));
        if (!rows.TryGetValue(sessionId, out var row)) {
            if (named.FirstOrDefault(session => session.SessionId == sessionId) is not { } known)
                return new(sessionId, $"Session {sessionId[..Math.Min(8, sessionId.Length)]}", null, null, arrives);
            return new(sessionId, known.Title is { Length: > 0 } knownTitle ? knownTitle : "Untitled session",
                known.LastActivityAt is { } moved ? Ago(moved) : null, known.Vendor, arrives);
        }
        var stamp = row.LastActivityAt ?? row.CreatedAt;
        var age = Ago(new DateTimeOffset(stamp.Kind == DateTimeKind.Local ? stamp.ToUniversalTime() : DateTime.SpecifyKind(stamp, DateTimeKind.Utc)));
        return new(sessionId, row.Title is { Length: > 0 } title ? title : "Untitled session",
            row.RepoGroupLabel is { Length: > 0 } repo ? $"{repo} · {age}" : age, row.Vendor, arrives);
    }

    string Ago(DateTimeOffset at) => RelativeTime.Ago(at, _time.GetUtcNow(), _time.LocalTimeZone);

    public void Dispose() {
        ClosePage();
        _disposables.Dispose();
    }
}
