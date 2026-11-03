using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class NavigationHistoryTests {
    static NavigationEntry Session(string id) => new(id, null, false, false, false, null, null);

    /// Leaving the same place twice in a row is one entry, so back never lands where it already is.
    [Test]
    public async Task Leaving_the_same_place_twice_keeps_one_entry() {
        var history = new NavigationHistory();
        history.Left(Session("a1"));
        history.Left(Session("a1"));

        await Assert.That(history.TakeBack()?.AgentId).IsEqualTo("a1");
        await Assert.That(history.CanGoBack).IsFalse();
    }

    /// Two sessions under one agent id are two places; a launching entry with no session yet is the same place.
    [Test]
    public async Task One_agent_with_two_sessions_keeps_both_entries() {
        var history = new NavigationHistory();
        history.Left(new NavigationEntry("a1", "s1", false, false, false, null, null));
        history.Left(new NavigationEntry("a1", "s2", false, false, false, null, null));
        history.Left(new NavigationEntry("a1", null, false, false, false, null, null));

        await Assert.That(history.TakeBack()?.SessionId).IsNull();
        await Assert.That(history.TakeBack()?.SessionId).IsEqualTo("s1");
    }

    /// The oldest entries fall off once the history is full.
    [Test]
    public async Task The_oldest_entry_falls_off_past_the_limit() {
        var history = new NavigationHistory();
        for (var i = 0; i <= NavigationHistory.Limit; i++) history.Left(Session($"a{i}"));

        NavigationEntry? last = null;
        var count = 0;
        while (history.TakeBack() is { } entry) {
            last = entry;
            count++;
        }

        await Assert.That(count).IsEqualTo(NavigationHistory.Limit);
        await Assert.That(last?.AgentId).IsEqualTo("a1");
    }
}
