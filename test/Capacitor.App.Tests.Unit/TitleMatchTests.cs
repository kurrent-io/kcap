using System.Globalization;
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

    [Test]
    public async Task Case_folds_by_the_current_culture() {
        var saved = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var runs = TitleMatch.Split("İzmir deploy", "izmir");

            await Assert.That(runs).IsEquivalentTo(new[] { new SnippetRun("İzmir", true), new SnippetRun(" deploy", false) });
        } finally {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
