using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using DynamicData;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// The sidebar tab matches the rail's own rows in memory: no request, no debounce, no length floor.
public class SessionSearchSidebarTests {
    sealed class Harness : IDisposable {
        public FakeAgentDirectory Directory { get; } = new();
        public List<string> Opened { get; } = [];
        public List<SearchRequest> Requests { get; } = [];
        public HistoricalScheduler Scheduler { get; } = new();
        public SessionRailViewModel Rail { get; }
        public SessionSearchViewModel Vm { get; }
        public int Closed { get; private set; }

        public Harness() {
            Rail = new SessionRailViewModel(Directory, _ => { }, _ => { }, TimeProvider.System, openServerSession: Opened.Add);
            Vm = new SessionSearchViewModel(
                (request, _) => {
                    Requests.Add(request);
                    return Task.FromResult(new SearchFetch(ServerReadOutcome.Ok, new()));
                },
                new Subject<SessionIdentity>(), new Subject<string>(), new FakeTimeProvider(), Scheduler,
                (_, _, _) => Task.FromResult(SearchOpenOutcome.Opened),
                sidebar: () => Rail.Groups.SelectMany(group => group.Sessions));
            Vm.Closed.Subscribe(_ => Closed++);
        }

        public void Add(string id, string title) => Directory.Rows.AddOrUpdate(RemoteHost.ServerRow(id) with { Title = title });

        public string[] Titles => [.. Vm.Hits.Select(hit => hit.TitleText)];

        public void Dispose() {
            Vm.Dispose();
            Rail.Dispose();
            Directory.Dispose();
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Lists_every_sidebar_session_until_a_key_narrows_it_by_title_ignoring_case() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            h.Add("a", "Fix Login timeout");
            h.Add("b", "Add rail filter");

            h.Vm.Refresh();
            var all = h.Titles;
            h.Vm.Query = "l";
            var one = h.Titles;
            h.Vm.Query = " LOGIN ";

            await Assert.That(all).IsEquivalentTo(["Fix Login timeout", "Add rail filter"]);
            await Assert.That(one).IsEquivalentTo(["Fix Login timeout", "Add rail filter"]);
            await Assert.That(h.Titles).IsEquivalentTo(["Fix Login timeout"]);
            await Assert.That(h.Vm.Hits[0].Title).IsEquivalentTo(new[] {
                new SnippetRun("Fix ", false), new SnippetRun("Login", true), new SnippetRun(" timeout", false)
            });
            h.Scheduler.AdvanceBy(TimeSpan.FromSeconds(1));
            await Assert.That(h.Requests).IsEmpty();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_miss_says_so_and_another_tab_asks_the_server() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            h.Add("a", "Fix login");

            h.Vm.Query = "zzz";
            var message = h.Vm.ListMessage;
            h.Vm.NextTab();

            await Assert.That(message).IsEqualTo("No matching sessions in the sidebar");
            await Assert.That(h.Vm.Tab).IsEqualTo(SearchMode.Sessions);
            await Assert.That(h.Requests).IsEquivalentTo([new SearchRequest("zzz", SearchMode.Sessions)]);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Opening_a_hit_opens_its_rail_row_and_closes_the_page() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            h.Add("a", "Fix login");
            h.Vm.Query = "login";

            await h.Vm.OpenHighlighted();

            await Assert.That(h.Opened).IsEquivalentTo(["a"]);
            await Assert.That(h.Closed).IsEqualTo(1);
        });
    }

    [Test]
    public async Task Tab_cycles_through_the_sidebar_and_both_server_tabs() {
        using var vm = new SessionSearchViewModel(
            (_, _) => Task.FromResult(new SearchFetch(ServerReadOutcome.Ok, new())),
            new Subject<SessionIdentity>(), new Subject<string>(), new FakeTimeProvider(), new HistoricalScheduler(),
            (_, _, _) => Task.FromResult(SearchOpenOutcome.Opened));
        var seen = new List<SearchMode> { vm.Tab };
        for (var i = 0; i < 3; i++) { vm.NextTab(); seen.Add(vm.Tab); }
        vm.PreviousTab();

        await Assert.That(string.Join(",", seen)).IsEqualTo("Sidebar,Sessions,Transcripts,Sidebar");
        await Assert.That(vm.Tab).IsEqualTo(SearchMode.Transcripts);
    }
}
