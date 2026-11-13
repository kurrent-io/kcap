using System.Collections.Frozen;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit.Services;

public class PendingAttentionTests {
    static readonly IReadOnlyDictionary<string, string> Map =
        new Dictionary<string, string> { ["s-open"] = "a-open", ["s-closed"] = "a-closed" };

    /// An open session's pending items reach the rail and tray only as cards, the same items its
    /// chat shows: the tracker can still list an interrupt the chat never renders, such as a
    /// transcript question, and counting it marked the session with nothing to answer.
    [Test]
    public async Task An_open_session_is_left_to_its_cards() {
        var unopened = PendingAttention.Unopened(
            attention: new HashSet<string> { "s-open", "s-closed" },
            established: new HashSet<string> { "s-open", "s-closed" },
            open: new HashSet<string> { "s-open" });

        await Assert.That(unopened).DoesNotContain("s-open");
        await Assert.That(unopened).Contains("s-closed");
    }

    /// Notification subscriptions establish access to every session, and the feed reconciles only
    /// answerable interrupts, so established access alone must not silence the tracker.
    [Test]
    public async Task Established_access_without_an_open_workspace_keeps_the_tracker() {
        var unopened = PendingAttention.Unopened(
            attention: new HashSet<string> { "s-closed" },
            established: new HashSet<string> { "s-closed" },
            open: FrozenSet<string>.Empty);

        await Assert.That(unopened).Contains("s-closed");
    }

    /// Until access stands the feed has not reconciled the session, so the tracker still speaks for it.
    [Test]
    public async Task An_open_session_without_access_keeps_the_tracker() {
        var unopened = PendingAttention.Unopened(
            attention: new HashSet<string> { "s-open" },
            established: FrozenSet<string>.Empty,
            open: new HashSet<string> { "s-open" });

        await Assert.That(unopened).Contains("s-open");
    }

    /// The tray's entry list is how the user finds a waiting remote session, so an open one
    /// holding a real card stays listed even though the tracker no longer speaks for it.
    [Test]
    public async Task The_tray_lists_an_open_session_holding_a_card() {
        var card = PendingPermissionRequest.FromServer(
            new ServerPermissionRequest("s-open", "r1", "Bash", null, null), "claude", DateTimeOffset.UnixEpoch);

        var sessions = PendingAttention.TraySessions(unopened: new HashSet<string> { "s-closed" }, cards: [card]);

        await Assert.That(sessions).Contains("s-open");
        await Assert.That(sessions).Contains("s-closed");
    }

    [Test]
    public async Task A_local_card_does_not_list_a_remote_session_sharing_its_id() {
        var local = new PendingPermissionRequest(new PermissionPendingDto(
            "l1", "a-local", "s-open", "claude", "Bash", null, null, false, false, "2026-11-02T00:00:00Z"));

        var sessions = PendingAttention.TraySessions(unopened: FrozenSet<string>.Empty, cards: [local]);

        await Assert.That(sessions).IsEmpty();
    }

    [Test]
    public async Task A_card_marks_its_agent_whether_or_not_the_session_is_open() {
        var agents = PendingAttention.Agents(
            cards: new HashSet<string> { "a-open" }, unopened: FrozenSet<string>.Empty, sessionAgents: Map);

        await Assert.That(agents).Contains("a-open");
    }

    [Test]
    public async Task An_unopened_session_marks_its_agent() {
        var agents = PendingAttention.Agents(
            cards: FrozenSet<string>.Empty, unopened: new HashSet<string> { "s-closed" }, sessionAgents: Map);

        await Assert.That(agents).Contains("a-closed");
    }

    [Test]
    public async Task A_tracked_session_with_no_known_agent_marks_nothing() {
        var agents = PendingAttention.Agents(
            cards: FrozenSet<string>.Empty, unopened: new HashSet<string> { "s-unknown" }, sessionAgents: Map);

        await Assert.That(agents).IsEmpty();
    }
}
