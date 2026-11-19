using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit.ViewModels;

[NotInParallel("AvaloniaSession")]
public class NeedToKnowSessionTests {
    [Test]
    public Task A_chat_notices_its_first_graded_reply() => RunOnUiAsync(async () => {
        await using var h = new ChatPromptHarness("claude");
        await h.ReadyAsync();
        h.Feed.Enqueue(new([new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "Plain `⌞code⌟` aside.")], [], []));
        await h.TickAsync();
        await Assert.That(h.Chat.Items.OfType<AssistantTextItem>().Count()).IsEqualTo(1);
        await Assert.That(h.Chat.HasNeedToKnowMarks).IsFalse();

        h.Feed.Enqueue(new([new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "Done. ⌞It took a while.⌟")], [], []));
        await h.TickAsync();
        await Assert.That(h.Chat.HasNeedToKnowMarks).IsTrue();
    });

    [Test]
    public Task Another_sessions_feed_starts_without_the_graded_reply() => RunOnUiAsync(async () => {
        await using var h = new ChatPromptHarness("claude");
        await h.ReadyAsync();
        h.Feed.Enqueue(new([new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "Done. ⌞It took a while.⌟")], [], []));
        await h.TickAsync();
        await Assert.That(h.Chat.HasNeedToKnowMarks).IsTrue();

        h.SwitchFeed("other");
        await h.ReadyAsync();
        await Assert.That(h.Chat.HasNeedToKnowMarks).IsFalse();
    });
}
