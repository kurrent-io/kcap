using Capacitor.Cli.Core.PullRequests;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class UnifiedPullRequestPage : ReactiveObject {
    const int PositionLimit = 20;
    const int ExpandedLimit = 12;
    readonly Dictionary<PullRequestSubjectDto, Position> _positions = [];
    readonly UnifiedPullRequestGroup[] _groups;
    Position? _position;
    PullRequestSubjectDto? _subject;
    PullRequestSubjectDto? _shortcutSubject;
    string? _shortcut;
    long _visit;
    bool _showContent;
    bool _cooldown;
    bool _validateCachedAccount;
    (string? Reader, string? Account) _cachedAccount;

    internal UnifiedPullRequestPage(PullRequestContextViewModel context) {
        Context = context;
        Reviewers = new(this, "reviewers", "Reviewers");
        Reviews = new(this, "reviews", "Submitted reviews");
        Threads = new(this, "threads", "File discussions");
        Conversation = new(this, "conversation", "General comments");
        Checks = new(this, "checks", "Checks");
        _groups = [Reviewers, Reviews, Threads, Conversation, Checks];
    }

    public PullRequestContextViewModel Context { get; }
    public UnifiedPullRequestGroup Reviewers { get; }
    public UnifiedPullRequestGroup Reviews { get; }
    public UnifiedPullRequestGroup Threads { get; }
    public UnifiedPullRequestGroup Conversation { get; }
    public UnifiedPullRequestGroup Checks { get; }
    public bool ShowContent => _showContent;
    public bool CoolingDown => _cooldown;

    public string? Description => Context.UnifiedDescription;
    public bool DescriptionTruncated => Context.UnifiedDescriptionTruncated;
    public bool ShowConflictDetails => ShowContent && Context.UnifiedConflictDetails;
    public bool ShowCleanMergeability => ShowContent && !ShowConflictDetails && Context.LifecycleStatus.Kind is "open" or "draft";
    public PullRequestStatus Mergeability => Context.UnifiedConflictStatus;
    public PullRequestStatus ChecksStatus => Context.UnifiedChecksStatus;
    public PullRequestStatus ThreadStatus => Context.UnifiedThreadsStatus;
    public string? StaleNote => Context.HasStaleOverview ? Context.UnifiedStaleReason ?? "Showing an earlier snapshot." : null;
    public bool IncludeResolved {
        get => _position?.IncludeResolved == true;
        set {
            if (_position is not { } position || position.IncludeResolved == value || !Context.UnifiedCanRead) return;
            position.IncludeResolved = value;
            this.RaisePropertyChanged();
            UpdateGroups();
            Threads.Reveal();
        }
    }
    internal string ThreadFilter => IncludeResolved ? "all" : "unresolved";
    internal ReadingAnchor Anchor {
        get => _position?.Anchor ?? new("overview", null, 0);
        set { if (_position is { } position && ShowContent) position.Anchor = value; }
    }
    internal string? PendingJumpFocus { get; private set; }
    internal void AcknowledgeJumpFocus() => PendingJumpFocus = null;
    internal event Action? RestoreAnchor;
    internal event Action<string>? JumpRequested;

    internal void Update() {
        var subject = Context.Selected?.Subject;
        var selectionChanged = _subject != subject || subject is not null && _position is null;
        if (selectionChanged) {
            _subject = subject;
            PendingJumpFocus = null;
            _position = subject is null ? null : PositionFor(subject);
            this.RaisePropertyChanged(nameof(IncludeResolved));
        }
        var showedContent = ShowContent;
        this.RaiseAndSetIfChanged(ref _showContent, Context.UnifiedCanDisplay, nameof(ShowContent));
        this.RaiseAndSetIfChanged(ref _cooldown, Context.UnifiedCoolingDown, nameof(CoolingDown));
        UpdateGroups();
        this.RaisePropertyChanged(nameof(Description));
        this.RaisePropertyChanged(nameof(DescriptionTruncated));
        this.RaisePropertyChanged(nameof(ShowConflictDetails));
        this.RaisePropertyChanged(nameof(ShowCleanMergeability));
        this.RaisePropertyChanged(nameof(Mergeability));
        this.RaisePropertyChanged(nameof(ThreadStatus));
        this.RaisePropertyChanged(nameof(ChecksStatus));
        this.RaisePropertyChanged(nameof(StaleNote));
        if (ShowContent && (selectionChanged || !showedContent)) RestoreAnchor?.Invoke();
        ApplyShortcut();
    }

    void UpdateGroups() {
        foreach (var group in _groups) group.Update();
    }

    internal void LoadVisited(bool refresh = false, bool checksChanged = false, bool renewDiscussions = false) {
        if (!Context.UnifiedCanRead || _position is not { } position) return;
        foreach (var group in _groups) {
            if (position.Visited.Contains(group.Key)) Reload(group);
        }
        foreach (var replies in ExpandedReplies()) Reload(replies);

        void Reload(UnifiedPullRequestGroup group) {
            var state = Context.UnifiedSection(group.Key);
            var singleDiscussionPage = group.Section is "reviews" or "conversation" && state is { Pages.Count: 1, Earlier.Count: 0 };
            var reload = refresh || renewDiscussions && singleDiscussionPage
                || group.Section == "checks" && (checksChanged || state is { StaleReason: not null }
                || state?.Pages.Any(page => page.Rows.Any(row => row.Outcome == "pending")) == true);
            if (state is null || reload || state is { Stopped: false, Error: not null, Pages.Count: 0 })
                group.LoadFirst(refresh: reload || state is not null);
        }
    }

    internal void LoadUnloadedReplies() {
        foreach (var replies in ExpandedReplies()) replies.RevealIfUnloaded();
    }

    UnifiedPullRequestGroup[] ExpandedReplies() =>
        Threads.Rows.Select(item => item.Replies).OfType<UnifiedPullRequestGroup>().ToArray();

    internal void Visit(string key) {
        if (_position is null || key.StartsWith("thread_comments:", StringComparison.Ordinal)) return;
        _position.Visited.Add(key);
    }

    internal void RememberShortcut(string section) {
        _shortcutSubject = Context.Selected?.Subject;
        _shortcut = section;
    }

    internal void ApplyShortcut() {
        if (_shortcut is not { } section || !ShowContent) return;
        _shortcut = null;
        if (_shortcutSubject == _subject) Jump(section);
    }

    internal void Jump(string destination) {
        if (!ShowContent) return;
        Anchor = new(destination, null, 0);
        PendingJumpFocus = destination;
        JumpRequested?.Invoke(destination);
        _groups.FirstOrDefault(group => group.Section == destination)?.Reveal();
    }

    internal bool IsExpanded(string id) => _position?.Expanded.Contains(id) == true;
    internal void ToggleReplies(string id) {
        if (!ShowContent || _position is not { } position) return;
        if (!position.Expanded.Remove(id)) {
            if (!Context.UnifiedCanRead) return;
            if (position.Expanded.Count >= ExpandedLimit) position.Expanded.RemoveAt(0);
            position.Expanded.Add(id);
        }
        Threads.Update();
        Threads.Rows.FirstOrDefault(item => item.Row.Id == id)?.Replies?.Reveal();
    }
    internal bool PassingExpanded {
        get => _position?.PassingExpanded == true;
        set { if (_position is { } position) position.PassingExpanded = value; }
    }

    Position PositionFor(PullRequestSubjectDto subject) {
        if (!_positions.TryGetValue(subject, out var position)) {
            if (_positions.Count >= PositionLimit) _positions.Remove(_positions.MinBy(entry => entry.Value.Touched).Key);
            _positions[subject] = position = new();
        }
        position.Touched = ++_visit;
        return position;
    }

    internal void SavePages(PullRequestSubjectDto subject, Dictionary<string, PullRequestSectionState> pages, (string? Reader, string? Account) account) {
        var position = PositionFor(subject);
        position.Pages = new(pages, StringComparer.Ordinal);
        position.Account = account;
        TrimPages(0);
    }

    internal Dictionary<string, PullRequestSectionState>? TakePages(PullRequestSubjectDto subject) {
        _validateCachedAccount = false;
        _cachedAccount = default;
        if (!_positions.TryGetValue(subject, out var position) || position.Pages is not { } pages) return null;
        position.Pages = null;
        _cachedAccount = position.Account;
        _validateCachedAccount = true;
        return pages;
    }

    internal bool AcceptCachedAccount((string? Reader, string? Account) account) {
        if (!_validateCachedAccount) return true;
        _validateCachedAccount = false;
        return account == _cachedAccount;
    }

    internal void TrimPages(long activeBytes) {
        foreach (var position in _positions.Values.Where(p => p.Pages is not null).OrderBy(p => p.Touched)) {
            var cached = _positions.Values.Sum(p => p.Pages?.Values.Sum(section => section.Bytes) ?? 0);
            if (cached + activeBytes <= PullRequestContextViewModel.PageCacheBudget) break;
            position.Pages = null;
        }
    }

    internal void ReleasePages() {
        foreach (var position in _positions.Values) position.Pages = null;
        Update();
    }

    internal void Reset() {
        _positions.Clear();
        _position = null;
        _subject = _shortcutSubject = null;
        _shortcut = null;
        PendingJumpFocus = null;
        _cachedAccount = default;
        _validateCachedAccount = false;
    }

    internal sealed record ReadingAnchor(string Section, string? Row, double Offset);
    sealed class Position {
        internal bool IncludeResolved;
        internal bool PassingExpanded;
        internal long Touched;
        internal HashSet<string> Visited { get; } = [];
        internal List<string> Expanded { get; } = [];
        internal ReadingAnchor Anchor { get; set; } = new("overview", null, 0);
        internal Dictionary<string, PullRequestSectionState>? Pages;
        internal (string? Reader, string? Account) Account;
    }
}
