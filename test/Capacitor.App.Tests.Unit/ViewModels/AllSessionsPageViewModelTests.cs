using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit.ViewModels;

public class AllSessionsPageViewModelTests {
    static readonly DateTimeOffset Now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    static readonly RailProject Kcap = new("p1", "kcap", ["h"]);

    /// 49 sessions an hour apart, the two most recent in progress.
    static RepoSessionDto[] Sessions() => [.. Enumerable.Range(0, 49).Select(i => new RepoSessionDto {
        SessionId = $"s{i}", Title = $"Session {i}", Status = i < 2 ? "active" : "ended", AccessLevel = "full",
        StartedAt = Now.AddHours(-i - 1), LastActivityAt = Now.AddHours(-i), EndedAt = i < 2 ? null : Now.AddHours(-i),
        Repo = new() { Hash = "h", Owner = "kurrent-io", Name = "kcap-cli" },
    })];

    static Task Run(Func<FakeServerSessionsService, BehaviorSubject<(RailScope, RailProject?)>, List<string>, AllSessionsPageViewModel, Task> body,
            RepoSessionDto[]? listed = null) =>
        AvaloniaSession.WithImmediateRxScheduler(async () => {
            var all = listed ?? Sessions();
            using var sessions = new FakeServerSessionsService {
                Page = query => new([.. all.Skip(query.Offset).Take(query.Limit)], all.Length),
            };
            using var filter = new BehaviorSubject<(RailScope, RailProject?)>((RailScope.Mine, Kcap));
            var opened = new List<string>();
            using var page = new AllSessionsPageViewModel(sessions, filter, new FakeTimeProvider(Now), opened.Add);
            await body(sessions, filter, opened, page);
        });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task The_page_lists_the_projects_sessions_by_day_and_pages_downwards() => Run(async (sessions, _, _, page) => {
        await Assert.That(page.Heading).IsEqualTo("All sessions · kcap");
        await Assert.That(sessions.PageCalls.Single()).IsEqualTo(new ServerSessionsQuery("all", "me", 20, 0, "p1"));
        await Assert.That(page.Days.Select(day => $"{day.Label}:{day.Rows.Count}")).IsEquivalentTo(["Today:13", "Yesterday:7"], CollectionOrdering.Matching);
        await Assert.That(page.Days[0].Rows[0].Title).IsEqualTo("Session 0");
        await Assert.That(page.CanShowMore).IsTrue();

        await page.ShowMoreCommand.Execute();
        var shown = page.Days[0].Rows[0];
        await page.ShowMoreCommand.Execute();

        await Assert.That(page.Days.Select(day => $"{day.Label}:{day.Rows.Count}"))
            .IsEquivalentTo(["Today:13", "Yesterday:24", "Thu 8 Jan:12"], CollectionOrdering.Matching);
        await Assert.That(page.Days[0].Rows[0]).IsSameReferenceAs(shown);
        await Assert.That(page.CanShowMore).IsFalse();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_row_names_its_repo_work_item_and_part_and_opens_its_session() => Run(async (_, _, opened, page) => {
        var rows = page.Days.Single().Rows;
        await Assert.That(rows[0].Meta).IsEqualTo("kcap-cli · Desktop: offline queue · Retry on reconnect");
        await Assert.That(rows[0].Age).IsEqualTo("2h ago");
        await Assert.That(rows[1].Meta).IsEqualTo("kcap-cli");

        await page.OpenSessionCommand.Execute(rows[1]);
        await Assert.That(opened).IsEquivalentTo(["s1"]);
    }, [
        Sessions()[2] with {
            WorkItem = new() {
                WorkItemId = "w1", Title = "Desktop: offline queue",
                Part = new() { WorkItemId = "w1-p", Title = "Retry on reconnect" },
            },
        },
        Sessions()[1],
    ]);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task The_page_follows_the_rails_scope_and_project() => Run(async (sessions, filter, _, page) => {
        filter.OnNext((RailScope.Team, null));

        await Assert.That(page.Heading).IsEqualTo("All sessions");
        await Assert.That(sessions.PageCalls[^1]).IsEqualTo(new ServerSessionsQuery("all", null, 20, 0, null));
        await Assert.That(page.Days.Sum(day => day.Rows.Count)).IsEqualTo(20);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_failed_page_keeps_the_rows_and_show_more_asks_again() => Run(async (sessions, _, _, page) => {
        var answer = sessions.Page;
        sessions.Page = _ => new(null, 0);
        await page.ShowMoreCommand.Execute();
        await Assert.That(page.Message).IsEqualTo("Couldn't load sessions");
        await Assert.That(page.Days.Sum(day => day.Rows.Count)).IsEqualTo(20);

        sessions.Page = answer;
        await page.ShowMoreCommand.Execute();
        await Assert.That(sessions.PageCalls[^1].Offset).IsEqualTo(20);
        await Assert.That(page.Message).IsNull();
        await Assert.That(page.Days.Sum(day => day.Rows.Count)).IsEqualTo(40);
    });
}
