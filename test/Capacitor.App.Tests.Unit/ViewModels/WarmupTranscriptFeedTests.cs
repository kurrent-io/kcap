using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit.ViewModels;

public class WarmupTranscriptFeedTests {
    /// A payload the parser stops reading would leave the warm-up chat empty and warming nothing,
    /// with no error to show for it.
    [Test]
    public async Task Every_made_up_event_projects_and_each_common_row_kind_is_there() {
        using var feed = new WarmupTranscriptFeed();

        var read = feed.ReadAppended();
        var kinds = read.Lines.SelectMany(line => line.Projection.Envelopes).Select(envelope => envelope.Kind).ToHashSet();

        await Assert.That(read.Status).IsEqualTo(FeedStatus.Reset);
        await Assert.That(read.Lines.Count).IsEqualTo((int)read.SnapshotOffset!);
        await Assert.That(read.Lines.All(line => line.Projection.Envelopes.Count > 0)).IsTrue();
        await Assert.That(kinds).IsEquivalentTo(new[] {
            AcpEventKind.UserMessage, AcpEventKind.AssistantThinking, AcpEventKind.ToolCall, AcpEventKind.ToolResult, AcpEventKind.AssistantText,
        });
        await Assert.That(feed.ReadAppended().Lines).IsEmpty();
    }
}
