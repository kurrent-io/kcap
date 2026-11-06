using Capacitor.App.Services;
using System.Reactive.Threading.Tasks;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.NextWork;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

static partial class DocsShotScenes {
    const string PaymentsHash = "9f2c41d07a6be3a1";

    static string Stamp(TimeSpan ago) => (Now - ago).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    static NextWorkCandidateDto Row(string key, string kind, string label, string arm, int tier, int? rank, string because,
            NextWorkFacetsDto facets, List<NextWorkEvidenceDto> evidence, string? href = null, string? id = null) =>
        new(key, kind, id ?? key[(key.IndexOf(':') + 1)..], label, href, PaymentsHash, arm, tier, rank, because, facets, evidence,
            TrackerDependent: kind == "work_item", TrackerStateAsOf: kind == "work_item" ? Now.AddMinutes(-9) : null);

    static NextWorkFacetsDto Facets(double days, int? priority = null, bool bug = false, int seen = 0, int waiting = 0) =>
        new(waiting, priority, bug, seen, days);

    /// Maya's next work in the payments repository: a review someone waits on, her assigned gateway bug with a loose end
    /// under it, an unclaimed loose end, a backlog item, an interrupted session, a chore and a quiet note.
    public static NextWorkSnapshotDto NorthwindNextWork() => new(Now, Partial: false, Incomplete: false,
        new() { [PaymentsHash] = "northwind/payments-api" },
        [
            Row("pr:northwind/payments-api#482", "pr", "#482 Make gateway retries idempotent", "ReviewRequested", 1, 1,
                "Dana is waiting on your review", Facets(0.3, waiting: 1),
                [new("review_request", "github", $"Review requested from you at {Stamp(TimeSpan.FromHours(5))}"),
                 new("pr_state", "github", $"Opened by Dana Okafor at {Stamp(TimeSpan.FromHours(20))}; 4 commits, checks passing")],
                href: "https://github.com/northwind/payments-api/pull/482"),
            Row("wi:w1", "work_item", "PAY-214 Gateway drops requests on a 503", "FinishYours", 2, 2,
                "Assigned to you and in progress", Facets(0.8, priority: 2, bug: true),
                [new("identity", "linear", "PAY-214 assigned to you"),
                 new("tracker_state", "linear", "Linear PAY-214: state In Progress (started), priority 2"),
                 new("loose_end", "session", $"sighted 2 times, last at {Stamp(TimeSpan.FromHours(3))}", "Add jitter to the retry backoff",
                     LooseEndId: "le1", SessionId: "s-payments-1")],
                href: "/work-items/w1"),
            Row("le:le2", "loose_end", "Document the webhook replay window in the runbook", "LooseEnd", 3, 3,
                "Document the webhook replay window in the runbook", Facets(2.2, seen: 3),
                [new("loose_end", "session", $"sighted 3 times, last at {Stamp(TimeSpan.FromDays(2))}", null, LooseEndId: "le2", SessionId: "s-payments-0")]),
            Row("wi:w2", "work_item", "PAY-198 Split the refunds worker out of the API process", "BacklogPickup", 3, 4,
                "Next in your backlog, active 1 day ago", Facets(1.4, priority: 3),
                [new("tracker_state", "linear", "Linear PAY-198: state Todo (unstarted), priority 3")],
                href: "/work-items/w2"),
            Row("session:s-ledger", "session", "Migrate ledger exports to Parquet", "ResumeInterrupted", 2, 5,
                "Stopped mid-task when the machine slept", Facets(3.1),
                [new("session_state", "kcap", $"Interrupted at {Stamp(TimeSpan.FromDays(3))} with 2 tasks open")]),
            Row("le:le3", "loose_end", "File an issue for the flaky settlement reconciliation test", "LooseEnd", 3, 6,
                "File an issue for the flaky settlement reconciliation test", Facets(1.1, seen: 1),
                [new("loose_end", "session", $"sighted 1 time, last at {Stamp(TimeSpan.FromDays(1))}", null, LooseEndId: "le3", SessionId: "s-payments-0")]),
            Row("le:le4", "loose_end", "Consider caching FX rates for an hour", "LooseEnd", 3, 7,
                "Consider caching FX rates for an hour", Facets(19),
                [new("loose_end", "session", $"sighted 1 time, last at {Stamp(TimeSpan.FromDays(19))}", null, LooseEndId: "le4")]),
        ]);

    public static NextWorkLaunchPreparationDto NorthwindWorkOn() => new(null, "northwind/payments-api",
        [new("maya-mbp", PaymentsRepo, ["claude", "codex"], "maya"),
         new("maya-studio", "/home/maya/src/payments-api", ["claude"], "maya")],
        [],
        """
        Work on PAY-214 in northwind/payments-api: Gateway drops requests on a 503.

        Loose ends noted against it:
        - Add jitter to the retry backoff

        Close each loose end you finish with close_loose_end.
        """);

    /// The launcher with what to pick up next under the composer.
    public static Task<DocsShotScene> LauncherNextWorkAsync() => LauncherWithBandAsync((_, _, _) => Task.CompletedTask);

    /// The gateway bug pressed: its prompt in the composer, the payments checkout chosen, the row marked.
    public static Task<DocsShotScene> LauncherFilledFromNextWorkAsync() => LauncherWithBandAsync(async (band, api, home) => {
        api.Preparation = new(NextWorkReadKind.Ready, NorthwindWorkOn());
        var card = band.Cards.First(c => c.Key == "wi:w1");
        await card.FillCommand.Execute().ToTask();
        await WorkspaceFixtures.WaitUntilAsync(() => home.NextWorkSource is not null, what: "the composer fill");
    });

    static async Task<DocsShotScene> LauncherWithBandAsync(Func<NextWorkBandViewModel, FakeNextWorkApi, HomeViewModel, Task> arrange) {
        var api = new FakeNextWorkApi { Snapshot = new(NextWorkReadKind.Ready, NorthwindNextWork()), ServerUrl = ServerUrl };
        HomeViewModel? home = null;
        var band = new NextWorkBandViewModel(api, new FakeTimeProvider(Now), localDaemon: "maya-mbp",
            fill: f => home!.FillFromNextWorkAsync(f));
        var scene = await MainWindowAsync(openSession: false, nextWork: band, arrange: async vm => {
            home = vm.Home!;
            await home.SelectRepositoryAsync(PaymentsRepo);
            await band.RefreshAsync();
            await arrange(band, api, home);
        });
        scene.Window.Height = 1000;
        return scene;
    }
}
