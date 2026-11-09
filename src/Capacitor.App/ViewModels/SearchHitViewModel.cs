using System.Globalization;
using Avalonia.Automation;
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class SearchHitViewModel : ReactiveObject {
    bool _isHighlighted;
    bool _isOpening;

    SearchHitViewModel() { }

    public required string SessionId { get; init; }
    public required SearchMode Kind { get; init; }
    public required IReadOnlyList<SnippetRun> Title { get; init; }
    public string TitleText => string.Concat(Title.Select(r => r.Text));
    public string? Speaker { get; init; }
    public IReadOnlyList<SnippetRun> Snippet { get; init; } = [];
    public required string Caption { get; init; }
    public int? PrNumber { get; init; }
    public string? OwnerName { get; init; }
    public string? OwnerInitials { get; init; }
    public string? OwnerAvatarUrl { get; init; }
    /// An avatar with no owner to name stays out of the control view rather than reading as an unnamed element.
    public AccessibilityView OwnerAvatarView => string.IsNullOrEmpty(OwnerName) ? AccessibilityView.Raw : AccessibilityView.Content;
    public required string WhenText { get; init; }
    public required PinPresentation Presentation { get; init; }

    public bool IsHighlighted { get => _isHighlighted; set => this.RaiseAndSetIfChanged(ref _isHighlighted, value); }
    public bool IsOpening { get => _isOpening; set => this.RaiseAndSetIfChanged(ref _isOpening, value); }

    public static SearchHitViewModel ForSession(SessionSearchHitDto hit, DateTimeOffset now) {
        var title = SearchSnippet.Parse(hit.Snippet);
        if (title.Count == 0) title = [new SnippetRun(FirstNonEmpty(hit.Title, hit.Slug) ?? hit.SessionId, false)];
        var hint = hit.MatchedField switch {
            "branch" => "matched on branch",
            "pr" => "matched on PR title",
            "user" => "matched on owner",
            "repo" => "matched on repository",
            "semantic" => "matched by meaning",
            _ => null
        };
        return new SearchHitViewModel {
            SessionId = hit.SessionId,
            Kind = SearchMode.Sessions,
            Title = title,
            Caption = Join(RepoOf(hit.RepoOwner, hit.RepoName), OwnerNameOf(hit.OwnerDisplayName, hit.OwnerUser), hint),
            PrNumber = hit.PrNumber,
            OwnerName = OwnerNameOf(hit.OwnerDisplayName, hit.OwnerUser),
            OwnerInitials = RailSessionViewModel.Initials(OwnerNameOf(hit.OwnerDisplayName, hit.OwnerUser)),
            OwnerAvatarUrl = hit.OwnerAvatarUrl,
            WhenText = hit.EndedAt is null ? "Live" : WhenOf(hit.StartedAt, null, now),
            Presentation = new PinPresentation(hit.OwnerUser, hit.OwnerDisplayName, hit.OwnerAvatarUrl)
        };
    }

    public static SearchHitViewModel ForTranscript(TranscriptSearchHitDto hit, DateTimeOffset now) {
        var speaker = hit.Kind == "turn_prose" ? "summary" : hit.Speaker;
        return new SearchHitViewModel {
            SessionId = hit.SessionId,
            Kind = SearchMode.Transcripts,
            Title = [new SnippetRun(FirstNonEmpty(hit.Title, hit.Slug) ?? hit.SessionId, false)],
            Speaker = speaker,
            Snippet = SearchSnippet.Parse(hit.Snippet),
            Caption = Join(RepoOf(hit.RepoOwner, hit.RepoName), OwnerNameOf(hit.OwnerDisplayName, hit.OwnerUser)),
            OwnerName = OwnerNameOf(hit.OwnerDisplayName, hit.OwnerUser),
            OwnerInitials = RailSessionViewModel.Initials(OwnerNameOf(hit.OwnerDisplayName, hit.OwnerUser)),
            OwnerAvatarUrl = hit.OwnerAvatarUrl,
            WhenText = WhenOf(hit.Timestamp, hit.StartedAt, now),
            Presentation = new PinPresentation(hit.OwnerUser, hit.OwnerDisplayName, hit.OwnerAvatarUrl)
        };
    }

    /// Null when the title does not contain the query; a blank query admits every row. The owner
    /// shows even when it is the viewer, as on the server tabs; a session on this machine carries
    /// no owner, so it is the viewer's.
    public static SearchHitViewModel? ForSidebar(RailSessionViewModel row, string query, DateTimeOffset now, string? viewerUserId = null) {
        var title = TitleMatch.Split(row.Primary, query);
        if (query.Length > 0 && !title.Any(run => run.Matched)) return null;
        if (title.Count == 0) title = [new SnippetRun(row.SessionAccessibleName, false)];
        var source = row.Row;
        var owner = string.IsNullOrWhiteSpace(source.OwnerName) ? null : source.OwnerName;
        var ownerId = source.OwnerUserId
            ?? (source.Origin is AgentOrigin.Local or AgentOrigin.Pending ? viewerUserId : null);
        return new SearchHitViewModel {
            SessionId = row.Key,
            Kind = SearchMode.Sidebar,
            Title = title,
            Caption = Join(row.RepoLabel, owner),
            OwnerName = owner,
            OwnerInitials = RailSessionViewModel.Initials(owner),
            OwnerAvatarUrl = string.IsNullOrEmpty(source.OwnerAvatarUrl) ? GitHubAvatar.UrlFor(ownerId) : source.OwnerAvatarUrl,
            WhenText = row.IsEnded
                ? RelativeTime.Format(new DateTimeOffset(DateTime.SpecifyKind(row.LastActivityAt ?? row.CreatedAt, DateTimeKind.Utc)), now)
                : "Live",
            Presentation = new PinPresentation(null, null, null)
        };
    }

    static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    static string? RepoOf(string? owner, string? name) =>
        string.IsNullOrEmpty(name) ? null : string.IsNullOrEmpty(owner) ? name : $"{owner}/{name}";

    static string? OwnerNameOf(string? displayName, string? user) => FirstNonEmpty(displayName, user);

    static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    static string WhenOf(string? primary, string? fallback, DateTimeOffset now) {
        foreach (var raw in new[] { primary, fallback }) {
            if (raw is not null && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                return RelativeTime.Format(at, now);
        }
        return "";
    }
}
