using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia;
using Avalonia.Headless;
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

                NeedToKnowBasis.Set(NeedToKnowBasis.Default with { Detail = NeedToKnowDisplay.Collapsed });
                Dispatcher.UIThread.RunJobs();
                var hidden = Spans<InlineUIContainer>(Paragraphs(root).Single().Inlines!).Single().Child as TextBlock;
                await Assert.That(hidden!.Text).IsEqualTo("⋯");
                await Assert.That((ToolTip.GetTip(hidden) as TextBlock)?.Text).IsEqualTo("7 ran.");

                NeedToKnowBasis.Set(NeedToKnowBasis.Default with { Detail = NeedToKnowDisplay.Marked });
                Dispatcher.UIThread.RunJobs();
                await Assert.That(Reads(Paragraphs(root).Single())).IsEqualTo("Tests pass. ⌞7 ran.⌟ CI is green.");

                NeedToKnowBasis.Set(NeedToKnowBasis.Default with { Detail = NeedToKnowDisplay.Faded });
                Dispatcher.UIThread.RunJobs();
                var greyed = Spans<Span>(Paragraphs(root).Single().Inlines!).Where(s => s.Classes.Contains("need-to-know-faded")).ToList();
                await Assert.That(greyed.Count).IsEqualTo(1);
                await Assert.That(greyed[0].Inlines.Text).IsEqualTo("7 ran.");
            } finally {
                window.Close();
            }
        });
    }

    [Test]
    public async Task The_pointer_lights_the_faded_footnote_span_under_it() {
        await RunOnUiAsync(async () => {
            NeedToKnowBasis.Set(new(NeedToKnowDisplay.Faded, NeedToKnowDisplay.Faded));
            var (window, root, _) = Show("Tests pass. ⌞⌞The launcher suite takes forty seconds.⌟⌟");
            try {
                var block = Paragraphs(root).Single();
                var footnote = Spans<Span>(block.Inlines!).Single(s => s.Classes.Contains("need-to-know-footnote"));

                window.MouseMove(At(block, 20, window));
                Dispatcher.UIThread.RunJobs();
                await Assert.That(footnote.Classes.Contains("need-to-know-lit")).IsTrue();

                window.MouseMove(At(block, 3, window));
                Dispatcher.UIThread.RunJobs();
                await Assert.That(footnote.Classes.Contains("need-to-know-lit")).IsFalse();
            } finally {
                window.Close();
            }
        });
    }

    [Test]
    [Arguments("Plain lead. ⌞⌞Footnote middle.⌟⌟ Plain tail.")]
    [Arguments("- **Bold lead:** text with `code` here. ⌞⌞Footnote middle.⌟⌟ Plain tail.")]
    [Arguments("See [a link](https://example.com) first. ⌞⌞Footnote middle.⌟⌟ Plain tail.")]
    [Arguments("Lead. ⌞Worth part ⌞⌞Footnote middle.⌟⌟ more worth.⌟ Plain tail.")]
    public async Task The_pointer_lights_a_faded_footnote_span_inside_a_block(string markdown) {
        await RunOnUiAsync(async () => {
            NeedToKnowBasis.Set(new(NeedToKnowDisplay.Faded, NeedToKnowDisplay.Faded));
            var (window, root, _) = Show(markdown);
            try {
                var block = Paragraphs(root).Single();
                var footnote = Spans<Span>(block.Inlines!).Single(s => s.Classes.Contains("need-to-know-footnote"));
                var index = Reads(block).IndexOf("Footnote middle", StringComparison.Ordinal) + 4;
                window.MouseMove(At(block, index, window));
                Dispatcher.UIThread.RunJobs();
                await Assert.That(footnote.Classes.Contains("need-to-know-lit")).IsTrue();
            } finally {
                window.Close();
            }
        });
    }

    [Test]
    public async Task The_pointer_lights_a_faded_footnote_span_on_a_wrapped_line() {
        await RunOnUiAsync(async () => {
            NeedToKnowBasis.Set(new(NeedToKnowDisplay.Faded, NeedToKnowDisplay.Faded));
            var (window, root, _) = Show("⌞⌞This footnote paragraph is long enough to wrap across several lines in a narrow view, so the pointer can rest on its last line.⌟⌟", width: 200);
            try {
                var block = Paragraphs(root).Single();
                var footnote = Spans<Span>(block.Inlines!).Single(s => s.Classes.Contains("need-to-know-footnote"));
                await Assert.That(block.TextLayout.TextLines.Count).IsGreaterThan(2);
                window.MouseMove(At(block, Reads(block).IndexOf("last line", StringComparison.Ordinal), window));
                Dispatcher.UIThread.RunJobs();
                await Assert.That(footnote.Classes.Contains("need-to-know-lit")).IsTrue();
            } finally {
                window.Close();
            }
        });
    }

    static Point At(TextBlock block, int index, Window window) =>
        block.TranslatePoint(block.TextLayout.HitTestTextPosition(index).Center + new Point(block.Padding.Left, block.Padding.Top), window)!.Value;
}
