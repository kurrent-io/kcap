using Capacitor.Cli.Core.WorkItems;
using TUnit.Assertions.Enums;

namespace Capacitor.Client.Tests.Unit;

/// One read merged into the card already shown: what a failed section keeps, what a new primary or
/// a terminal answer clears, and how contributors are named.
public class WorkContextCardsTests {
    static readonly WorkContextCard Loading = WorkContextCard.Empty(WorkContextPhase.Loading);

    static SessionWorkItemAssignmentDto Row(string id, string label) =>
        new() { WorkItemId = id, Label = label, Source = "mcp", Confidence = 1, IsPrimary = true };

    static WorkItemPartDto Part(string id, string title, int ordinal) =>
        new() { WorkItemId = id, Title = title, Ordinal = ordinal };

    static WorkItemLinkDto Issue(string shortKey, string url, string? title, string value) =>
        new() { Kind = "issue", Provider = "github", Value = value, ShortKey = shortKey, Url = url, Title = title, LinkClass = "link" };

    static WorkItemContributorDto Person(string userId, string? name, string? avatar = null) =>
        new() { UserId = userId, DisplayName = name, AvatarUrl = avatar };

    static WorkItemDto Item(string id = "w1", string? enriched = "Desktop shell: work-context sidebar", params WorkItemPartDto[] parts) =>
        new() {
            WorkItemId = id, Title = "WK-2198", EnrichedTitle = enriched,
            Key = new WorkItemKeyDto { ShortKey = "WK-2198", Provider = "tracker", Kind = "issue", Value = "WK-2198" },
            State = new WorkItemStateDto { Kind = "in_flight" },
            Parts = [.. parts],
        };

    static WorkItemTopologyDto Blocked(string blocker) => new() {
        PartOf = new WorkItemRefDto { WorkItemId = "w0", Title = "Parent epic" },
        BlockedBy = [new WorkItemRefDto { WorkItemId = "b1", Title = blocker }],
        Cycle = "indeterminate",
    };

    static WorkContextRead ReadyWith(
            SessionWorkItemAssignmentDto primary, WorkItemDto? item = null, WorkItemTopologyDto? topology = null,
            bool itemFailed = false, bool topologyFailed = false) =>
        new(WorkContextReadKind.Ready, [primary], primary, item, topology, new SessionSummaryDto { SessionId = "s" },
            itemFailed, topologyFailed, false, null);

    static WorkContextCard Apply(WorkContextCard card, params WorkContextRead[] reads) {
        foreach (var read in reads) card = WorkContextCards.Apply(card, read, null);
        return card;
    }

    /// The survivor's id is the card's identity, and the assignment may catch up to it a poll
    /// later; only a request for another item drops the card.
    [Test]
    public async Task An_absorbed_item_served_under_another_id_keeps_the_card() {
        var served = Apply(Loading, ReadyWith(Row("w1", "t"), Item(id: "w9", parts: Part("p1", "First", 0))));
        await Assert.That(served.PrimaryId).IsEqualTo("w9");
        await Assert.That(served.RequestedId).IsEqualTo("w1");

        var caughtUp = Apply(served, ReadyWith(Row("w9", "t"), itemFailed: true));
        await Assert.That(caughtUp.Parts.Count).IsEqualTo(1);
        await Assert.That(caughtUp.IsStale).IsTrue();

        var other = Apply(caughtUp, ReadyWith(Row("w9", "t"), Item(id: "w9", parts: Part("p1", "First", 0))), ReadyWith(Row("w1", "t"), itemFailed: true));
        await Assert.That(other.Parts).IsEmpty();
    }

    [Test]
    public async Task A_failed_item_read_keeps_the_last_item_and_marks_the_card_stale() {
        var card = Apply(Loading,
            ReadyWith(Row("w1", "WK-1 — t"), Item(parts: Part("p1", "First", 0))),
            ReadyWith(Row("w1", "WK-1 — t"), itemFailed: true));

        await Assert.That(card.Phase).IsEqualTo(WorkContextPhase.Ready);
        await Assert.That(card.Key).IsEqualTo("WK-2198");
        await Assert.That(card.Title).IsEqualTo("Desktop shell: work-context sidebar");
        await Assert.That(card.StateKind).IsEqualTo("in_flight");
        await Assert.That(card.Parts.Select(p => p.Title)).IsEquivalentTo(new[] { "First" });
        await Assert.That(card.IsStale).IsTrue();
    }

    [Test]
    public async Task A_failed_topology_read_keeps_the_last_topology() {
        var card = Apply(Loading,
            ReadyWith(Row("w1", "t"), Item(), Blocked("Pin the helper")),
            ReadyWith(Row("w1", "t"), Item(), topologyFailed: true));

        await Assert.That(card.BlockedBy).IsEquivalentTo(new[] { "Pin the helper" });
        await Assert.That(card.PartOfTitle).IsEqualTo("Parent epic");
        await Assert.That(card.Cycle).IsEqualTo("indeterminate");
        await Assert.That(card.IsStale).IsTrue();
    }

