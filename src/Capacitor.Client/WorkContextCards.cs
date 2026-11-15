using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Client;

/// How one read merges into the card already shown, section by section: a failed section keeps its
/// last values for the same primary and marks the card stale, an authoritative empty answer clears
/// it, and a terminal answer clears everything the server gave.
public static class WorkContextCards {
    const string ViewerName = "You";

    public static WorkContextCard Apply(WorkContextCard previous, WorkContextRead read, WorkContextRequester? requester) =>
        read.Kind switch {
            WorkContextReadKind.Ready          => ApplyReady(previous, read, requester),
            WorkContextReadKind.SignedOut      => WorkContextCard.Empty(WorkContextPhase.SignedOut),
            WorkContextReadKind.NotInPlan      => WorkContextCard.Empty(WorkContextPhase.NotInPlan),
            WorkContextReadKind.SessionUnknown => WorkContextCard.Empty(WorkContextPhase.SessionUnknown),
            WorkContextReadKind.Unreachable when previous.Phase is WorkContextPhase.Ready or WorkContextPhase.NoWorkItem =>
                previous with { IsStale = true },
            _ => previous with { Phase = WorkContextPhase.Unreachable },
        };

    /// Names the contributors again for no viewer.
    public static WorkContextCard ForgetViewer(WorkContextCard card, WorkContextRequester? requester) =>
        WithViewer(card, null, requester);

    static WorkContextCard WithViewer(WorkContextCard card, ViewerIdentityDto? viewer, WorkContextRequester? requester) =>
        card with { Viewer = viewer, Contributors = People(card.ContributorRows, viewer, requester, card.Contributors) };

    static WorkContextCard ApplyReady(WorkContextCard previous, WorkContextRead read, WorkContextRequester? requester) {
        if (read.Primary is null)
            return WorkContextCard.Empty(WorkContextPhase.NoWorkItem) with { IsStale = read.SummaryFailed, Viewer = read.Viewer };

        var card = WithViewer(previous, read.Viewer, requester);
        var requested = read.Primary.WorkItemId;
        var served = read.Item?.WorkItemId;
        var samePrimary = served is not null
            ? Same(served, card.PrimaryId)
            : Same(requested, card.RequestedId) || Same(requested, card.PrimaryId);
        card = card with { RequestedId = requested, PrimaryId = served ?? (samePrimary ? card.PrimaryId : requested) };

        if (read.Item is { } item) card = ApplyItem(card, item, read.Assignments, requester);
        else if (!samePrimary) card = ClearItem(card) with { Title = read.Primary.Label };

        if (!read.TopologyFailed && read.Topology is { } topology) card = ApplyTopology(card, topology);
        else if (!samePrimary) card = ClearTopology(card);

        return card with { Phase = WorkContextPhase.Ready, IsStale = read.ItemFailed || read.TopologyFailed || read.SummaryFailed };
    }

    static WorkContextCard ApplyItem(WorkContextCard card, WorkItemDto item, IReadOnlyList<SessionWorkItemAssignmentDto> assignments,
            WorkContextRequester? requester) {
        var key = FirstNonBlank(item.Key?.ShortKey);
        // An item with no tracker or generated title arrives with its key as the title.
        var title = FirstNonBlank(item.EnrichedTitle, item.Title) ?? "";
        var attached = new HashSet<string>(assignments.Select(a => a.WorkItemId), StringComparer.Ordinal);
        WorkItemContributorDto[] rows = [.. item.Contributors];
        return card with {
            SeedIssueIdentity = item.Key is { } seed ? SeedIdentity(seed) : null,
            Key = key,
            Title = string.Equals(title, key, StringComparison.OrdinalIgnoreCase) ? "" : title,
            Overview = item.IsOverviewMechanical ? null : FirstNonBlank(item.Overview),
            StateKind = item.State?.Kind,
            Parts = [.. item.Parts.OrderBy(p => p.Ordinal).Select(p => new WorkContextPart(p.Title, PartMark(p, attached)))],
            Issues = Issues(item.Links.Where(l => l.Kind == "issue" && l.LinkClass == "link")),
            ContributorRows = rows,
            Contributors = People(rows, card.Viewer, requester, card.Contributors),
            SessionCount = item.SessionCount,
        };
    }

    static WorkContextCard ClearItem(WorkContextCard card) => card with {
        SeedIssueIdentity = null, Key = null, Title = "", Overview = null, StateKind = null,
        Parts = [], Issues = [], ContributorRows = [], Contributors = [], SessionCount = 0,
    };

