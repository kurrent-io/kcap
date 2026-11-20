using System.Collections.Frozen;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Pages;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit.ViewModels.Pages;

public class WorkItemsPageViewModelTests {
    static readonly RailProject Kcap = new("p1", "kcap", ["h"]);
    static readonly WorkItemPerson Me = new("me-id", "Me Myself", null);
    static readonly WorkItemPerson Ana = new("ana-id", "Ana Lee", "https://avatars/ana");

    static RailWorkItem Item(string id, string title, int done, int total, WorkStatus status, WorkItemPerson[] people, params string[] sessions) =>
        new(id, title, done, total, sessions.ToFrozenDictionary(s => s, _ => RailWorkItem.NoPart, StringComparer.Ordinal), Status: status, People: people);

    /// The project's open work items list under status sections with their parts, sessions and people; search narrows
    /// by title or person, the owner filter keeps items holding that person's session and follows the rail's scope, a
    /// row opens its work item, and a project without one names why the page is empty.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task The_page_lists_the_projects_open_work_items_by_status_for_the_team() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        var reads = new List<string>();
        using var filter = new BehaviorSubject<(RailScope, RailProject?)>((RailScope.Team, Kcap));
        var opened = new List<string>();
        using var page = new WorkItemsPageViewModel((id, _) => {
            reads.Add(id);
            return Task.FromResult<RailWorkItems?>(new(true, [
                Item("w1", "Offline queue", 1, 3, WorkStatus.Open, [Me, Ana], "s1", "s2"),
                Item("w2", "Sidebar polish", 0, 0, WorkStatus.InProgress, [Ana], "s3"),
            ]));
        }, filter, Observable.Return<IReadOnlySet<string>>(new HashSet<string> { Me.UserId }), opened.Add);

        await Assert.That(page.Heading).IsEqualTo("Work items · kcap");
        await Assert.That(page.Count).IsEqualTo("2 open");
        await Assert.That(page.Groups.Select(group => $"{group.Label}: {string.Join(", ", group.Rows.Select(row => row.Title))}"))
            .IsEquivalentTo(["In progress: Sidebar polish", "Open: Offline queue"], CollectionOrdering.Matching);
        await Assert.That(page.Groups[1].Rows[0].Meta).IsEqualTo("1 of 3 parts · 2 sessions");
        await Assert.That(page.Groups[1].Rows[0].People.Select(p => $"{p.Name}|{p.Initials}"))
            .IsEquivalentTo(["Me Myself|MM", "Ana Lee|AL"], CollectionOrdering.Matching);
        await Assert.That(page.Owners.Select(o => o.Label)).IsEquivalentTo(["Anyone", "You", "Ana Lee"], CollectionOrdering.Matching);

        page.Search = "ana";
        await Assert.That(page.Count).IsEqualTo("2 open");
        page.Search = "sidebar";
        await Assert.That(page.Groups.SelectMany(g => g.Rows).Select(row => row.WorkItemId)).IsEquivalentTo(["w2"]);
        await Assert.That(page.Count).IsEqualTo("1 of 2 open");
        page.Search = "nothing";
        await Assert.That(page.Message).IsEqualTo("No work items match");
        page.Search = "";

        filter.OnNext((RailScope.Mine, Kcap));
        await Assert.That(page.Owner).IsEqualTo(SessionsPageViewModel.You);
        await Assert.That(page.Groups.SelectMany(g => g.Rows).Select(row => row.WorkItemId)).IsEquivalentTo(["w1"]);
        page.Owner = page.Owners[2];
        await Assert.That(page.Groups.SelectMany(g => g.Rows).Select(row => row.WorkItemId)).IsEquivalentTo(["w2", "w1"]);

        await page.OpenWorkItemCommand.Execute(page.Groups[0].Rows[0]);
        await Assert.That(opened).IsEquivalentTo(["w2"]);

        filter.OnNext((RailScope.Mine, null));
        await Assert.That(page.Groups).IsEmpty();
        await Assert.That(page.Message).IsEqualTo("Pick a project to see its work items");
        await Assert.That(reads).IsEquivalentTo(["p1"]);
    });
}
