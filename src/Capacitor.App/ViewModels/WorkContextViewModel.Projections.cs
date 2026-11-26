using System.Globalization;
using System.Reactive;
using Avalonia.Collections;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.WorkItems;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The server-derived half of the pane: the card the last read left, synced into the bound lists
/// and properties, and the session summary's link cards.
public sealed partial class WorkContextViewModel {
    WorkContextCard _card = WorkContextCard.Empty(WorkContextPhase.WaitingForSession);
    readonly AvaloniaList<WorkContextPartViewModel> _parts = new();
    readonly AvaloniaList<WorkContextPartViewModel> _thisSessionParts = new();
    readonly AvaloniaList<string> _blockedBy = new();
    readonly AvaloniaList<WorkContextLinkViewModel> _links = new();
    readonly AvaloniaList<WorkContextLinkViewModel> _issues = new();
    readonly List<WorkContextLinkViewModel> _summaryPrs = [];
    readonly AvaloniaList<WorkContextPersonViewModel> _contributors = new();
    string? PrimaryId => _card.PrimaryId;

    public IAvaloniaReadOnlyList<WorkContextPartViewModel> Parts => _parts;
    /// What the collapsed section lists: the parts this session is attached to, in part order.
    public IAvaloniaReadOnlyList<WorkContextPartViewModel> ThisSessionParts => _thisSessionParts;
    /// Open, this session, done: one entry per mark some part carries, so the numbers add up to the list.
    public IReadOnlyList<WorkContextPartCount> PartCounts { get; private set; } = [];
    public IAvaloniaReadOnlyList<string> BlockedBy => _blockedBy;
    public IAvaloniaReadOnlyList<WorkContextLinkViewModel> Links => _links;
    public IAvaloniaReadOnlyList<WorkContextLinkViewModel> Issues => _issues;
    public IAvaloniaReadOnlyList<WorkContextPersonViewModel> Contributors => _contributors;

    public string? Key => _card.Key;
    public string Title => _card.Title;
    public string DisplayTitle => _card.DisplayTitle;
    /// The primary issue folded into the work-item title, so the section leaves it out.
    public bool HasInlineIssue => _card.SeparateIssues.Count < _card.Issues.Count;
    /// The card's separate issues as rows; the bound list itself unless one folded into the title.
    public IEnumerable<WorkContextLinkViewModel> SeparateIssues {
        get {
            var separate = _card.SeparateIssues;
            return separate.Count < _card.Issues.Count
                ? _issues.Where(issue => separate.Any(s => s.Identity == issue.Identity))
                : _issues;
        }
    }
    /// The issue the section shows when there is only one separate entry — not the raw first
    /// server seed, which may be absorbed into the work-item title.
    public WorkContextLinkViewModel? SeparateIssue => SeparateIssues.FirstOrDefault();
    public bool HasSeparateIssue => SeparateIssue is not null;
    public bool HasMultipleIssues => SeparateIssues.Skip(1).Any();
    /// Issue titles wrap over several lines, so two already make a tall section.
    internal const int VisibleIssuesCap = 2;
    public bool IssuesOverflows => SeparateIssues.Skip(VisibleIssuesCap).Any();
    public IEnumerable<WorkContextLinkViewModel> VisibleSeparateIssues =>
        IssuesExpanded || !IssuesOverflows ? SeparateIssues : SeparateIssues.Take(VisibleIssuesCap);
    public string IssueSectionEyebrow => HasMultipleIssues ? "ISSUES" : "ISSUE";
    public string IssueSectionMeta => HasMultipleIssues
        ? SeparateIssues.Count().ToString(CultureInfo.InvariantCulture)
        : SeparateIssue?.Key ?? "";

    public KcapTip? WorkItemTip {
        get {
            var title = FirstNonBlank(DisplayTitle, Key);
            if (title is null) return null;
            var state = StateLabel?.ToLowerInvariant();
            var status = string.IsNullOrEmpty(state) ? null : char.ToUpperInvariant(state[0]) + state[1..];
            return new(title, status, IsClosed ? "danger" : state == "in flight" ? "active" : null,
                KcapTipDetail.Present(string.Equals(title, Key, StringComparison.OrdinalIgnoreCase) ? null : KcapTipDetail.Note(Key)));
        }
    }

