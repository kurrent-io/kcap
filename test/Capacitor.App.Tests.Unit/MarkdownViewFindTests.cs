using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Capacitor.App.Views;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.MarkdownViewHarness;

namespace Capacitor.App.Tests.Unit;

public class MarkdownViewFindTests {
    static List<Run> Marked(MarkdownView view) => Runs(view).Where(r => r.FontWeight == FontWeight.SemiBold).ToList();

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Matches_take_the_search_panes_look_across_prose_links_and_code_without_changing_the_text() => RunOnUiAsync(async () => {
        var (window, view, _) = Show("Find **the needle**, then [a needle link](https://x.test).\n\n```\nneed le NEEDLE\n```");
        try {
            var before = All<TextBlock>(view).Select(Reads).ToList();

            view.FindQuery = "needle";
            Dispatcher.UIThread.RunJobs();

            var info = window.FindResource("KcapInfoBrush");
            var marked = Marked(view);
            await Assert.That(view.FindMatchCount).IsEqualTo(3);
            await Assert.That(marked.Select(r => r.Text ?? "")).IsEquivalentTo(new[] { "needle", "needle", "NEEDLE" });
            await Assert.That(marked.All(r => ReferenceEquals(r.Foreground, info))).IsTrue();
            await Assert.That(All<TextBlock>(view).Select(Reads).ToList()).IsEquivalentTo(before);

            view.FindQuery = null;
            Dispatcher.UIThread.RunJobs();
            await Assert.That(view.FindMatchCount).IsEqualTo(0);
            await Assert.That(Runs(view).Any(r => ReferenceEquals(r.Foreground, info) && r.FontWeight == FontWeight.SemiBold)).IsFalse();
        } finally {
            window.Close();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task The_current_match_is_marked_on_the_selection_brush_until_the_text_changes() => RunOnUiAsync(async () => {
        var (window, view, _) = Show("one needle and another needle");
        try {
            view.FindQuery = "needle";
            view.SetCurrentFind(1);
            Dispatcher.UIThread.RunJobs();
            var selection = window.FindResource("KcapSelectionBrush");

            await Assert.That(Marked(view).Select(r => ReferenceEquals(r.Background, selection))).IsEquivalentTo(new[] { false, true });
            await Assert.That(view.FindMatchBounds(1)).IsNotNull();

            view.Text = "a needle";
            Dispatcher.UIThread.RunJobs();
            await Assert.That(Marked(view).Any(r => r.Background is not null)).IsFalse();
        } finally {
            window.Close();
        }
    });
}
