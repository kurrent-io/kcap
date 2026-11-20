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

    readonly IWorkItemPageApi _api;
    readonly IAgentDirectory _directory;
    readonly TimeProvider _time;
    readonly ISet<string> _flippedGroups;
    readonly HashSet<string> _openedParts = new(StringComparer.Ordinal);
    // A status the viewer just set, shown until a read reports it: the read right after the change can come back
    // before the server's read model has it.
    readonly Dictionary<string, string> _setStatuses = new(StringComparer.Ordinal);
    readonly Subject<Unit> _reread = new();
    readonly CompositeDisposable _disposables = new();
    WorkItemPartsDto? _read;
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
    bool _isLoading = true;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }
    string? _message;
    public string? Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }

    string _newPartTitle = "";
    public string NewPartTitle { get => _newPartTitle; set => this.RaiseAndSetIfChanged(ref _newPartTitle, value); }
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
    public ReactiveCommand<Unit, Unit> AddPartCommand { get; }
    public ReactiveCommand<Unit, Unit> AddLinkCommand { get; }
    public SessionTitleEditorViewModel TitleEditor { get; }
    /// Edits the goal in place; a blank one clears it.
    public SessionTitleEditorViewModel GoalEditor { get; }

    /// <param name="flippedGroups">The groups the viewer opened or closed against their default, kept for the
    /// life of the app.</param>
    /// <param name="needsYou">The agents waiting on the viewer, such as for a permission.</param>
    /// <param name="viewPage">A viewer for a Page by id and URL, or null when Pages open in the browser right now.</param>
    public WorkItemPageViewModel(string workItemId, IWorkItemPageApi api, IAgentDirectory directory, TimeProvider time,
            ISet<string> flippedGroups, IObservable<IReadOnlySet<string>>? needsYou = null, IObservable<Unit>? changed = null,
            Action<string>? openSession = null, Action<string>? openUrl = null, Action? openInWeb = null,
            Func<string, string, PageViewerViewModel?>? viewPage = null) {
        WorkItemId = workItemId;
        _api = api;
        _directory = directory;
        _time = time;
        _flippedGroups = flippedGroups;
        _disposables.Add(_reread);

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
        AddPartCommand = ReactiveCommand.CreateFromTask(async () => {
            if (await ChangeAsync(async ct => await _api.AddPartAsync(WorkItemId, NewPartTitle.Trim(), ct) is not null, "Couldn't add that slice")) NewPartTitle = "";
        }, this.WhenAnyValue(x => x.NewPartTitle, title => !string.IsNullOrWhiteSpace(title))).DisposeWith(_disposables);
        AddLinkCommand = ReactiveCommand.CreateFromTask(async () => {
            if (await ChangeAsync(ct => _api.AddLinkAsync(WorkItemId, NewLinkReference.Trim(), ct), "Couldn't add that link")) NewLinkReference = "";
        }, this.WhenAnyValue(x => x.NewLinkReference, reference => !string.IsNullOrWhiteSpace(reference))).DisposeWith(_disposables);

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

    public IReadOnlyList<RowMenuItem> MenuFor(WorkItemPagePart part) => [
        .. Enum.GetValues<WorkStatus>().Where(status => status != part.Status)
            .Select(status => new RowMenuItem(status.Icon(), $"Move to {status.Label()}", () => part.SetStatusCommand.Execute(status.Wire()).Subscribe(), status.BrushKey())),
        new(LucideIconKind.Link, "Work it out from links", () => part.SetStatusCommand.Execute("").Subscribe()),
    ];

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

    void Rebuild() {
        if (_read is not { } read) return;
        var rows = new Dictionary<string, AgentRow>(StringComparer.Ordinal);
        foreach (var row in _directory.Rows.Items)
            if (row.SessionId is { Length: > 0 } id) rows.TryAdd(id, row);

        Title = read.WorkItem.Title;
        Goal = string.IsNullOrWhiteSpace(read.Goal) ? null : read.Goal.Trim();
        Links = LinksOf(read.Links);
        Pages = [.. read.Pages.Select(page => new WorkItemPublishedPage(
            page.ArtefactId, string.IsNullOrWhiteSpace(page.Title) ? "Untitled page" : page.Title, $"v{page.LatestVersion} · {Ago(page.UpdatedAt)}", page.Url))];

        var parts = read.Parts.Select(part => PartOf(part, rows)).ToList();
        var done = parts.Count(part => part.Status == WorkStatus.Done);
        ProgressText = parts.Count == 0 ? "" : $"{done}/{parts.Count}";
        ProgressSweep = parts.Count == 0 ? 0 : 360.0 * done / parts.Count;

        var groups = new List<WorkItemPageGroupViewModel>();
        void AddStatus(WorkStatus status) {
            var inGroup = parts.Where(part => part.Status == status).ToList();
            if (inGroup.Count > 0) groups.Add(Group(status.Wire(), status, status.Label(), status.Icon(), parts: inGroup, expandedByDefault: status != WorkStatus.Done));
        }
        foreach (var status in Enum.GetValues<WorkStatus>().Where(status => status != WorkStatus.Done)) AddStatus(status);
        var looseEnds = read.LooseEnds.Select(looseEnd => new WorkItemPageLooseEnd(looseEnd.LooseEndId, looseEnd.Text)).ToList();
        if (looseEnds.Count > 0) groups.Add(Group(LooseEndsKey, null, "Loose ends", LucideIconKind.Flag, looseEnds: looseEnds));
        AddStatus(WorkStatus.Done);
        var sessions = read.SessionIds.Select(id => SessionOf(id, rows, read.Sessions)).ToList();
        if (sessions.Count > 0) groups.Add(Group(OnWorkItemKey, null, "Other sessions", LucideIconKind.MessagesSquare, sessions: sessions));
        Groups = groups;
    }

    WorkItemPageGroupViewModel Group(string key, WorkStatus? status, string label, LucideIconKind icon,
            IReadOnlyList<WorkItemPagePart>? parts = null, IReadOnlyList<WorkItemPageSession>? sessions = null,
            IReadOnlyList<WorkItemPageLooseEnd>? looseEnds = null, bool expandedByDefault = true) =>
        new(key, status, label, icon, parts ?? [], sessions ?? [], looseEnds ?? [],
            expandedByDefault ^ _flippedGroups.Contains(key), expanded => {
            if (expanded == expandedByDefault) _flippedGroups.Remove(key);
            else _flippedGroups.Add(key);
        });

    WorkItemPagePart PartOf(WorkItemPartDetailDto part, Dictionary<string, AgentRow> rows) {
        if (_setStatuses.TryGetValue(part.WorkItemId, out var set) && set == part.Status) _setStatuses.Remove(part.WorkItemId);
        var status = WorkStatusExtensions.ParseWorkStatus(_setStatuses.GetValueOrDefault(part.WorkItemId, part.Status));
        var tip = status == WorkStatus.Blocked && part.WaitsOn.Count > 0
            ? $"Waits on {string.Join(", ", part.WaitsOn.Select(other => other.Title))}"
            : null;
        var needsYou = part.SessionIds.Any(id => rows.TryGetValue(id, out var row) && _needsYou.Contains(row.Id));
        var setStatus = ReactiveCommand.CreateFromTask<string>(async wire => {
            if (!await ChangeAsync(ct => _api.SetStatusAsync(part.WorkItemId, wire.Length == 0 ? null : wire, ct), "Couldn't set that status")) return;
            if (wire.Length == 0) _setStatuses.Remove(part.WorkItemId);
            else _setStatuses[part.WorkItemId] = wire;
            Rebuild();
        });
        var id = part.WorkItemId;
        return new WorkItemPagePart(id, part.Title, status, tip, needsYou, LinksOf(part.Links),
            [.. part.SessionIds.Select(sessionId => SessionOf(sessionId, rows, part.Sessions))], _openedParts.Contains(id),
            expanded => { if (expanded) _openedParts.Add(id); else _openedParts.Remove(id); }, setStatus);
    }

    static IReadOnlyList<WorkItemPageLink> LinksOf(IEnumerable<WorkItemLinkDto> links) =>
        [.. links.Where(link => link.LinkClass != "reference")
            .Select(link => new WorkItemPageLink(link.ShortKey, link.Title, link.State, link.Url, link.Kind == "pr"))];

    WorkItemPageSession SessionOf(string sessionId, Dictionary<string, AgentRow> rows, IReadOnlyList<WorkItemPartSessionDto> named) {
        if (!rows.TryGetValue(sessionId, out var row)) {
            if (named.FirstOrDefault(session => session.SessionId == sessionId) is not { } known)
                return new(sessionId, $"Session {sessionId[..Math.Min(8, sessionId.Length)]}", null);
            return new(sessionId, known.Title is { Length: > 0 } knownTitle ? knownTitle : "Untitled session",
                known.LastActivityAt is { } moved ? Ago(moved) : null);
        }
        var stamp = row.LastActivityAt ?? row.CreatedAt;
        var age = Ago(new DateTimeOffset(stamp.Kind == DateTimeKind.Local ? stamp.ToUniversalTime() : DateTime.SpecifyKind(stamp, DateTimeKind.Utc)));
        return new(sessionId, row.Title is { Length: > 0 } title ? title : "Untitled session",
            row.RepoGroupLabel is { Length: > 0 } repo ? $"{repo} · {age}" : age);
    }

    string Ago(DateTimeOffset at) => RelativeTime.Ago(at, _time.GetUtcNow(), _time.LocalTimeZone);

    public void Dispose() {
        ClosePage();
        _disposables.Dispose();
    }
}
