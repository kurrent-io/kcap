using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class TitleMatchTests {
    [Test]
    public async Task Marks_every_occurrence_ignoring_case_and_keeps_the_title_casing() {
        var runs = TitleMatch.Split("Fix login, then LOGIN again", " Login ");

        await Assert.That(runs).IsEquivalentTo(new[] {
            new SnippetRun("Fix ", false), new SnippetRun("login", true), new SnippetRun(", then ", false),
            new SnippetRun("LOGIN", true), new SnippetRun(" again", false)
        });
    }

    [Test]
    public async Task A_blank_filter_or_a_miss_gives_one_unmatched_run() {
        await Assert.That(TitleMatch.Split("Title", "  ")).IsEquivalentTo(new[] { new SnippetRun("Title", false) });
        await Assert.That(TitleMatch.Split("Title", "zz")).IsEquivalentTo(new[] { new SnippetRun("Title", false) });
        await Assert.That(TitleMatch.Split(null, "zz")).IsEmpty();
    }
}
