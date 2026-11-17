using System.Collections.Frozen;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;
using DynamicData;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit.ViewModels;

public class SessionRailWorkItemTests {
    static readonly string ServerHash = Capacitor.Cli.Core.RepoHashHelper.ComputeRepoHash("kurrent-io", "kcap-server");
    static readonly RailProject Kcap = new("p-kcap", "kcap", [ServerHash]);

    static readonly RailWorkItem Approve = new("approve", "Approve requests from your phone", 2, 6,
        new Dictionary<string, string> { ["build"] = "List every decision waiting on you" }.ToFrozenDictionary());

    static AgentRow Session(string id) => AgentRow.FromServer(new RepoSessionDto {
        SessionId = id, Status = "ended", AccessLevel = "full",
        StartedAt = DateTimeOffset.UtcNow, LastActivityAt = DateTimeOffset.UtcNow,
        Repo = new() { Hash = ServerHash },
    }, hostedPresentation: false);

    static Func<string, CancellationToken, Task<RailWorkItems?>> Reads(RailWorkItems read) =>
        (_, _) => Task.FromResult<RailWorkItems?>(read);

    static async Task<SessionRailViewModel> PickKcapAsync(FakeAgentDirectory directory, RailWorkItems read) {
        var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System, readWorkItems: Reads(read));
        directory.Rows.AddOrUpdate([Session("build"), Session("prune")]);
        await rail.SetProjectCommand.Execute(Kcap);
        return rail;
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Picking_a_project_lists_its_work_items_first_and_the_rest_under_days() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var rail = await PickKcapAsync(directory, new RailWorkItems(true, [Approve]));
            await WorkspaceFixtures.WaitUntilAsync(() => rail.ShowsWorkItems, what: "the work items read");

            await Assert.That(rail.EffectiveGrouping).IsEqualTo(RailGrouping.WorkItem);
            await Assert.That(rail.FiltersDiffer).IsFalse();
            await Assert.That(rail.Groups.Select(g => (g.Label, g.IsWorkItem)))
                .IsEquivalentTo([("Approve requests from your phone", true), ("Today", false)], CollectionOrdering.Matching);
            await Assert.That(rail.Groups[0].PartsText).IsEqualTo("2/6");
            await Assert.That(rail.Groups[1].Sessions.Select(s => s.Id)).IsEquivalentTo(["prune"]);
            await Assert.That(rail.Groups[1].Sessions[0].ShowsRepo).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_work_item_expands_to_its_sessions_each_naming_its_part() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var rail = await PickKcapAsync(directory, new RailWorkItems(true, [Approve]));
            await WorkspaceFixtures.WaitUntilAsync(() => rail.ShowsWorkItems, what: "the work items read");
            var approve = rail.Groups[0];
            await Assert.That(approve.IsExpanded).IsFalse();

            await approve.ToggleCommand.Execute();

            await Assert.That(approve.IsExpanded).IsTrue();
            var build = approve.Sessions.Single();
            await Assert.That(build.Id).IsEqualTo("build");
            await Assert.That(build.PartLabel).IsEqualTo("List every decision waiting on you");
            await Assert.That(build.ShowsRepo).IsFalse();
        });
    }

    /// A suggestion marks a session under its day, and goes once the session leaves the suggested set; a session
    /// already in a work item never shows one.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_session_with_a_suggestion_says_so_under_its_day() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            var suggested = new System.Reactive.Subjects.BehaviorSubject<IReadOnlySet<string>>(new HashSet<string> { "build", "prune" });
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System,
                readWorkItems: Reads(new RailWorkItems(true, [Approve])), suggestedSessions: suggested);
            directory.Rows.AddOrUpdate([Session("build"), Session("prune")]);
            await rail.SetProjectCommand.Execute(Kcap);
            await WorkspaceFixtures.WaitUntilAsync(() => rail.ShowsWorkItems, what: "the work items read");
            await rail.Groups[0].ToggleCommand.Execute();

            var prune = rail.Groups[1].Sessions.Single();
            await Assert.That(prune.IsSuggested).IsTrue();
            await Assert.That(rail.Groups[0].Sessions.Single().IsSuggested).IsFalse();

            suggested.OnNext(new HashSet<string>());
            await Assert.That(prune.IsSuggested).IsFalse();
        });
    }

    /// The viewer sees none of its sessions, yet the work item is listed with the same progress.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_work_item_with_no_visible_session_is_still_listed_with_its_progress() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var rail = await PickKcapAsync(directory, new RailWorkItems(true, [Approve with { PartBySession = FrozenDictionary<string, string>.Empty }]));
            await WorkspaceFixtures.WaitUntilAsync(() => rail.ShowsWorkItems, what: "the work items read");

            await Assert.That(rail.Groups[0].PartsText).IsEqualTo("2/6");
            await Assert.That(rail.Groups[0].Sessions).IsEmpty();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task All_projects_groups_by_repository_and_offers_no_work_item() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var rail = await PickKcapAsync(directory, new RailWorkItems(true, [Approve]));
            await WorkspaceFixtures.WaitUntilAsync(() => rail.ShowsWorkItems, what: "the work items read");

            await rail.SetProjectCommand.Execute(null);

            await Assert.That(rail.EffectiveGrouping).IsEqualTo(RailGrouping.Repository);
            await Assert.That(rail.OffersWorkItem).IsFalse();
            await Assert.That(rail.Groups.Any(g => g.IsWorkItem)).IsFalse();
            await Assert.That(rail.FiltersDiffer).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_server_without_work_items_keeps_repository_grouping() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var rail = await PickKcapAsync(directory, RailWorkItems.Unavailable);
            await WorkspaceFixtures.WaitUntilAsync(() => !rail.OffersWorkItem, what: "the not-found read");

            await Assert.That(rail.EffectiveGrouping).IsEqualTo(RailGrouping.Repository);
            await Assert.That(rail.Groups.Any(g => g.IsWorkItem)).IsFalse();
        });
    }
}
