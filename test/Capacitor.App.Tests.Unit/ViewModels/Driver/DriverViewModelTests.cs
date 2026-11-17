using System.Reactive.Concurrency;
using Capacitor.App.Services;
using Capacitor.App.Services.Driver;
using Capacitor.App.ViewModels.Driver;
using DynamicData;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit.ViewModels.Driver;

public class DriverViewModelTests {
    static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    sealed class FakeDriverApi(NextWorkRead<DriverWorkItemsDto> read) : IDriverApi {
        public Task<NextWorkRead<DriverWorkItemsDto>> WorkItemsAsync(CancellationToken ct) => Task.FromResult(read);
    }

    static AgentRow Live(string id, string sessionId, bool? awaitingInput, string? title = null) => new(
        Key: $"local:{id}", Origin: AgentOrigin.Local, Id: id, SessionId: sessionId, Kind: "agent", Vendor: "claude", Status: "Running",
        CreatedAt: DateTime.UnixEpoch, RepoPath: null, Title: title, Model: null, RequesterDisplay: null,
        WorktreePath: null, WorkLocation: null, BorrowedFrom: null, MachineBadge: null,
        RepoGroupKey: "", RepoGroupLabel: "", CheckoutKey: "", CheckoutLabel: "", AwaitingInput: awaitingInput);

    static DriverWorkItemsDto.Part Part(string id, string status, string[]? sessions = null, string[]? waitsOn = null,
            DriverWorkItemsDto.Link[]? links = null) =>
        new(id, $"Part {id}", status, false, waitsOn ?? [], links ?? [],
            (sessions ?? []).Select(s => new DriverWorkItemsDto.Session(s, null, Now, null)).ToList());

    static DriverWorkItemsDto.WorkItem Item(string id, params DriverWorkItemsDto.Part[] parts) =>
        new(id, $"Item {id}", "Fix the Windows issues", new("p1", "kcap"), "in_progress", false, Now, [], [], parts);

    static DriverViewModel Driver(DriverWorkItemsDto data, SourceCache<AgentRow, string>? rows = null) =>
        new(new FakeDriverApi(new(NextWorkReadKind.Ready, data)), new FakeTimeProvider(Now), rows?.Connect(), scheduler: Scheduler.Immediate);

    [Test]
    public async Task A_part_takes_its_activity_from_the_live_sessions_on_it() {
        var rows = new SourceCache<AgentRow, string>(r => r.Key);
        rows.AddOrUpdate(Live("a1", "s1", awaitingInput: false));
        rows.AddOrUpdate(Live("a2", "s2", awaitingInput: true, title: "Batch G"));
        var driver = Driver(new([Item("w1", Part("g", "in_progress", ["s1"]), Part("h", "in_progress", ["s2"]), Part("i", "open"))]), rows);

        await driver.RefreshAsync();

        var parts = driver.WorkItems.Single().Parts.ToDictionary(p => p.WorkItemId);
        await Assert.That(parts["g"].Activity).IsEqualTo(DriverActivity.Working);
        await Assert.That(parts["h"].Activity).IsEqualTo(DriverActivity.WaitingOnYou);
        await Assert.That(parts["i"].Activity).IsEqualTo(DriverActivity.None);
        await Assert.That(driver.NeedsYou.Select(n => n.Kind)).Contains("Your turn");
    }

    [Test]
    public async Task A_part_in_review_waits_on_the_person_and_lifts_its_work_item_to_the_top() {
        var pr     = new DriverWorkItemsDto.Link("pr", "github", "kurrent-io/kcap#12", "kcap#12", "https://github.com/kurrent-io/kcap/pull/12", null, "open");
        var driver = Driver(new([Item("quiet", Part("a", "open")), Item("busy", Part("f", "in_review", links: [pr]))]));

        await driver.RefreshAsync();

        await Assert.That(driver.WorkItems[0].WorkItemId).IsEqualTo("busy");
        await Assert.That(driver.NeedsYou.Single().Kind).IsEqualTo("Review");
        await Assert.That(driver.NeedsYou.Single().Action).IsEqualTo("Open PR");
    }

    [Test]
    public async Task Parts_list_review_first_and_done_last_and_name_what_they_wait_on() {
        var driver = Driver(new([Item("w1", Part("done", "done"), Part("g", "blocked", waitsOn: ["f"]), Part("f", "in_review"))]));

        await driver.RefreshAsync();

        var parts = driver.WorkItems.Single().Parts;
        await Assert.That(string.Join(",", parts.Select(p => p.WorkItemId))).IsEqualTo("f,g,done");
        await Assert.That(parts[1].WaitsOnText).IsEqualTo("after Part f");
    }

    [Test]
    public async Task A_server_without_work_items_version_2_says_so() {
        var driver = new DriverViewModel(new FakeDriverApi(NextWorkRead<DriverWorkItemsDto>.Unavailable), new FakeTimeProvider(Now),
            scheduler: Scheduler.Immediate);

        await driver.RefreshAsync();

        await Assert.That(driver.Phase).IsEqualTo(DriverPhase.Unavailable);
        await Assert.That(driver.ShowsPhase).IsTrue();
    }
}
