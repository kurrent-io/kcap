using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Capacitor.App.Views;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.Views.MarkdownViewHarness;

namespace Capacitor.App.Tests.Unit.Views;

public class MarkdownViewFindTests {
    static List<Run> Marked(MarkdownView view) => Runs(view).Where(r => r.FontWeight == FontWeight.SemiBold).ToList();

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Matches_are_marked_across_prose_links_and_code_without_changing_the_text() => RunOnUiAsync(async () => {
        var (window, view, _) = Show("Find **the needle**, then [a needle link](https://x.test).\n\n```\nneed le NEEDLE\n```");
        try {
            var before = All<TextBlock>(view).Select(Reads).ToList();

            view.FindQuery = "needle";
            Dispatcher.UIThread.RunJobs();

            var marked = Marked(view);
            await Assert.That(view.FindMatchCount).IsEqualTo(3);
            await Assert.That(marked.Select(r => r.Text ?? "")).IsEquivalentTo(new[] { "needle", "needle", "NEEDLE" });
            await Assert.That(All<TextBlock>(view).Select(Reads).ToList()).IsEquivalentTo(before);

            view.FindQuery = null;
            Dispatcher.UIThread.RunJobs();
            await Assert.That(view.FindMatchCount).IsEqualTo(0);
            await Assert.That(Marked(view)).IsEmpty();
        } finally {
            window.Close();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task The_current_match_holds_until_the_text_changes() => RunOnUiAsync(async () => {
        var (window, view, _) = Show("one needle and another needle");
        try {
            view.FindQuery = "needle";
            view.SetCurrentFind(1);
            Dispatcher.UIThread.RunJobs();

            await Assert.That(view.CurrentFindForTesting).IsEqualTo(1);
            await Assert.That(view.FindMatchBounds(1)).IsNotNull();

            view.Text = "a needle";
            Dispatcher.UIThread.RunJobs();
            await Assert.That(view.CurrentFindForTesting).IsEqualTo(-1);
        } finally {
            window.Close();
        }
    });
}