    public KcapTip? IssueHeaderTip => HasMultipleIssues
        ? new($"{IssueSectionMeta} linked issues")
        : SeparateIssue?.LinkTip;

    void NotifyIdentity() {
        this.RaisePropertyChanged(nameof(DisplayTitle));
        this.RaisePropertyChanged(nameof(WorkItemTip));
        this.RaisePropertyChanged(nameof(HasInlineIssue));
        this.RaisePropertyChanged(nameof(HasSeparateIssue));
        this.RaisePropertyChanged(nameof(SeparateIssue));
        this.RaisePropertyChanged(nameof(HasMultipleIssues));
        this.RaisePropertyChanged(nameof(IssuesOverflows));
        this.RaisePropertyChanged(nameof(VisibleSeparateIssues));
        this.RaisePropertyChanged(nameof(IssueSectionEyebrow));
        this.RaisePropertyChanged(nameof(IssueSectionMeta));
        this.RaisePropertyChanged(nameof(IssueHeaderTip));
        this.RaisePropertyChanged(nameof(SeparateIssues));
        RaiseRelated();
    }
    public string? Overview => _card.Overview;
    /// The server may add kinds; an unknown one is shown as sent, in the in-flight look.
    public string? StateLabel => _card.StateKind?.Replace('_', ' ').Trim().ToUpperInvariant() is { Length: > 0 } text ? text : null;
    public bool IsShipped => _card.StateKind == "shipped";
    public bool IsClosed => _card.StateKind == "closed";
    public string? PartOfTitle => _card.PartOfTitle;
    public string? CycleNote => _card.Cycle switch {
        "cyclic"        => "Dependencies form a cycle",
        "indeterminate" => "Dependencies could not be fully resolved",
        _               => null,
    };

    public WorkContextLinkViewModel? Issue {
        get;
        private set {
            if (ReferenceEquals(field, value)) return;
            this.RaiseAndSetIfChanged(ref field, value);
            NotifyIdentity();
        }
    }

    public string PartsHeader => _parts.Count switch {
        0     => "0",
        var n => $"{SettledCount} of {n}",
    };
    int SettledCount => _parts.Count(p => p.IsSettled);
    public bool HasParts => _parts.Count > 0;
    public bool HasThisSessionParts => _thisSessionParts.Count > 0;
    public bool HasBlockers => _blockedBy.Count > 0;
    public bool HasTopologyNotes => HasBlockers || !string.IsNullOrEmpty(CycleNote);
    public bool HasContributors => _contributors.Count > 0;
    public bool ShowsPeople => HasAgent && (_card.Viewer is not { } viewer
        || (_card.Contributors.Count > 0
            ? _card.Contributors.Any(c => !viewer.Owns(c.UserId))
            : _requesterId is not null && !viewer.Owns(_requesterId)));
    /// The card carries the PR picker and the live checks and review rows, so it stays while it
    /// has a PR to select or something to say, including the empty-list message during discovery
    /// and polling. The pane's own empty copy is a fallback when there is no card content.
    /// Before a session id the card could only repeat the pane's waiting note.
    public bool ShowsPullRequestCard =>
        Phase != WorkContextPhase.WaitingForSession
        && PullRequests is { } prs && (prs.HasChoice || prs.HasNotice || prs.HasReaderNote);
    public bool ShowsLegacyLinkCards => ShowsLegacyLinks && _links.Count > 0;
    /// Empty copy waits until the session list and the work-item read have both settled — an
    /// earlier miss is often the item's link, not a missing PR.
    public bool ShowsPullRequestEmpty =>
        Phase is WorkContextPhase.Ready or WorkContextPhase.NoWorkItem
        && (PullRequests?.HasListed ?? true)
        && PullRequests is not { HasPullRequest: true }
        && _links.Count == 0
        && !ShowsPullRequestCard;
    public const string PullRequestEmptyNote = "No pull request linked";
    public bool ShowsPullRequestSection => ShowsPullRequestCard || ShowsLegacyLinkCards || ShowsPullRequestEmpty;
    public string WhoCountText => _card.SessionCount switch {
        0     => "",
        1     => "1 session",
        var n => $"{n} sessions",
    };

