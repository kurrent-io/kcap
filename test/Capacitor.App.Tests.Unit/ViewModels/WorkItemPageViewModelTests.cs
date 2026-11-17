using System.Collections.Frozen;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;
using DynamicData;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit.ViewModels;

public class WorkItemPageViewModelTests {
    const string ListEvery = "List every decision waiting on you";

    static WorkItemPartDetailDto Part(string id, string title, string status, List<string>? sessions = null,
            List<WorkItemLinkDto>? links = null, List<WorkItemRefDto>? waitsOn = null) =>
        new() { WorkItemId = id, Title = title, Status = status, SessionIds = sessions ?? [], Links = links ?? [], WaitsOn = waitsOn ?? [] };

    static WorkItemPartsDto Approve() => new() {
        WorkItem = new() { WorkItemId = "approve", Title = "Approve requests from your phone" },
        Goal = "See and answer every permission from your phone.",
        Status = "in_progress",
        Links = [new() { ShortKey = "AI-5190", State = "In Progress" }, new() { ShortKey = "mention", LinkClass = "reference" }],
        Parts = [
            Part("deny", "Send a deny reason back to the agent", "in_progress", ["s-deny"], [new() { ShortKey = "#2914" }]),
            Part("list", ListEvery, "in_review", ["s-build", "s-plan"], [new() { ShortKey = "#2915" }]),
            Part("lock", "Approve from the lock screen", "blocked", waitsOn: [new() { WorkItemId = "list", Title = ListEvery }]),
            Part("rules", "Manage allow rules from any client", "open"),
            Part("shape", "Shape the request", "done", links: [new() { ShortKey = "#2900" }]),
            Part("hub", "Push requests over the hub", "done", links: [new() { ShortKey = "#2901" }]),
        ],
        SessionIds = ["s-onitem"],
        LooseEnds = [new() { LooseEndId = "restart", Text = "Handle approvals across a daemon restart" }],
    };

    static AgentRow Session(string id, string title) => AgentRow.FromServer(new RepoSessionDto {
        SessionId = id, Title = title, Status = "ended", AccessLevel = "full",
        StartedAt = DateTimeOffset.UtcNow, LastActivityAt = DateTimeOffset.UtcNow,
    }, hostedPresentation: false);

    static async Task<WorkItemPageViewModel> OpenAsync(FakeAgentDirectory directory, FakeWorkItemPageApi pages, ISet<string>? flipped = null,
            IObservable<IReadOnlySet<string>>? needsYou = null) {
        var page = new WorkItemPageViewModel("approve", pages, directory, TimeProvider.System,
            flipped ?? new HashSet<string>(), needsYou: needsYou);
        await WorkspaceFixtures.WaitUntilAsync(() => page.Groups.Count > 0, what: "the parts read");
        return page;
    }

    static WorkItemPagePart PartTitled(WorkItemPageViewModel page, string title) =>
        page.Groups.SelectMany(g => g.Parts).Single(p => p.Title == title);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Parts_are_grouped_by_status_with_the_server_s_counts_and_progress() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            directory.Rows.AddOrUpdate(Session("s-build", "Build the waiting list"));
            using var page = await OpenAsync(directory, new FakeWorkItemPageApi { Read = Approve() });

            await Assert.That(page.ProgressText).IsEqualTo("2 of 6 parts done");
            await Assert.That(page.Links.Select(l => l.Label)).IsEquivalentTo(["AI-5190 In Progress"]);
            await Assert.That(page.Groups.Select(g => (g.Label, g.Count, g.IsExpanded))).IsEquivalentTo([
                ("In progress", 1, true), ("In review", 1, true), ("Blocked", 1, true), ("Open", 1, true), ("Done", 2, false),
                ("On the work item", 2, true),
            ], CollectionOrdering.Matching);

