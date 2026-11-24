using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class UnifiedPullRequestGroup : ReactiveObject {
    readonly ObservableCollection<UnifiedPullRequestItem> _rows = [];
    readonly ObservableCollection<UnifiedPullRequestItem> _passing = [];
    PullRequestSectionState? _state;
    string _note = "";
    string _updated = "";
    bool _canLoad;
    bool _hasMore;
    bool _hasEarlier;
    bool _canPage;
    bool _canFilter;
    bool _loading;
    bool _passingExpanded;
    int _passingCount;

    internal UnifiedPullRequestGroup(UnifiedPullRequestPage page, string section, string title, string? thread = null, string? rootComment = null) {
        Page = page;
        Section = section;
        Title = title;
        Thread = thread;
        RootComment = rootComment;
        FirstCommand = ReactiveCommand.Create(Reveal);
        MoreCommand = ReactiveCommand.Create(() => {
            if (_state?.Next is { } cursor && CanPage) Load(cursor);
        });
        EarlierCommand = ReactiveCommand.Create(() => {
            if (_state?.Evicted is { } cursor && CanPage) Load(cursor, earlier: true);
        });
    }

    public UnifiedPullRequestPage Page { get; }
    public string Section { get; }
    public string Title { get; }
    string? Thread { get; }
    string? RootComment { get; }
    string? Filter => Section == "threads" ? Page.ThreadFilter : null;
    internal string Key => PullRequestContextViewModel.PageKey(Section, Filter, Thread);
    public bool IsThreads => Section == "threads";
    public bool IsReplies => Thread is not null;
    public IReadOnlyList<UnifiedPullRequestItem> Rows => _rows;
    public IReadOnlyList<UnifiedPullRequestItem> PassingRows => _passing;
    public string Note => _note;
    public string Updated => _updated;
    public bool HasMore => _hasMore;
    public bool HasEarlier => _hasEarlier;
    public bool CanPage => _canPage;
    public bool CanFilter => _canFilter;
    public bool IsLoading => _loading;
    public string LoadLabel => Section switch {
        "conversation" => "Load comments",
        "threads" => "Load file discussions",
        "reviews" => "Load submitted reviews",
        _ => "Load " + Title.ToLowerInvariant(),
    };
    public string MoreAccessibleName => "Load more " + Title.ToLowerInvariant();
    public string EarlierAccessibleName => "Reload earlier " + Title.ToLowerInvariant();
    public bool CanLoad => _canLoad;
    public bool HasPassing => _passingCount > 0;
    public string PassingLabel => $"Passing checks ({_passingCount} loaded)";
    public bool PassingExpanded {
        get => _passingExpanded;
        set {
            if (_passingExpanded == value || !Page.ShowContent) return;
            Page.PassingExpanded = value;
            Update();
        }
    }

    public ICommand FirstCommand { get; }
    public ICommand MoreCommand { get; }
    public ICommand EarlierCommand { get; }

    internal void Update() {
        _state = Page.Context.UnifiedSection(Key);
        var pending = Page.ShowContent && Page.Context.UnifiedPagePending(Key);
        var canPage = Page.Context.UnifiedCanRead && !pending;
        var rows = _state?.Pages.SelectMany(page => page.Rows)
            .Where(row => RootComment is null || row.Id != RootComment) ?? [];
        var checks = Section == "checks";
        if (checks) rows = rows.OrderBy(CheckPriority);
        var items = rows.ToArray();
        var passing = checks ? items.Count(row => row.Outcome == "success") : 0;
        var passingExpanded = checks && Page.PassingExpanded;
        Reconcile(_rows, checks ? items.Where(row => row.Outcome != "success") : items);
        Reconcile(_passing, passingExpanded ? items.Where(row => row.Outcome == "success") : []);
        if (_passingCount != passing) {
            _passingCount = passing;
            this.RaisePropertyChanged(nameof(HasPassing));
            this.RaisePropertyChanged(nameof(PassingLabel));
        }
        this.RaiseAndSetIfChanged(ref _passingExpanded, passingExpanded, nameof(PassingExpanded));
        this.RaiseAndSetIfChanged(ref _loading, pending, nameof(IsLoading));
        this.RaiseAndSetIfChanged(ref _canPage, canPage, nameof(CanPage));
        this.RaiseAndSetIfChanged(ref _canFilter, IsThreads && Page.Context.UnifiedCanRead, nameof(CanFilter));
        this.RaiseAndSetIfChanged(ref _hasMore, _state is { Next: not null, Stopped: false }, nameof(HasMore));
        this.RaiseAndSetIfChanged(ref _hasEarlier, _state is { Evicted: not null, Stopped: false }, nameof(HasEarlier));
        this.RaiseAndSetIfChanged(ref _canLoad, canPage && (_state is null || _state is { Stopped: true } || _state is { Pages.Count: 0, Evicted: null }), nameof(CanLoad));
        this.RaiseAndSetIfChanged(ref _note, Status(items.Length), nameof(Note));
        var updated = _state?.Completed is { } at ? "Updated " + at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture) : "";
        if (checks && _state?.Head is { Length: >= 7 } head) updated += $" · Commit {head[..7]}";
        this.RaiseAndSetIfChanged(ref _updated, updated, nameof(Updated));
    }

    string Status(int rows) {
        if (_state is null) return "";
        if (_state.Error is { } error) return error;
        if (_state.StaleReason is { } stale) return stale;
        if (_state.Coverage != "complete") return "Limited snapshot. More may be available on GitHub.";
        if (_state.Pages.Count == 0 || rows != 0 || _state.Next is not null || _state.Evicted is not null) return "";
        return Section switch {
            "checks" => "No checks reported.",
            "reviewers" => "No reviewers yet.",
            "reviews" => "No submitted reviews.",
            "threads" when Page.IncludeResolved => "No file discussions.",
            "threads" when _state.Excluded is { Kind: "exact", Value: > 0 } excluded => $"No unresolved threads ({excluded.Value} resolved).",
            "threads" => "No unresolved threads.",
            "thread_comments" => "No replies.",
            _ => "No general comments.",
        };
    }

    static int CheckPriority(PullRequestRow row) => row.Outcome switch {
        "failure" or "timed_out" or "action_required" => 0,
        "pending" when row.Status?.PreviewGlyph == StatusGlyphKind.Activity => 1,
        "pending" => 2,
        _ => 3,
    };

    void Reconcile(ObservableCollection<UnifiedPullRequestItem> target, IEnumerable<PullRequestRow> rows) {
        var index = 0;
        foreach (var row in rows) {
            if (index >= target.Count) target.Add(new(this, row));
            else if (target[index].Row != row) target[index] = new(this, row);
            target[index++].Update();
        }
        while (target.Count > index) target.RemoveAt(target.Count - 1);
    }

    internal void Reveal() {
        if (!Page.Context.UnifiedCanRead) return;
        Page.Visit(Key);
        if (_state is null) LoadFirst();
        else if (CanLoad) LoadFirst(refresh: true);
    }

    internal void RevealIfUnloaded() {
        if (Page.Context.UnifiedCanRead && _state is null) Reveal();
    }

    internal void LoadFirst(bool refresh = false) => Load(null, refresh);

    void Load(string? cursor, bool refresh = false, bool earlier = false) =>
        Page.Context.LoadUnifiedPage(Section, Filter, Thread, cursor, refresh, earlier);
}