    public string Requester { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "You";
    public string RequesterRole { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string RequesterInitial { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "Y";
    string? _requesterId;
    public ViewerIdentityDto? ViewerIdentity => _card.Viewer;
    string? _requesterAvatarUrl;
    public string? RequesterAvatarUrl { get => _requesterAvatarUrl; private set => this.RaiseAndSetIfChanged(ref _requesterAvatarUrl, value); }

    public bool PartsExpanded {
        get;
        private set {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(PartsAccessibleName));
        }
    }
    /// The folded counts are told apart only by their marks, so the header names them in words.
    public string PartsAccessibleName => PartsExpanded || PartCounts.Count == 0
        ? $"Parts, {PartsHeader}"
        : $"Parts, {string.Join(", ", PartCounts.Select(c => c.Label))}";
    public bool PeopleExpanded { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool IssuesExpanded {
        get;
        private set {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(VisibleSeparateIssues));
            this.RaisePropertyChanged(nameof(IssueHeaderTip));
        }
    }
    readonly WorkContextLayout _layout;
    // Usage, checkout, session, topics and bookmarks folds are shared by every session's pane, so switching sessions
    // keeps what the reader opened.
    public bool UsageExpanded {
        get => _layout.UsageExpanded;
        private set => _layout.UsageExpanded = value;
    }
    public bool CheckoutExpanded {
        get => _layout.CheckoutExpanded;
        private set => _layout.CheckoutExpanded = value;
    }
    public bool SessionExpanded {
        get => _layout.SessionExpanded;
        private set => _layout.SessionExpanded = value;
    }
    public bool OutlineExpanded {
        get => _layout.OutlineExpanded;
        private set => _layout.OutlineExpanded = value;
    }

    /// The chat's topics, listed with its bookmarks under the session's facts.
    public ChatTopics? Topics { get; set => this.RaiseAndSetIfChanged(ref field, value); }

    /// The chat's bookmarks, which also lay out the topics and bookmarks section.
    public ChatBookmarks? Bookmarks { get; set => this.RaiseAndSetIfChanged(ref field, value); }

    /// A header button has no expand state of its own to announce, so its name carries it.
    public string UsageToggleName => FoldName("Usage", UsageExpanded);
    public string CheckoutToggleName => FoldName("Checkout", CheckoutExpanded);
    public string SessionToggleName => FoldName("Session", SessionExpanded);
    public string OutlineToggleName => FoldName("Topics and bookmarks", OutlineExpanded);
    static string FoldName(string section, bool expanded) => $"{section}, {(expanded ? "expanded" : "collapsed")}";

    void OnLayoutChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) {
        switch (e.PropertyName) {
            case nameof(WorkContextLayout.UsageExpanded):
                this.RaisePropertyChanged(nameof(UsageExpanded));
                this.RaisePropertyChanged(nameof(UsageToggleName));
                break;
            case nameof(WorkContextLayout.CheckoutExpanded):
                this.RaisePropertyChanged(nameof(CheckoutExpanded));
                this.RaisePropertyChanged(nameof(CheckoutToggleName));
                break;
            case nameof(WorkContextLayout.SessionExpanded):
                this.RaisePropertyChanged(nameof(SessionExpanded));
                this.RaisePropertyChanged(nameof(SessionToggleName));
                break;
            case nameof(WorkContextLayout.OutlineExpanded):
                this.RaisePropertyChanged(nameof(OutlineExpanded));
                this.RaisePropertyChanged(nameof(OutlineToggleName));
                break;
        }
    }

    public bool SubagentsExpanded { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    public ReactiveCommand<Unit, Unit> TogglePartsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> TogglePeopleCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleIssuesCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleUsageCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleCheckoutCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleSessionCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleOutlineCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleSubagentsCommand { get; private set; } = null!;

    void InitializeProjections() {
        TogglePartsCommand   = Toggle(() => PartsExpanded = !PartsExpanded);
        TogglePeopleCommand  = Toggle(() => PeopleExpanded = !PeopleExpanded);
        ToggleIssuesCommand  = Toggle(() => {
            if (IssuesOverflows) IssuesExpanded = !IssuesExpanded;
            else if (!HasMultipleIssues && SeparateIssue is { CanOpen: true } issue)
                LinkPolicy.Open(_opener, issue.Url);
        });
        ToggleUsageCommand    = Toggle(() => UsageExpanded = !UsageExpanded);
        ToggleCheckoutCommand = Toggle(() => CheckoutExpanded = !CheckoutExpanded);
        ToggleSessionCommand  = Toggle(() => SessionExpanded = !SessionExpanded);
        ToggleOutlineCommand  = Toggle(() => OutlineExpanded = !OutlineExpanded);
        _layout.PropertyChanged += OnLayoutChanged;
        _disposables.Add(System.Reactive.Disposables.Disposable.Create(() => _layout.PropertyChanged -= OnLayoutChanged));
        ToggleSubagentsCommand = Toggle(() => SubagentsExpanded = !SubagentsExpanded);
    }

    ReactiveCommand<Unit, Unit> Toggle(Action flip) {
        var command = ReactiveCommand.Create(flip);
        _disposables.Add(command);
        return command;
    }

    void UpdateRequester(AgentStatusDto dto, string vendorLabel) {
        Requester = FirstNonBlank(dto.RequesterDisplay, dto.Requester) ?? "You";
        RequesterRole = $"This session · {vendorLabel}";
        RequesterInitial = WorkContextPersonViewModel.InitialOf(Requester);
        _requesterId = dto.Requester;
        ResolveRequesterAvatar();
        this.RaisePropertyChanged(nameof(ShowsPeople));
    }

    /// A launch with no requester is the daemon owner's, and the viewer only sees their own daemons.
    /// Anyone else gets a picture only from a `github:` id.
    void ResolveRequesterAvatar() =>
        RequesterAvatarUrl = _card.Viewer is { AvatarUrl: { } own } viewer && (_requesterId is null || viewer.Owns(_requesterId))
            ? own
            : GitHubAvatar.UrlFor(_requesterId);

    WorkContextRequester? SessionRequester() => _dto is null ? null : new(_dto.Requester, _dto.RequesterDisplay);

    void ForgetViewer() => ShowCard(WorkContextCards.ForgetViewer(_card, SessionRequester()));

    static string? FirstNonBlank(params string?[] values) {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return null;
    }

    /// A session change keeps who the viewer is: only a read, a reconnect or a sign-in replaces it.
    void ClearServerProjections(WorkContextPhase phase) {
        ShowCard(WorkContextCard.Empty(phase) with { Viewer = _card.Viewer });
        ClearLinks();
    }

    void ClearLinks() {
        _summaryPrs.Clear();
        _links.Clear();
        RaiseRelated();
    }

    /// `itemServed`: the read behind the card carried the work item.
    void ShowCard(WorkContextCard card, bool itemServed = false) {
        var was = _card;
        _card = card;
        if (!ReferenceEquals(was.Parts, card.Parts))
            Replace(_parts, [.. card.Parts.Select(p => new WorkContextPartViewModel(p.Title, p.Mark))], p => (p.Title, p.Mark));
        if (!ReferenceEquals(was.Issues, card.Issues)) {
            Replace(_issues, [.. card.Issues.Select(i => new WorkContextLinkViewModel("ISSUE", i.Key, i.Title, i.Url, _opener, identity: i.Identity, state: i.State))],
                i => (i.Key, i.Title, i.Url, i.Identity));
            // A kept row still takes the issue's new state.
            for (var i = 0; i < card.Issues.Count; i++) _issues[i].State = card.Issues[i].State;
        }
        if (!ReferenceEquals(was.Contributors, card.Contributors)) {
            var now = _time.GetUtcNow();
            Replace(_contributors, [.. card.Contributors.Select(c => new WorkContextPersonViewModel(c.Name, c.AvatarUrl, c.LastActivityAt, now, c.UserId))],
                c => (c.UserId, c.Name, c.AvatarUrl, c.LastActivityText));
        }
        if (!ReferenceEquals(was.BlockedBy, card.BlockedBy)) Replace(_blockedBy, [.. card.BlockedBy], b => b);

        if (!string.Equals(was.PrimaryId, card.PrimaryId, StringComparison.Ordinal)) {
            _canOpenWorkItem.OnNext(WorkContextIds.ValidWorkItemId(card.PrimaryId) is not null && _openWorkItem is not null);
            // A new primary without its item drops the old item, and the issue fold with it.
            if (card.PrimaryId is null || !itemServed) IssuesExpanded = false;
        }
        if (was.Phase != card.Phase) {
            this.RaisePropertyChanged(nameof(Phase));
            this.RaisePropertyChanged(nameof(PhaseNote));
            this.RaisePropertyChanged(nameof(IsReady));
            this.RaisePropertyChanged(nameof(ShowsSignIn));
            this.RaisePropertyChanged(nameof(ShowsRetry));
            this.RaisePropertyChanged(nameof(ShowsReadProgress));
        }
        if (was.IsStale != card.IsStale) this.RaisePropertyChanged(nameof(IsStale));
        if (!Equals(was.Viewer, card.Viewer)) this.RaisePropertyChanged(nameof(ViewerIdentity));
        if (was.Key != card.Key) this.RaisePropertyChanged(nameof(Key));
        if (was.Title != card.Title) this.RaisePropertyChanged(nameof(Title));
        if (was.Overview != card.Overview) this.RaisePropertyChanged(nameof(Overview));
        if (was.StateKind != card.StateKind) {
            this.RaisePropertyChanged(nameof(StateLabel));
            this.RaisePropertyChanged(nameof(IsShipped));
            this.RaisePropertyChanged(nameof(IsClosed));
        }
        if (was.PartOfTitle != card.PartOfTitle) this.RaisePropertyChanged(nameof(PartOfTitle));
        if (was.Cycle != card.Cycle) this.RaisePropertyChanged(nameof(CycleNote));
        if (was.SessionCount != card.SessionCount) this.RaisePropertyChanged(nameof(WhoCountText));
        Issue = _issues.FirstOrDefault();
        ResolveRequesterAvatar();
        NotifyIdentity();
        RaiseCardCounts();
    }

    void RaiseCardCounts() {
        SyncPartsFold();
        this.RaisePropertyChanged(nameof(PartsHeader));
        this.RaisePropertyChanged(nameof(PartsAccessibleName));
        this.RaisePropertyChanged(nameof(HasParts));
        this.RaisePropertyChanged(nameof(HasThisSessionParts));
        this.RaisePropertyChanged(nameof(HasBlockers));
        this.RaisePropertyChanged(nameof(HasTopologyNotes));
        this.RaisePropertyChanged(nameof(HasContributors));
        this.RaisePropertyChanged(nameof(ShowsPeople));
    }

    void SyncPartsFold() {
        StableRows.Sync(_thisSessionParts, _parts.Where(p => p.IsThisSession));
        WorkContextPartCount[] counts = [
            new(WorkContextPartMark.Unknown, _parts.Count(p => p.Mark == WorkContextPartMark.Unknown)),
            new(WorkContextPartMark.ThisSession, _thisSessionParts.Count),
            new(WorkContextPartMark.Settled, SettledCount),
        ];
        var present = counts.Where(c => c.Count > 0).ToArray();
        if (present.SequenceEqual(PartCounts)) return;
        PartCounts = present;
        this.RaisePropertyChanged(nameof(PartCounts));
    }

    void RaiseRelated() {
        this.RaisePropertyChanged(nameof(ShowsPullRequestCard));
        this.RaisePropertyChanged(nameof(ShowsLegacyLinkCards));
        this.RaisePropertyChanged(nameof(ShowsPullRequestEmpty));
        this.RaisePropertyChanged(nameof(ShowsPullRequestSection));
    }

    bool _hasSessionFacts;

    void ApplySessionFacts(SessionSummaryDto? summary) {
        _hasSessionFacts = summary is not null;
        Tokens = SessionFactText.Tokens(summary?.Stats);
        Cost = SessionFactText.Cost(summary?.Stats);
        ContextUse = SessionFactText.Context(summary?.Stats);
        _stats = summary?.Stats;
        CurrentContextUse = SessionFactText.CurrentContext(_stats);
        if (!(ContextPercent >= CompactThreshold) || _stats?.AwaitingContextReading == true) EndCompact();
        RaiseCompactState();
        Created = SessionFactText.Timestamp(summary?.StartedAt, _time.LocalTimeZone);
        Updated = SessionFactText.Timestamp(summary?.LastEventAt, _time.LocalTimeZone);
        SessionSharing = summary is null ? SessionFactText.Unknown : SessionFactText.SessionSharing(summary);
        AgentSharing = SessionFactText.AgentSharing(summary?.AgentVisibility);
        _storedBranch = FirstNonBlank(summary?.RepoBranch, summary?.Repositories.FirstOrDefault(r => r.IsPrimary)?.Branch);
        _storedRepository = FirstNonBlank(summary?.Repositories.FirstOrDefault(r => r.IsPrimary)?.RepoName, summary?.RepoName);
        if (_isRemote) {
            ResolveBranch();
            if (_dto is not null) ResolveRepository(_dto.RepoPath);
        }
        this.RaisePropertyChanged(nameof(PhaseNote));
    }

    void ApplyLinks(WorkContextRead read) {
        if (read is { SummaryFailed: false, Summary: { } summary }) {
            var primaryRepositories = summary.Repositories.Where(repository => repository.IsPrimary).ToArray();
            if (primaryRepositories.Length == 1) {
                var repo = primaryRepositories[0];
                // The host is inferred from a linked pull request sharing the repository hash, else github.com is assumed.
                var (provider, host) = RepositoryIdentity(summary, repo.RepoHash);
                PrimaryRepository = new(provider, host, repo.Owner, repo.RepoName, repo.RepoHash);
            } else PrimaryRepository = null;

            if (ShowsLegacyLinks) {
                var cards = summary.PullRequests
                    .Select(pr => Link(pr.Number, pr.Title, pr.Url))
                    .ToList();
                if (summary.PrNumber is { } number && !summary.PullRequests.Any(pr => SamePullRequest(pr, summary, number)))
                    cards.Add(Link(number, summary.PrTitle, summary.PrUrl));
                _summaryPrs.Clear();
                _summaryPrs.AddRange(cards);
            } else _summaryPrs.Clear();
        }

        RebuildLinks();
    }

    /// Link cards are the legacy stand-in for the card: the session summary's PRs, never the work
    /// item's.
    void RebuildLinks() {
        Replace(_links, ShowsLegacyLinks ? [.. _summaryPrs] : [], l => (l.Key, l.Title, l.Url));
        RaiseRelated();
    }

    /// A poll that returns the same rows leaves the bound list alone, so the ItemsControl keeps its
    /// containers instead of rebuilding them every 30 seconds.
    static void Replace<T, TKey>(AvaloniaList<T> target, List<T> incoming, Func<T, TKey> key) {
        if (target.Select(key).SequenceEqual(incoming.Select(key))) return;
        target.Clear();
        target.AddRange(incoming);
    }

    WorkContextLinkViewModel Link(int number, string? title, string? url) =>
        new("PULL REQUEST", $"#{number}", title ?? "", url, _opener);

    static (string Provider, string Host) RepositoryIdentity(SessionSummaryDto summary, string repoHash) {
        var link = summary.PullRequests.FirstOrDefault(pr => pr.RepoHash == repoHash && PullRequestWire.SafeLink(pr.Url) is not null);
        if (link is null) return ("github", "github.com");
        var uri = new Uri(PullRequestWire.SafeLink(link.Url)!);
        return (ProviderOf(uri), uri.IdnHost);
    }

    static string ProviderOf(Uri uri) =>
        uri.AbsolutePath.Contains("/-/merge_requests/", StringComparison.Ordinal) ? "gitlab" : "github";

    /// PR numbers are repository-local; without a repository identity on the summary the number
    /// alone decides, which never shows one PR twice.
    internal static bool SamePullRequest(SessionPullRequestDto pr, SessionSummaryDto summary, int number) {
        if (pr.Number != number) return false;
        if (string.IsNullOrEmpty(summary.RepoOwner) || string.IsNullOrEmpty(summary.RepoName)) return true;

        return string.Equals(pr.Owner, summary.RepoOwner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pr.RepoName, summary.RepoName, StringComparison.OrdinalIgnoreCase);
    }
}
