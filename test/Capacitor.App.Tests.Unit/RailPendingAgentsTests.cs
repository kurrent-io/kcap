using System.Collections.Frozen;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class RailPendingAgentsTests {
    static readonly IReadOnlyDictionary<string, string> Map =
        new Dictionary<string, string> { ["s-open"] = "a-open", ["s-closed"] = "a-closed" };

    /// An open session's pending items reach the rail only as cards, the same items its chat
    /// shows: the tracker can still list an interrupt the chat never renders, such as a transcript
    /// question, and counting it lit "Needs you" with nothing to answer.
    [Test]
    public async Task An_open_session_is_marked_by_its_cards_not_by_the_tracker() {
        var agents = RailPendingAgents.Compose(
            cards: FrozenSet<string>.Empty,
            attention: new HashSet<string> { "s-open" },
            open: new HashSet<string> { "s-open" },
            sessionAgents: Map);

        await Assert.That(agents).DoesNotContain("a-open");
    }

    [Test]
    public async Task A_session_the_app_has_not_opened_is_marked_by_the_tracker() {
        var agents = RailPendingAgents.Compose(
            cards: FrozenSet<string>.Empty,
            attention: new HashSet<string> { "s-closed" },
            open: new HashSet<string> { "s-open" },
            sessionAgents: Map);

        await Assert.That(agents).Contains("a-closed");
    }

    [Test]
    public async Task A_card_marks_its_agent_whether_or_not_the_session_is_open() {
        var agents = RailPendingAgents.Compose(
            cards: new HashSet<string> { "a-open" },
            attention: FrozenSet<string>.Empty,
            open: new HashSet<string> { "s-open" },
            sessionAgents: Map);

        await Assert.That(agents).Contains("a-open");
    }

    [Test]
    public async Task A_tracked_session_with_no_known_agent_marks_nothing() {
        var agents = RailPendingAgents.Compose(
            cards: FrozenSet<string>.Empty,
            attention: new HashSet<string> { "s-unknown" },
            open: FrozenSet<string>.Empty,
            sessionAgents: Map);

        await Assert.That(agents).IsEmpty();
    }
}