            var list = PartTitled(page, ListEvery);
            await Assert.That(list.SessionCountText).IsEqualTo("2 sessions");
            await Assert.That(list.Sessions.Select(s => s.Title)).IsEquivalentTo(["Build the waiting list", "Session s-plan"], CollectionOrdering.Matching);
            var lockScreen = PartTitled(page, "Approve from the lock screen");
            await Assert.That(lockScreen.Detail).IsEqualTo($"Waits on {ListEvery}");
            await Assert.That(lockScreen.DetailIsWaiting).IsTrue();
            await Assert.That(PartTitled(page, "Manage allow rules from any client").Detail).IsEqualTo(WorkItemPageViewModel.NoLinks);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_part_shows_needs_you_while_one_of_its_sessions_waits_on_the_viewer() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            directory.Rows.AddOrUpdate(Session("s-deny", "Add the deny reason"));
            using var waiting = new BehaviorSubject<IReadOnlySet<string>>(FrozenSet<string>.Empty);
            using var page = await OpenAsync(directory, new FakeWorkItemPageApi { Read = Approve() }, needsYou: waiting);
            await Assert.That(PartTitled(page, "Send a deny reason back to the agent").NeedsYou).IsFalse();

            waiting.OnNext(new HashSet<string> { "s-deny" });

            await Assert.That(PartTitled(page, "Send a deny reason back to the agent").NeedsYou).IsTrue();
            await Assert.That(page.Groups[0].Status).IsEqualTo(WorkStatus.InProgress);
            await Assert.That(PartTitled(page, ListEvery).NeedsYou).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Make_part_turns_a_loose_end_into_an_open_part() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var page = await OpenAsync(directory, new FakeWorkItemPageApi { Read = Approve() });
            var looseEnd = page.Groups[^1].LooseEnds.Single();

            await page.MakePartCommand.Execute(looseEnd);
            await WorkspaceFixtures.WaitUntilAsync(() => page.Groups.Any(g => g.Status == WorkStatus.Open && g.Count == 2), what: "the reread");

            await Assert.That(page.Groups.Single(g => g.Status == WorkStatus.Open).Parts.Select(p => p.Title))
                .Contains("Handle approvals across a daemon restart");
            await Assert.That(page.Groups[^1].LooseEnds).IsEmpty();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_status_set_by_hand_names_the_part_and_an_empty_choice_clears_it() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            var pages = new FakeWorkItemPageApi { Read = Approve() };
            using var page = await OpenAsync(directory, pages);
            var rules = PartTitled(page, "Manage allow rules from any client");

            await rules.SetStatusCommand.Execute("done");
            await rules.SetStatusCommand.Execute("");

            await Assert.That(pages.StatusesSet).IsEquivalentTo([("rules", "done"), ("rules", (string?)null)], CollectionOrdering.Matching);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_group_opened_against_its_default_stays_open_on_the_next_page() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            var flipped = new HashSet<string>();
            using (var first = await OpenAsync(directory, new FakeWorkItemPageApi { Read = Approve() }, flipped))
                await first.Groups.Single(g => g.Status == WorkStatus.Done).ToggleCommand.Execute();

            using var second = await OpenAsync(directory, new FakeWorkItemPageApi { Read = Approve() }, flipped);

            await Assert.That(second.Groups.Single(g => g.Status == WorkStatus.Done).IsExpanded).IsTrue();
        });
    }

    [Test]
    public async Task The_parts_read_binds_the_server_s_wire_names() {
        const string json = """
            {"work_item":{"work_item_id":"approve","title":"Approve"},"links":[{"kind":"issue","provider":"linear","value":"ai-5190",
             "short_key":"AI-5190","url":"https://linear.app/x","title":null,"state":"In Progress","link_class":"link","is_seed":true}],
             "goal":"g","status":"in_progress","session_ids":["s1"],"loose_ends":[{"loose_end_id":"le","text":"t"}],
             "parts":[{"work_item_id":"lock","title":"Lock","links":[],"waits_on":[{"work_item_id":"list","title":"List"}],
             "status":"blocked","session_ids":["s2"]}]}
            """;

        var read = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.WorkItemPartsDto)!;

        await Assert.That(read.Links.Single().State).IsEqualTo("In Progress");
        await Assert.That(read.LooseEnds.Single().LooseEndId).IsEqualTo("le");
        await Assert.That(read.Parts.Single().WaitsOn.Single().Title).IsEqualTo("List");
        await Assert.That(read.Parts.Single().SessionIds).IsEquivalentTo(["s2"]);
    }
}