    static WorkContextCard ApplyTopology(WorkContextCard card, WorkItemTopologyDto topology) => card with {
        PartOfTitle = topology.PartOf?.Title,
        BlockedBy = [.. topology.BlockedBy.Select(b => b.Title)],
        Cycle = topology.Cycle,
    };

    static WorkContextCard ClearTopology(WorkContextCard card) => card with { PartOfTitle = null, BlockedBy = [], Cycle = "none" };

    /// GitHub numbers issues and pull requests in one sequence, so a seed spelled `!N` can be the
    /// issue `#N` of its repository; other providers number them apart.
    static string SeedIdentity(WorkItemKeyDto seed) =>
        IssueIdentity("issue", seed.Provider, seed.Provider == "github" ? seed.Value.Replace('!', '#') : seed.Value);

    static WorkContextPartMark PartMark(WorkItemPartDto part, HashSet<string> attached) =>
        part.IsSettled ? WorkContextPartMark.Settled
        : attached.Contains(part.WorkItemId) ? WorkContextPartMark.ThisSession
        : WorkContextPartMark.Unknown;

    /// ShortKey is display text only: two null-URL issues from different repos can share `#42`.
    /// Prefer URL when both have one; otherwise the server's kind/provider/value tuple.
    static WorkContextIssue[] Issues(IEnumerable<WorkItemLinkDto> links) {
        var issues = new List<WorkContextIssue>();
        foreach (var link in links) {
            var identity = IssueIdentity(link.Kind, link.Provider, link.Value);
            if (issues.Exists(issue =>
                    link.Url is { Length: > 0 } url && issue.Url is { Length: > 0 } existing
                        && string.Equals(existing, url, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(issue.Identity, identity, StringComparison.Ordinal)))
                continue;
            issues.Add(new WorkContextIssue(link.ShortKey, FirstNonBlank(link.Title) ?? "", link.Url, identity));
        }
        return [.. issues];
    }

    static string IssueIdentity(string kind, string provider, string value) => string.Join('\u001f', kind, provider, value);

    static WorkContextPerson[] People(IReadOnlyList<WorkItemContributorDto> rows, ViewerIdentityDto? viewer,
            WorkContextRequester? requester, IReadOnlyList<WorkContextPerson> shown) =>
        [.. rows.Select(row => {
            var owned = viewer?.Owns(row.UserId) == true;
            return new WorkContextPerson(row.UserId, PersonName(row, owned, requester, shown),
                owned ? viewer!.AvatarUrl ?? row.AvatarUrl : row.AvatarUrl, row.LastActivityAt, owned);
        })];

    /// DisplayName is null for an owner with no user row; the session requester's email/name
    /// stands in when it is the same person, else the name last shown for the id.
    static string PersonName(WorkItemContributorDto row, bool owned, WorkContextRequester? requester, IReadOnlyList<WorkContextPerson> shown) {
        if (owned) return ViewerName;
        if (HumanLabel(row.DisplayName) is { } display) return display;
        if (string.Equals(row.UserId, requester?.Id, StringComparison.Ordinal)
            && HumanLabel(FirstNonBlank(requester?.Display, requester?.Id)) is { } mine)
            return mine;
        if (shown.FirstOrDefault(p => string.Equals(p.UserId, row.UserId, StringComparison.Ordinal)) is { } previous
            && previous.Name != ViewerName && HumanLabel(previous.Name) is { } kept)
            return kept;
        return HumanLabel(row.UserId) ?? "Someone";
    }

    static string? HumanLabel(string? value) {
        var label = FirstNonBlank(value);
        if (label is null || IsOpaqueUserId(label)) return null;
        return label;
    }

    /// A WorkOS id (`user_` + ULID) is not a name. `github:` ids stay visible: they are the
    /// fallback for a contributor with no display name.
    static bool IsOpaqueUserId(string value) {
        if (!value.StartsWith("user_", StringComparison.Ordinal) || value.Length < 25) return false;
        for (var i = 5; i < value.Length; i++)
            if (!char.IsAsciiLetterOrDigit(value[i])) return false;
        return true;
    }

    static string? FirstNonBlank(params string?[] values) {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return null;
    }

    static bool Same(string a, string? b) => string.Equals(a, b, StringComparison.Ordinal);
}
