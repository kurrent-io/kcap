using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Capacitor.App.ProgressiveDisclosure;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.Views.MarkdownViewHarness;

namespace Capacitor.App.Tests.Unit.Views;

/// The basis is process-wide, so these hold the whole assembly.
[NotInParallel]
public class ProgressiveDisclosureViewTests {
    [After(Test)]
    public void Restore() => ProgressiveDisclosureBasis.Set(ProgressiveDisclosureBasis.Default);

    [Test]
    public async Task A_chat_view_follows_the_basis_as_it_changes() {
        await RunOnUiAsync(async () => {
            ProgressiveDisclosureBasis.Set(new(ProgressiveDisclosureDisplay.Normal, ProgressiveDisclosureDisplay.Normal));
            var (window, root, _) = Show("Tests pass. ⌞7 ran.⌟ CI is green.");
            try {
                await Assert.That(Reads(Paragraphs(root).Single())).IsEqualTo("Tests pass. 7 ran. CI is green.");

                ProgressiveDisclosureBasis.Set(new(ProgressiveDisclosureDisplay.Hidden, ProgressiveDisclosureDisplay.Hidden));
                Dispatcher.UIThread.RunJobs();
                await Assert.That(Reads(Paragraphs(root).Single())).IsEqualTo("Tests pass. CI is green.");

                ProgressiveDisclosureBasis.Set(new(ProgressiveDisclosureDisplay.Faded, ProgressiveDisclosureDisplay.Hidden));
                Dispatcher.UIThread.RunJobs();
                var greyed = Spans<Span>(Paragraphs(root).Single().Inlines!).Where(s => s.Classes.Contains("progressive-disclosure-faded")).ToList();
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
            ProgressiveDisclosureBasis.Set(new(ProgressiveDisclosureDisplay.Faded, ProgressiveDisclosureDisplay.Faded));
            var (window, root, _) = Show("Tests pass. ⌞⌞The launcher suite takes forty seconds.⌟⌟");
            try {
                var block = Paragraphs(root).Single();
                var footnote = Spans<Span>(block.Inlines!).Single(s => s.Classes.Contains("progressive-disclosure-footnote"));

                window.MouseMove(At(block, 20, window));
                Dispatcher.UIThread.RunJobs();
                await Assert.That(footnote.Classes.Contains("progressive-disclosure-lit")).IsTrue();

                window.MouseMove(At(block, 3, window));
                Dispatcher.UIThread.RunJobs();
                await Assert.That(footnote.Classes.Contains("progressive-disclosure-lit")).IsFalse();

                window.MouseMove(At(block, 20, window));
                Dispatcher.UIThread.RunJobs();
                var end = At(block, Reads(block).Length - 1, window);
                window.MouseMove(new Point(end.X + 15, end.Y));
                Dispatcher.UIThread.RunJobs();
                await Assert.That(footnote.Classes.Contains("progressive-disclosure-lit")).IsFalse();

                window.MouseMove(At(block, 20, window));
                Dispatcher.UIThread.RunJobs();
                window.MouseMove(new Point(window.Bounds.Width - 5, end.Y));
                Dispatcher.UIThread.RunJobs();
                await Assert.That(footnote.Classes.Contains("progressive-disclosure-lit")).IsFalse();
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
            ProgressiveDisclosureBasis.Set(new(ProgressiveDisclosureDisplay.Faded, ProgressiveDisclosureDisplay.Faded));
            var (window, root, _) = Show(markdown);
            try {
                var block = Paragraphs(root).Single();
                var footnote = Spans<Span>(block.Inlines!).Single(s => s.Classes.Contains("progressive-disclosure-footnote"));
                var index = Reads(block).IndexOf("Footnote middle", StringComparison.Ordinal) + 4;
                window.MouseMove(At(block, index, window));
                Dispatcher.UIThread.RunJobs();
                await Assert.That(footnote.Classes.Contains("progressive-disclosure-lit")).IsTrue();
            } finally {
                window.Close();
            }
        });
    }

    [Test]
    public async Task The_pointer_lights_a_faded_footnote_span_on_a_wrapped_line() {
        await RunOnUiAsync(async () => {
            ProgressiveDisclosureBasis.Set(new(ProgressiveDisclosureDisplay.Faded, ProgressiveDisclosureDisplay.Faded));
            var (window, root, _) = Show("⌞⌞This footnote paragraph is long enough to wrap across several lines in a narrow view, so the pointer can rest on its last line.⌟⌟", width: 200);
            try {
                var block = Paragraphs(root).Single();
                var footnote = Spans<Span>(block.Inlines!).Single(s => s.Classes.Contains("progressive-disclosure-footnote"));
                await Assert.That(block.TextLayout.TextLines.Count).IsGreaterThan(2);
                window.MouseMove(At(block, Reads(block).IndexOf("last line", StringComparison.Ordinal), window));
                Dispatcher.UIThread.RunJobs();
                await Assert.That(footnote.Classes.Contains("progressive-disclosure-lit")).IsTrue();

                var lines = block.TextLayout.TextLines;
                var (short_, top) = (lines[0], 0.0);
                for (var i = 0; i < lines.Count - 1; i++) {
                    if (lines[i].WidthIncludingTrailingWhitespace < short_.WidthIncludingTrailingWhitespace) (short_, top) = (lines[i], lines.Take(i).Sum(l => l.Height));
                }
                await Assert.That(block.Bounds.Width - short_.WidthIncludingTrailingWhitespace).IsGreaterThan(6);
                window.MouseMove(block.TranslatePoint(new Point(short_.WidthIncludingTrailingWhitespace + 3, top + short_.Height / 2), window)!.Value);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(footnote.Classes.Contains("progressive-disclosure-lit")).IsFalse();
            } finally {
                window.Close();
            }
        });
    }

    static Point At(TextBlock block, int index, Window window) =>
        block.TranslatePoint(block.TextLayout.HitTestTextPosition(index).Center + new Point(block.Padding.Left, block.Padding.Top), window)!.Value;
}
