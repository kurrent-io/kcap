using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit.ViewModels;

public sealed class ChatTurnIndexTests {
    [Test]
    public async Task Outputs_after_standalone_events_are_ungrouped_even_when_a_prompt_is_loaded() {
        var rows = new ChatItemViewModel[] {
            new AssistantTextItem("Reply from earlier history"),
            new UserTurnItem("Review the patch"),
            new ShellCommandItem("git status"),
            new AssistantTextItem("Resumed reply"),
            new UserTurnItem("Try again", undelivered: true),
            new AssistantTextItem("Final reply"),
        };
        var index = new ChatTurnIndex();
        index.Append(rows, hasOlder: true);

        await Assert.That(index.Starts.Select(entry => entry.Title)).IsEquivalentTo(
            ["Partial turn", "Turn 1", "Shell command", "Ungrouped output", "Undelivered prompt", "Ungrouped output"]);
        await Assert.That(index.Latest.Row).IsSameReferenceAs(rows[1]);
    }
}
