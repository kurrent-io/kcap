using System.Collections.Frozen;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class PendingAttentionTests {
    static readonly IReadOnlyDictionary<string, string> Map =
        new Dictionary<string, string> { ["s-open"] = "a-open", ["s-closed"] = "a-closed" };

    /// An open session's pending items reach the rail and tray only as cards, the same items its
    /// chat shows: the tracker can still list an interrupt the chat never renders, such as a
    /// transcript question, and counting it marked the session with nothing to answer.
    [Test]
    public async Task An_open_session_is_left_to_its_cards() {
        var unopened = PendingAttention.Unopened(
            attention: new HashSet<string> { "s-open", "s-closed" }, open: new HashSet<string> { "s-open" });

        await Assert.That(unopened).DoesNotContain("s-open");
        await Assert.That(unopened).Contains("s-closed");
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