    [Test]
    public async Task A_new_primary_whose_sections_failed_clears_both_and_shows_its_label() {
        var card = Apply(Loading,
            ReadyWith(Row("w1", "WK-1 — t"), Item(parts: Part("p1", "First", 0)), Blocked("Pin the helper")),
            ReadyWith(Row("w9", "WK-9 — other"), itemFailed: true, topologyFailed: true));

        await Assert.That(card.PrimaryId).IsEqualTo("w9");
        await Assert.That(card.Key).IsNull();
        await Assert.That(card.Title).IsEqualTo("WK-9 — other");
        await Assert.That(card.StateKind).IsNull();
        await Assert.That(card.Parts).IsEmpty();
        await Assert.That(card.BlockedBy).IsEmpty();
        await Assert.That(card.PartOfTitle).IsNull();
        await Assert.That(card.Cycle).IsEqualTo("none");
        await Assert.That(card.IsStale).IsTrue();
    }

    /// A short key is display text only, so when both issues have a URL the URL decides.
    [Test]
    public async Task Two_issues_with_one_url_are_one() {
        var card = Apply(Loading, ReadyWith(Row("w1", "t"), Item() with {
            Links = [
                Issue("#42", "https://example.test/o/r/issues/42", "First", "github:o/r#42"),
                Issue("#42", "https://EXAMPLE.test/o/r/issues/42", "Second", "github:O/R#42"),
            ],
        }));

        await Assert.That(card.Issues.Select(i => i.Title)).IsEquivalentTo(new[] { "First" });
    }

    /// The state is the server's word, carried as sent; a later read replaces it.
    [Test]
    public async Task Each_issue_carries_the_state_its_latest_read_served() {
        var first = Issue("#42", "https://example.test/o/r/issues/42", "First", "github:o/r#42");
        var second = Issue("#43", "https://example.test/o/r/issues/43", "Second", "github:o/r#43");
        var card = Apply(Loading,
            ReadyWith(Row("w1", "t"), Item() with { Links = [first with { State = "In Progress" }, second] }),
            ReadyWith(Row("w1", "t"), Item() with { Links = [first with { State = "Done" }, second] }));

        await Assert.That(card.Issues.Select(i => i.State)).IsEquivalentTo(new[] { "Done", null }, CollectionOrdering.Matching);
    }

    /// The `github` provider numbers issues and pull requests in one sequence, so its seed `!N` is the issue `#N`.
    [Test]
    public async Task A_seed_spelled_as_a_pull_request_folds_its_issue_into_the_title() {
        var card = Apply(Loading, ReadyWith(Row("w1", "!2423"), Item(enriched: null) with {
            Title = "!2423",
            Key   = new WorkItemKeyDto { ShortKey = "!2423", Provider = "github", Kind = "pr", Value = "github:o/r!2423" },
            Links = [Issue("#2423", "https://example.test/o/r/issues/2423", "Tracker title", "github:o/r#2423")],
        }));

        await Assert.That(card.DisplayTitle).IsEqualTo("Tracker title");
        await Assert.That(card.Issues.Count).IsEqualTo(1);
        await Assert.That(card.SeparateIssues).IsEmpty();
    }

    [Test]
    public async Task A_contributor_the_viewer_owns_is_you_with_the_viewers_picture() {
        const string mine = "user_01KYZ590AKWBCFFNKM1KZ1HZ2B";
        var card = Apply(Loading, ReadyWith(Row("w1", "t"), Item() with { Contributors = [Person(mine, null), Person("u2", "Ada")] }) with {
            Viewer = new ViewerIdentityDto { UserId = "github:5", Ids = [mine], AvatarUrl = "https://example.test/me.png" },
        });

        await Assert.That(card.Contributors.Select(c => c.Name)).IsEquivalentTo(new[] { "You", "Ada" }, CollectionOrdering.Matching);
        await Assert.That(card.Contributors.Select(c => c.IsViewer)).IsEquivalentTo(new[] { true, false }, CollectionOrdering.Matching);
        await Assert.That(card.Contributors[0].AvatarUrl).IsEqualTo("https://example.test/me.png");
    }

    [Test]
    public async Task A_contributor_without_a_display_name_keeps_the_name_last_shown() {
        const string userId = "user_01KYZ590AKWBCFFNKM1KZ1HZ2B";
        var card = Apply(Loading,
            ReadyWith(Row("w1", "t"), Item() with { Contributors = [Person(userId, "Ada Lovelace")] }),
            ReadyWith(Row("w1", "t"), Item() with { Contributors = [Person(userId, null)] }));

        await Assert.That(card.Contributors.Select(c => c.Name)).IsEquivalentTo(new[] { "Ada Lovelace" });
    }

