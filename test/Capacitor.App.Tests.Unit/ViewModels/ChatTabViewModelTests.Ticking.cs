using DynamicData;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class ChatTabViewModelTests {
    /// The poll stops once an ended session's last lines are read, and a resume starts it again. A
    /// line written just before the end still arrives: the timer outlives the end by one read.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_poll_stops_after_an_ended_sessions_last_read_and_starts_again_on_resume() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            try {
                var path = Tmp.CreateFile("t.jsonl", [UserLine]);
                await h.PushAsync(Dto(path));
                await WaitUntilAsync(() => h.Chat.Items.Count == 1, what: "the initial load");
                await Assert.That(h.Chat.IsTickingForTesting).IsTrue();

                await File.AppendAllTextAsync(path, AssistantLine + "\n");
                h.Daemon.Agents.Remove("a1");
                await WaitUntilAsync(async () => {
                    await h.TickAsync();
                    return !h.Chat.IsTickingForTesting;
                }, what: "the poll to stop");
                await Assert.That(h.Chat.Items.Count).IsEqualTo(2);

                await h.PushAsync(Dto(path));
                await WaitUntilAsync(() => h.Chat.IsTickingForTesting, what: "the poll to restart on resume");
            } finally {
                await h.TeardownAsync();
            }
        });
    }
}
