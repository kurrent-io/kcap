using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Threading;
using Capacitor.App.NeedToKnow;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.MarkdownViewHarness;

namespace Capacitor.App.Tests.Unit.NeedToKnow;

/// The basis is process-wide, so these hold the whole assembly.
[NotInParallel]
public class NeedToKnowViewTests {
    [After(Test)]
    public void Restore() => NeedToKnowBasis.Set(NeedToKnowBasis.Default);

    [Test]
    public async Task A_chat_view_follows_the_basis_as_it_changes() {
        await RunOnUiAsync(async () => {
            NeedToKnowBasis.Set(NeedToKnowBasis.Default);
            var (window, root, _) = Show("Tests pass. ⌞7 ran.⌟ CI is green.");
            try {
                await Assert.That(Reads(Paragraphs(root).Single())).IsEqualTo("Tests pass. 7 ran. CI is green.");

                NeedToKnowBasis.Set(NeedToKnowBasis.Default with { WorthKnowing = NeedToKnowDisplay.Hidden });
                Dispatcher.UIThread.RunJobs();
                var hidden = Spans<InlineUIContainer>(Paragraphs(root).Single().Inlines!).Single().Child as TextBlock;
                await Assert.That(hidden!.Text).IsEqualTo("⋯");
                await Assert.That((ToolTip.GetTip(hidden) as TextBlock)?.Text).IsEqualTo("7 ran.");

                NeedToKnowBasis.Set(NeedToKnowBasis.Default with { WorthKnowing = NeedToKnowDisplay.Marked });
                Dispatcher.UIThread.RunJobs();
                await Assert.That(Reads(Paragraphs(root).Single())).IsEqualTo("Tests pass. ⌞7 ran.⌟ CI is green.");

                NeedToKnowBasis.Set(NeedToKnowBasis.Default with { WorthKnowing = NeedToKnowDisplay.Greyed });
                Dispatcher.UIThread.RunJobs();
                var greyed = Spans<Span>(Paragraphs(root).Single().Inlines!).Where(s => s.Classes.Contains("need-to-know-greyed")).ToList();
                await Assert.That(greyed.Count).IsEqualTo(1);
                await Assert.That(greyed[0].Inlines.Text).IsEqualTo("7 ran.");
            } finally {
                window.Close();
            }
        });
    }
}
