using System.Collections.Frozen;
using Capacitor.Cli.Core;
using Capacitor.Remote.Models;
using static Capacitor.App.Services.SessionFieldSource;

namespace Capacitor.App.Services;

internal sealed record SessionMergeState {
    public static SessionMergeState Empty { get; } = new();

    public SessionMergeKind Kind { get; init; } = SessionMergeKind.RecordedOnly;
    public HostedSessionCheckout? Checkout { get; init; }
    public DateTime? HostedUntil { get; init; }
    public DateTime? FinishedUntil { get; init; }
    public bool HasRetention => Checkout is not null || HostedUntil is not null || FinishedUntil is not null;
    public DateTime? NextExpiry => HostedUntil is { } hosted && (FinishedUntil is null || hosted < FinishedUntil) ? hosted : FinishedUntil;

    static readonly FrozenDictionary<SessionMergeKind, (SessionFieldSource Status, SessionFieldSource Checkout,
            SessionFieldSource Branch, SessionFieldSource Owner, SessionFieldSource Activity, SessionFieldSource Access)> Sources =
        new Dictionary<SessionMergeKind, (SessionFieldSource, SessionFieldSource, SessionFieldSource, SessionFieldSource, SessionFieldSource, SessionFieldSource)> {
            [SessionMergeKind.HostedLocally]  = (HostedRow, HostedRow,        HostedThenListing,   Listing, Listing, HostedRow),
            [SessionMergeKind.HostedRemotely] = (HostedRow, HostedRow,        HostedThenListing,   Listing, Listing, HostedRow),
            [SessionMergeKind.HandingOver]    = (Listing,   RetainedCheckout, RetainedThenListing, Listing, Listing, Listing),
            [SessionMergeKind.FinishedHeld]   = (HeldEnd,   RetainedCheckout, RetainedThenListing, Listing, Listing, Listing),
            [SessionMergeKind.RecordedOnly]   = (Listing,   RetainedCheckout, RetainedThenListing, Listing, Listing, Listing),
            [SessionMergeKind.Restored]       = (Listing,   Listing,          Listing,             Listing, Listing, Listing),
        }.ToFrozenDictionary();

    public (SessionMergeState State, IReadOnlySet<string> KeptRows) Transition(
            IReadOnlyList<AgentRow> rows, RepoSessionDto? listing, bool localConnected, bool registryCurrent, DateTime now,
            bool observedFinished = false) {
        HashSet<string>? kept = null;
        var renewHosted = false;
        var liveLocal = false;
        var hasLocal = false;
        foreach (var row in rows) {
            var current = row.Origin == AgentOrigin.Local ? localConnected : registryCurrent;
            var rule = (listing is not null, row.Origin, current, row.Status, listing?.Status != "active") switch {
                (false, _, _, _, _) => (Keep: true, Renew: false, Resume: false),
                (true, _, false, _, true) => (Keep: false, Renew: false, Resume: false),
                (true, _, false, _, false) => (Keep: true, Renew: false, Resume: false),
                (true, AgentOrigin.Local, true, "Completed", _) => (Keep: false, Renew: false, Resume: false),
                (true, AgentOrigin.Local, true, "Failed", _) => (Keep: true, Renew: false, Resume: false),
                (true, AgentOrigin.Local, true, _, _) => (Keep: true, Renew: true, Resume: true),
                (true, AgentOrigin.Remote, true, AgentRow.DaemonDownStatus, _) => (Keep: true, Renew: false, Resume: false),
                (true, AgentOrigin.Remote, true, _, _) => (Keep: true, Renew: true, Resume: false),
                _ => (Keep: true, Renew: false, Resume: false),
            };
            if (rule.Keep) {
                (kept ??= new(StringComparer.Ordinal)).Add(row.Key);
                hasLocal |= row.Origin == AgentOrigin.Local;
            }
            renewHosted |= rule.Renew;
            liveLocal |= rule.Resume;
        }
        var hostedUntil = renewHosted ? now + AgentDirectory.HandoverGrace : HostedUntil;
        var finishedUntil = observedFinished ? now + AgentDirectory.FinishedHold : FinishedUntil;
        if (liveLocal || listing is { Status: not "active" }) finishedUntil = null;
        var kind = hasLocal ? SessionMergeKind.HostedLocally
            : kept is { Count: > 0 } ? SessionMergeKind.HostedRemotely
            : finishedUntil > now && (listing is null || listing.Status == "active") ? SessionMergeKind.FinishedHeld
            : hostedUntil > now ? SessionMergeKind.HandingOver
            : Checkout is null && listing?.Cwd is { } cwd && GitRepository.AgentWorktreeOf(cwd) is not null ? SessionMergeKind.Restored
            : SessionMergeKind.RecordedOnly;
        return (this with { Kind = kind, HostedUntil = hostedUntil, FinishedUntil = finishedUntil }, kept ?? (IReadOnlySet<string>)FrozenSet<string>.Empty);
    }

    public SessionMergeState Expire(DateTime now) {
        var hosted = HostedUntil <= now ? null : HostedUntil;
        var finished = FinishedUntil <= now ? null : FinishedUntil;
        return hosted == HostedUntil && finished == FinishedUntil ? this : this with { HostedUntil = hosted, FinishedUntil = finished };
    }

    public AgentRow ProjectDaemon(AgentRow row, RepoSessionDto? listing, bool? ownerIsViewer) {
        if (listing is null) return row;
        var kind = row.Origin == AgentOrigin.Local ? SessionMergeKind.HostedLocally : SessionMergeKind.HostedRemotely;
        return ApplyListing(row, listing, ownerIsViewer, kind);
    }

    public AgentRow ProjectServer(RepoSessionDto listing, bool? ownerIsViewer, DateTime now) {
        var fields = Sources[Kind];
        var shown = fields.Status == HeldEnd
            ? listing with { Status = "ended", EndedAt = listing.LastActivityAt } : listing;
        var row = AgentRow.FromServer(shown, HostedUntil > now);
        return ApplyListing(row, listing, ownerIsViewer, Kind);
    }

    AgentRow ApplyListing(AgentRow row, RepoSessionDto listing, bool? ownerIsViewer, SessionMergeKind kind) {
        var fields = Sources[kind];
        var retained = fields.Checkout == RetainedCheckout && listing.AccessLevel == "full" ? Checkout : null;
        if (retained is not null) row = retained.Apply(row);
        var branch = fields.Branch switch {
            HostedThenListing => row.Branch ?? listing.Branch,
            RetainedThenListing when retained is { BranchFromDaemon: true } => retained.Branch ?? row.Branch,
            RetainedThenListing when retained is not null => row.Branch ?? retained.Branch,
            _ => row.Branch,
        };
        return row with {
            OwnerUserId = fields.Owner == Listing ? listing.Owner?.UserId : row.OwnerUserId,
            OwnerName = fields.Owner == Listing ? listing.Owner?.DisplayName ?? listing.Owner?.Username : row.OwnerName,
            OwnerAvatarUrl = fields.Owner == Listing ? listing.Owner?.AvatarUrl : row.OwnerAvatarUrl,
            OwnerIsViewer = fields.Owner == Listing ? ownerIsViewer : row.OwnerIsViewer,
            LastActivityAt = fields.Activity == Listing ? listing.LastActivityAt.UtcDateTime : row.LastActivityAt,
            AccessLevel = fields.Access == Listing ? listing.AccessLevel : row.AccessLevel,
            ListedPullRequests = listing.PullRequests,
            Branch = branch,
        };
    }
}
