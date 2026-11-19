using System.Collections.Frozen;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Pages;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit.ViewModels.Pages;

public class WorkItemsPageViewModelTests {
    static readonly RailProject Kcap = new("p1", "kcap", ["h"]);

    static RailWorkItem Item(string id, string title, int done, int total, params string[] sessions) =>
        new(id, title, done, total, sessions.ToFrozenDictionary(s => s, _ => RailWorkItem.NoPart, StringComparer.Ordinal));

    /// The project's open work items list with their parts and sessions, search narrows them by title, a row opens its
    /// work item, and a project without one names why the page is empty.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task The_page_lists_the_projects_open_work_items_and_searches_their_titles() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        var reads = new List<string>();
        using var project = new BehaviorSubject<RailProject?>(Kcap);
        var opened = new List<string>();
        using var page = new WorkItemsPageViewModel((id, _) => {
            reads.Add(id);
            return Task.FromResult<RailWorkItems?>(new(true, [Item("w1", "Offline queue", 1, 3, "s1", "s2"), Item("w2", "Sidebar polish", 0, 0, "s3")]));
        }, project, opened.Add);

        await Assert.That(page.Heading).IsEqualTo("Work items · kcap");
        await Assert.That(page.Count).IsEqualTo("2 open");
        await Assert.That(page.Rows.Select(row => $"{row.Title}|{row.Meta}"))
            .IsEquivalentTo(["Offline queue|1 of 3 parts · 2 sessions", "Sidebar polish|1 session"], CollectionOrdering.Matching);

        page.Search = "sidebar";
        await Assert.That(page.Rows.Select(row => row.WorkItemId)).IsEquivalentTo(["w2"]);
        await Assert.That(page.Count).IsEqualTo("1 of 2 open");
        page.Search = "nothing";
        await Assert.That(page.Message).IsEqualTo("No work items match");

        page.Search = "";
        await page.OpenWorkItemCommand.Execute(page.Rows[0]);
        await Assert.That(opened).IsEquivalentTo(["w1"]);

        project.OnNext(null);
        await Assert.That(page.Rows).IsEmpty();
        await Assert.That(page.Message).IsEqualTo("Pick a project to see its work items");
        await Assert.That(reads).IsEquivalentTo(["p1"]);
    });
}
