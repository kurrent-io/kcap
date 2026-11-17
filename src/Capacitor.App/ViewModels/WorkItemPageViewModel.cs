using System.Collections.Frozen;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// A work item's page in the main area: its goal, links, progress and parts grouped by status. Statuses and progress
/// are the server's, the same for every viewer; sessions and "Needs you" cover only what this viewer can see.
public sealed class WorkItemPageViewModel : ReactiveObject, IDisposable {
    public const string NoLinks = "No links. Status set by hand";
    const string OnWorkItemKey = "on_work_item";

    readonly IWorkItemPageApi _api;
    readonly IAgentDirectory _directory;
    readonly TimeProvider _time;
    readonly ISet<string> _flippedGroups;
    readonly Subject<Unit> _reread = new();
    readonly CompositeDisposable _disposables = new();
    WorkItemPartsDto? _read;
    IReadOnlySet<string> _needsYou = FrozenSet<string>.Empty;

    public string WorkItemId { get; }

    string _title = "";
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }
    string? _goal;
    public string? Goal { get => _goal; private set => this.RaiseAndSetIfChanged(ref _goal, value); }
    IReadOnlyList<WorkItemPageLink> _links = [];
    public IReadOnlyList<WorkItemPageLink> Links { get => _links; private set => this.RaiseAndSetIfChanged(ref _links, value); }
    /// One status per part, done first, for the progress bar's segments.
    IReadOnlyList<WorkStatus> _progress = [];
    public IReadOnlyList<WorkStatus> Progress { get => _progress; private set => this.RaiseAndSetIfChanged(ref _progress, value); }
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

    public ReactiveCommand<Unit, Unit> OpenInWebCommand { get; }
    public ReactiveCommand<WorkItemPageLink, Unit> OpenLinkCommand { get; }
    public ReactiveCommand<WorkItemPageSession, Unit> OpenSessionCommand { get; }
    public ReactiveCommand<WorkItemPageLooseEnd, Unit> MakePartCommand { get; }
    public ReactiveCommand<Unit, Unit> AddPartCommand { get; }
    public ReactiveCommand<Unit, Unit> AddLinkCommand { get; }

    /// <param name="flippedGroups">The groups the viewer opened or closed against their default, kept for the
    /// life of the app.</param>
    /// <param name="needsYou">The agents waiting on the viewer, such as for a permission.</param>
    public WorkItemPageViewModel(string workItemId, IWorkItemPageApi api, IAgentDirectory directory, TimeProvider time,
            ISet<string> flippedGroups, IObservable<IReadOnlySet<string>>? needsYou = null, IObservable<Unit>? changed = null,
            Action<string>? openSession = null, Action<string>? openUrl = null, Action? openInWeb = null) {
        WorkItemId = workItemId;
        _api = api;
        _directory = directory;
        _time = time;
        _flippedGroups = flippedGroups;
        _disposables.Add(_reread);

        OpenInWebCommand = ReactiveCommand.Create(() => openInWeb?.Invoke(), Observable.Return(openInWeb is not null)).DisposeWith(_disposables);
        OpenLinkCommand = ReactiveCommand.Create<WorkItemPageLink>(link => { if (link.Url is { } url) openUrl?.Invoke(url); }).DisposeWith(_disposables);
        OpenSessionCommand = ReactiveCommand.Create<WorkItemPageSession>(session => openSession?.Invoke(session.SessionId)).DisposeWith(_disposables);
        MakePartCommand = ReactiveCommand.CreateFromTask<WorkItemPageLooseEnd>(looseEnd =>
            ChangeAsync(ct => _api.MakePartAsync(WorkItemId, looseEnd.LooseEndId, ct), "Couldn't make a part from that loose end")).DisposeWith(_disposables);
        AddPartCommand = ReactiveCommand.CreateFromTask(async () => {
            if (await ChangeAsync(ct => _api.AddPartAsync(WorkItemId, NewPartTitle.Trim(), ct), "Couldn't add that part")) NewPartTitle = "";
        }, this.WhenAnyValue(x => x.NewPartTitle, title => !string.IsNullOrWhiteSpace(title))).DisposeWith(_disposables);
        AddLinkCommand = ReactiveCommand.CreateFromTask(async () => {
            if (await ChangeAsync(ct => _api.AddLinkAsync(WorkItemId, NewLinkReference.Trim(), ct), "Couldn't add that link")) NewLinkReference = "";
        }, this.WhenAnyValue(x => x.NewLinkReference, reference => !string.IsNullOrWhiteSpace(reference))).DisposeWith(_disposables);

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

        var parts = read.Parts.Select(part => PartOf(part, rows)).ToList();
        ProgressText = parts.Count == 0 ? "" : $"{parts.Count(part => part.Status == WorkStatus.Done)} of {parts.Count} parts done";
        Progress = [.. parts.Select(part => part.Status).OrderBy(status => status == WorkStatus.Done ? -1 : (int)status)];

        var groups = new List<WorkItemPageGroupViewModel>();
        foreach (var status in Enum.GetValues<WorkStatus>()) {
            var inGroup = parts.Where(part => part.Status == status).ToList();
            if (inGroup.Count > 0) groups.Add(Group(status.Wire(), status, status.Label(), inGroup, [], [], status != WorkStatus.Done));
        }
        var sessions = read.SessionIds.Select(id => SessionOf(id, rows)).ToList();
        var looseEnds = read.LooseEnds.Select(looseEnd => new WorkItemPageLooseEnd(looseEnd.LooseEndId, looseEnd.Text)).ToList();
        if (sessions.Count + looseEnds.Count > 0) groups.Add(Group(OnWorkItemKey, null, "On the work item", [], sessions, looseEnds, true));
        Groups = groups;
    }

    WorkItemPageGroupViewModel Group(string key, WorkStatus? status, string label, IReadOnlyList<WorkItemPagePart> parts,
            IReadOnlyList<WorkItemPageSession> sessions, IReadOnlyList<WorkItemPageLooseEnd> looseEnds, bool expandedByDefault) =>
        new(key, status, label, parts, sessions, looseEnds, expandedByDefault ^ _flippedGroups.Contains(key), expanded => {
            if (expanded == expandedByDefault) _flippedGroups.Remove(key);
            else _flippedGroups.Add(key);
        });

    WorkItemPagePart PartOf(WorkItemPartDetailDto part, Dictionary<string, AgentRow> rows) {
        var status = WorkStatusExtensions.ParseWorkStatus(part.Status);
        var waiting = status == WorkStatus.Blocked && part.WaitsOn.Count > 0;
        var links = LinksOf(part.Links);
        var detail = waiting ? $"Waits on {string.Join(", ", part.WaitsOn.Select(other => other.Title))}"
            : links.Count > 0 ? string.Join(" · ", links.Select(link => link.Label))
            : NoLinks;
        var needsYou = part.SessionIds.Any(id => rows.TryGetValue(id, out var row) && _needsYou.Contains(row.Id));
        var setStatus = ReactiveCommand.CreateFromTask<string>(wire =>
            ChangeAsync(ct => _api.SetStatusAsync(part.WorkItemId, wire.Length == 0 ? null : wire, ct), "Couldn't set that status"));
        return new WorkItemPagePart(part.WorkItemId, part.Title, status, detail, waiting, needsYou,
            [.. part.SessionIds.Select(id => SessionOf(id, rows))], setStatus);
    }

    static IReadOnlyList<WorkItemPageLink> LinksOf(IEnumerable<WorkItemLinkDto> links) =>
        [.. links.Where(link => link.LinkClass != "reference")
            .Select(link => new WorkItemPageLink(link.State is { Length: > 0 } state ? $"{link.ShortKey} {state}" : link.ShortKey, link.Url, link.Title))];

    WorkItemPageSession SessionOf(string sessionId, Dictionary<string, AgentRow> rows) {
        if (!rows.TryGetValue(sessionId, out var row)) return new(sessionId, $"Session {sessionId[..Math.Min(8, sessionId.Length)]}", null);
        var stamp = row.LastActivityAt ?? row.CreatedAt;
        var at = new DateTimeOffset(stamp.Kind == DateTimeKind.Local ? stamp.ToUniversalTime() : DateTime.SpecifyKind(stamp, DateTimeKind.Utc));
        var age = RelativeTime.Ago(at, _time.GetUtcNow(), _time.LocalTimeZone);
        return new(sessionId, row.Title is { Length: > 0 } title ? title : "Untitled session",
            row.RepoGroupLabel is { Length: > 0 } repo ? $"{repo} · {age}" : age);
    }

    public void Dispose() => _disposables.Dispose();
}