    /// An opaque `user_` id is not a name: the session requester's display stands in for that same person,
    /// and anyone else is "Someone".
    [Test]
    public async Task An_opaque_user_id_is_the_requesters_display_for_the_requester_and_someone_otherwise() {
        const string userId = "user_01KYZ590AKWBCFFNKM1KZ1HZ2B";
        var requester = new WorkContextRequester(userId, "norton@example.com");

        var requested = WorkContextCards.Apply(Loading, ReadyWith(Row("w1", "t"), Item() with { Contributors = [Person(userId, null)] }), requester);
        var other = WorkContextCards.Apply(requested,
            ReadyWith(Row("w1", "t"), Item() with { Contributors = [Person("user_01AAAAAAAAAAAAAAAAAAAAAAAA", null)] }), requester);

        await Assert.That(requested.Contributors.Select(c => c.Name)).IsEquivalentTo(new[] { "norton@example.com" });
        await Assert.That(other.Contributors.Select(c => c.Name)).IsEquivalentTo(new[] { "Someone" });
    }

    /// The viewer's own rows are named for whoever the viewer is, so forgetting it, or a read that
    /// keeps the item but names no viewer, names them by their own names again.
    [Test]
    public async Task A_contributor_no_longer_owned_by_the_viewer_takes_its_own_name_and_picture() {
        var shown = Apply(Loading, ReadyWith(Row("w1", "t"), Item() with { Contributors = [Person("u1", "Ada", "https://example.test/ada.png")] }) with {
            Viewer = new ViewerIdentityDto { UserId = "u1", AvatarUrl = "https://example.test/me.png" },
        });
        await Assert.That(shown.Contributors[0].Name).IsEqualTo("You");

        foreach (var card in new[] {
                     WorkContextCards.ForgetViewer(shown, null),
                     WorkContextCards.Apply(shown, ReadyWith(Row("w1", "t"), itemFailed: true), null),
                 }) {
            await Assert.That(card.Viewer).IsNull();
            await Assert.That(card.Contributors).IsEquivalentTo(new[] {
                new WorkContextPerson("u1", "Ada", "https://example.test/ada.png", null, IsViewer: false),
            });
        }
    }

    [Test]
    [Arguments(WorkContextReadKind.SignedOut, WorkContextPhase.SignedOut)]
    [Arguments(WorkContextReadKind.NotInPlan, WorkContextPhase.NotInPlan)]
    [Arguments(WorkContextReadKind.SessionUnknown, WorkContextPhase.SessionUnknown)]
    public async Task A_terminal_answer_clears_the_card_and_sets_its_phase(WorkContextReadKind kind, WorkContextPhase phase) {
        var shown = Apply(Loading, ReadyWith(Row("w1", "WK-1 — t"), Item(parts: Part("p1", "First", 0)) with {
            Overview = "Overview", Links = [Issue("#777", "https://example.test/o/r/issues/777", "Issue", "github:o/r#777")],
            Contributors = [Person("u1", "Ada")], SessionCount = 2,
        }, Blocked("Blocker")) with { Viewer = new ViewerIdentityDto { UserId = "u2" } });

        var card = WorkContextCards.Apply(shown with { IsStale = true }, WorkContextRead.Of(kind), null);

        await Assert.That(card.Phase).IsEqualTo(phase);
        await Assert.That(card.IsStale).IsFalse();
        await Assert.That(card.PrimaryId).IsNull();
        await Assert.That(card.RequestedId).IsNull();
        await Assert.That(card.Key).IsNull();
        await Assert.That(card.Title).IsEqualTo("");
        await Assert.That(card.Overview).IsNull();
        await Assert.That(card.StateKind).IsNull();
        await Assert.That(card.PartOfTitle).IsNull();
        await Assert.That(card.BlockedBy).IsEmpty();
        await Assert.That(card.Cycle).IsEqualTo("none");
        await Assert.That(card.Parts).IsEmpty();
        await Assert.That(card.Issues).IsEmpty();
        await Assert.That(card.SeedIssueIdentity).IsNull();
        await Assert.That(card.Contributors).IsEmpty();
        await Assert.That(card.SessionCount).IsEqualTo(0);
        await Assert.That(card.Viewer).IsNull();
    }

    /// A card already showing an item or its absence goes stale rather than blank; one that shows
    /// nothing yet becomes unreachable.
    [Test]
    public async Task Unreachable_keeps_a_shown_card_stale_and_otherwise_sets_its_phase() {
        var shown = Apply(Loading, ReadyWith(Row("w1", "t"), Item(parts: Part("p1", "First", 0))));
        var unreachable = WorkContextRead.Of(WorkContextReadKind.Unreachable, "no response");

        await Assert.That(WorkContextCards.Apply(shown, unreachable, null)).IsEqualTo(shown with { IsStale = true });
        await Assert.That(WorkContextCards.Apply(Loading, unreachable, null)).IsEqualTo(Loading with { Phase = WorkContextPhase.Unreachable });
    }
}
