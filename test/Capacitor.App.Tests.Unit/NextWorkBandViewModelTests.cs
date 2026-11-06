using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels.NextWork;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class NextWorkBandViewModelTests {
    static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    static NextWorkCandidateDto Row(string key, string kind, string label, int tier, int? rank, string outcome = "kept",
            string? reason = null, double days = 0) =>
        new(key, kind, key[(key.IndexOf(':') + 1)..], label, null, "h1", "Arm", tier, outcome, reason, rank, label,
            new(0, null, false, 1, days), []);

    static FakeNextWorkApi Api(params NextWorkCandidateDto[] rows) =>
        new() { Snapshot = new(NextWorkReadKind.Ready, new(Now, false, false, new() { ["h1"] = "northwind/payments-api" }, [.. rows])) };

    static NextWorkBandViewModel Band(FakeNextWorkApi api, NextWorkSeenStore? seen = null, Func<NextWorkFill, Task>? fill = null,
            Func<bool>? enabled = null, Func<string?>? vendor = null, Action<string>? notify = null) =>
        new(api, new FakeTimeProvider(Now), seen, notify: notify, localDaemon: "mbp", enabled: enabled, fill: fill, preferredVendor: vendor);

    static NextWorkLaunchPreparationDto Prep(params NextWorkLaunchCheckoutDto[] checkouts) =>
        new(null, "northwind/payments-api", [.. checkouts], [], "Work on A-1");

    [Test]
    public async Task A_server_without_the_engine_hides_the_band() {
        var band = Band(new FakeNextWorkApi());
        await band.RefreshAsync();
        await Assert.That(band.IsAvailable).IsFalse();
    }

    [Test]
    public async Task The_list_holds_who_waits_then_the_picks_folds_the_rest_of_the_work_and_counts_the_chores_for_the_dashboard() {
        var band = Band(Api(
            Row("pr:1", "pr", "#1 Review", 1, 1),
            Row("wi:a", "work_item", "A-1 Fix it", 2, 2),
            Row("wi:b", "work_item", "B-2 Fix that", 2, 3),
            Row("wi:c", "work_item", "C-3 Fix more", 2, 4),
            Row("wi:d", "work_item", "D-4 Fix again", 2, 5),
            Row("pr:9", "pr", "#9 Merged", 1, null, "disconfirmed", "pr_merged"),
            Row("le:n", "loose_end", "File an issue for the flake", 3, 6)));
        await band.RefreshAsync();

        await Assert.That(band.Cards.Select(c => c.Key)).IsEquivalentTo(["pr:1", "wi:a", "wi:b", "wi:c"]);
        await Assert.That(band.MoreCards.Select(c => c.Key)).IsEquivalentTo(["wi:d"]);
        await Assert.That(band.MoreListVisible).IsFalse();
        await Assert.That(band.MoreToggleText).IsEqualTo("More to do (1)");
        await Assert.That(band.ElsewhereText).IsEqualTo("1 more on the dashboard");
        await Assert.That(band.Cards[1].Meta).IsEqualTo("payments-api · today");
        await Assert.That(band.Cards[1].KindLabel).IsEqualTo("Work item");
        await Assert.That(band.Cards[0].KindLabel).IsEqualTo("Pull request");
    }

    [Test]
    public async Task A_refresh_while_pinned_waits_for_release() {
        var api = Api(Row("wi:a", "work_item", "A-1 Fix it", 2, 1));
        var band = Band(api);
        await band.RefreshAsync();
        band.Pin();
        api.Snapshot = new(NextWorkReadKind.Ready, new(Now, false, false, null, [Row("wi:b", "work_item", "B-2 Other", 2, 1)]));
        await band.RefreshAsync();
        await Assert.That(band.Cards.Single().Key).IsEqualTo("wi:a");

        band.Release();
        await Assert.That(band.Cards.Single().Key).IsEqualTo("wi:b");
    }

    [Test]
    public async Task Dismiss_and_mark_done_reach_the_server_and_reload() {
        var api = Api(Row("le:x", "loose_end", "Document the replay window", 3, 1));
        var band = Band(api);
        await band.RefreshAsync();
        var card = band.Cards.Single();
        var reads = api.SnapshotReads;

        band.CloseLooseEnd(card.LooseEndId!);
        await WorkspaceFixtures.WaitUntilAsync(() => api.SnapshotReads > reads, what: "the reload after closing");
        band.Dismiss(card);
        await WorkspaceFixtures.WaitUntilAsync(() => api.SnapshotReads > reads + 1, what: "the reload after dismissing");

        await Assert.That(api.Calls).IsEquivalentTo(["close x", "dismiss le:x h1"]);
    }

    [Test]
    public async Task A_row_not_shown_on_the_previous_visit_is_new() {
        using var tmp = new TempDir();
        var store = new AppStateStore(tmp.PathTo("app-state.json"));
        var seen = new NextWorkSeenStore(store, "https://kcap.test");
        await seen.SaveAsync(["wi:a"]);
        var band = Band(Api(Row("wi:a", "work_item", "A-1 Old", 2, 1), Row("wi:b", "work_item", "B-2 Fresh", 2, 2)), seen);
        await band.RefreshAsync();

        var cards = band.Cards.ToDictionary(c => c.Key, c => c.IsNew);
        await Assert.That(cards["wi:a"]).IsFalse();
        await Assert.That(cards["wi:b"]).IsTrue();
    }

    [Test]
    public async Task Only_work_items_and_loose_ends_with_a_repository_offer_work_on() {
        var band = Band(Api(Row("pr:1", "pr", "#1 Review", 1, 1), Row("wi:a", "work_item", "A-1 Fix", 2, 2)));
        await band.RefreshAsync();
        var cards = band.Cards.ToDictionary(c => c.Key, c => c.CanWorkOn);
        await Assert.That(cards["pr:1"]).IsFalse();
        await Assert.That(cards["wi:a"]).IsTrue();
    }

    [Test]
    public async Task The_switch_off_hides_the_band_without_asking_the_server() {
        var api = Api(Row("wi:a", "work_item", "A-1 Fix it", 2, 1));
        var band = Band(api, enabled: () => false);
        await band.RefreshAsync();

        await Assert.That(band.IsAvailable).IsFalse();
        await Assert.That(api.SnapshotReads).IsEqualTo(0);
    }

    [Test]
    public async Task Pressing_a_work_item_fills_its_title_with_the_servers_prompt_as_context_in_this_machines_checkout() {
        var api = Api(Row("wi:a", "work_item", "A-1 Fix it", 2, 1));
        api.Preparation = new(NextWorkReadKind.Ready, Prep(new NextWorkLaunchCheckoutDto("studio", "/srv/p", ["claude"], "u"),
            new NextWorkLaunchCheckoutDto("mbp", "/Users/m/p", ["claude"], "u")));
        NextWorkFill? filled = null;
        var band = Band(api, fill: f => { filled = f; return Task.CompletedTask; });
        await band.RefreshAsync();
        var card = band.Cards.Single();

        await card.FillCommand.Execute().ToTask();

        await Assert.That(filled).IsEqualTo(new NextWorkFill("wi:a", "A-1 Fix it", "A-1 Fix it", "mbp", true, "/Users/m/p", "Work on A-1"));
        await Assert.That(filled!.ContextSummary).IsNull();
        await Assert.That(card.IsFilled).IsTrue();
    }

    [Test]
    public async Task Pressing_a_review_fills_a_goal_naming_the_pull_request() {
        var api = new FakeNextWorkApi { Snapshot = new(NextWorkReadKind.Ready, new(Now, false, false, null,
            [new("pr:9", "pr", "9", "#9 Retry", "https://github.com/o/r/pull/9", "h1", "ReviewRequested", 1, "kept", null, 1, "Dana waits", new(1, null, false, 0, 0), [])])) };
        NextWorkFill? filled = null;
        var band = Band(api, fill: f => { filled = f; return Task.CompletedTask; });
        await band.RefreshAsync();

        await band.Cards.Single().FillCommand.Execute().ToTask();

        await Assert.That(filled!.Goal).IsEqualTo("Review pull request #9 Retry (https://github.com/o/r/pull/9).");
        await Assert.That(api.Calls).DoesNotContain("prepare pr:9");
    }

    [Test]
    public async Task Start_now_launches_in_this_machines_checkout_with_the_composers_harness() {
        var api = Api(Row("wi:a", "work_item", "A-1 Fix it", 2, 1));
        api.Preparation = new(NextWorkReadKind.Ready, Prep(new NextWorkLaunchCheckoutDto("mbp", "/Users/m/p", ["claude", "codex"], "u")));
        api.Launch = new(NextWorkReadKind.Ready, new("requested", "ab12cd34", null, []));
        var band = Band(api, vendor: () => "codex");
        await band.RefreshAsync();

        await band.Cards.Single().StartCommand.Execute().ToTask();

        await Assert.That(api.Launches.Single()).IsEqualTo(new NextWorkLaunchRequestDto("wi:a", "mbp", "/Users/m/p", "codex", null, "u"));
    }

    [Test]
    public async Task Start_now_without_a_checkout_here_fills_the_composer_instead() {
        var api = Api(Row("wi:a", "work_item", "A-1 Fix it", 2, 1));
        api.Preparation = new(NextWorkReadKind.Ready, Prep(new NextWorkLaunchCheckoutDto("studio", "/srv/p", ["claude"], "u")));
        NextWorkFill? filled = null;
        var band = Band(api, fill: f => { filled = f; return Task.CompletedTask; });
        await band.RefreshAsync();

        await band.Cards.Single().StartCommand.Execute().ToTask();

        await Assert.That(api.Launches).IsEmpty();
        await Assert.That(filled!.Machine).IsEqualTo("studio");
        await Assert.That(filled.MachineIsLocal).IsFalse();
    }

    [Test]
    public async Task A_refused_start_says_why() {
        var api = Api(Row("wi:a", "work_item", "A-1 Fix it", 2, 1));
        api.Preparation = new(NextWorkReadKind.Ready, Prep(new NextWorkLaunchCheckoutDto("mbp", "/Users/m/p", ["claude"], "u")));
        api.Launch = new(NextWorkReadKind.Ready, new("refused", null, "already_in_progress", []));
        string? said = null;
        var band = Band(api, notify: m => said = m);
        await band.RefreshAsync();

        await band.Cards.Single().StartCommand.Execute().ToTask();

        await Assert.That(said).IsEqualTo("This work is already reserved or in progress.");
    }

    [Test]
    public async Task With_no_checkout_anywhere_the_prompt_still_fills_the_composer() {
        var api = Api(Row("le:x", "loose_end", "Document the replay window", 3, 1));
        api.Preparation = new(NextWorkReadKind.Ready, new("no_matching_daemon", "o/r", [], [], "Close the replay loose end"));
        NextWorkFill? filled = null;
        var band = Band(api, fill: f => { filled = f; return Task.CompletedTask; });
        await band.RefreshAsync();

        await band.Cards.Single().FillCommand.Execute().ToTask();

        await Assert.That(filled!.Goal).IsEqualTo("Document the replay window");
        await Assert.That(filled.Context).IsEqualTo("Close the replay loose end");
        await Assert.That(filled.ContextSummary).IsEqualTo("The agent also gets the session this loose end was noted in.");
        await Assert.That(filled.RepoPath).IsNull();
    }

    [Test]
    public async Task An_unreadable_prompt_falls_back_to_the_label_without_the_bands_facts() {
        var api = Api(Row("le:x", "loose_end", "Document the replay window", 3, 1));
        NextWorkFill? filled = null;
        var band = Band(api, fill: f => { filled = f; return Task.CompletedTask; });
        await band.RefreshAsync();

        await band.Cards.Single().FillCommand.Execute().ToTask();

        await Assert.That(filled!.Goal).IsEqualTo("Document the replay window");
    }

    [Test]
    public async Task An_item_shows_the_loose_end_noted_against_it_and_how_many_more() {
        var api = new FakeNextWorkApi { Snapshot = new(NextWorkReadKind.Ready, new(Now, false, false, null, [
            new("wi:a", "work_item", "a", "A-1 Fix it", null, "h1", "FinishYours", 2, "kept", null, 1, "Assigned to you", new(0, null, false, 0, 0), [
                new("loose_end", "session", "sighted once", "Add jitter to the retry backoff", LooseEndId: "le1"),
                new("loose_end", "session", "sighted once", "Cover the 503 path in the gateway tests", LooseEndId: "le2")]),
        ])) };
        var band = Band(api);
        await band.RefreshAsync();

        await Assert.That(band.Cards.Single().Context).IsEqualTo("Add jitter to the retry backoff · +1 more");
        await Assert.That(band.Cards.Single().ContextSummary).IsEqualTo("The agent also gets the 2 loose ends noted against it and the sessions they came from.");
    }

    [Test]
    public async Task An_opened_more_list_stays_open_across_a_refresh() {
        var api = Api(Row("wi:a", "work_item", "A-1", 2, 1), Row("wi:b", "work_item", "B-2", 2, 2), Row("wi:c", "work_item", "C-3", 2, 3),
            Row("wi:d", "work_item", "D-4", 2, 4));
        var band = Band(api);
        await band.RefreshAsync();
        await band.ToggleMoreCommand.Execute().ToTask();
        await band.RefreshAsync();

        await Assert.That(band.MoreListVisible).IsTrue();
    }

    [Test]
    public async Task Snooze_holds_a_loose_end_until_nine_tomorrow() {
        var api = Api(Row("le:x", "loose_end", "Document the replay window", 3, 1));
        var band = Band(api);
        await band.RefreshAsync();
        var reads = api.SnapshotReads;

        await band.Cards.Single().SnoozeCommand.Execute().ToTask();
        await WorkspaceFixtures.WaitUntilAsync(() => api.SnapshotReads > reads, what: "the reload after snoozing");

        await Assert.That(api.Calls).IsEquivalentTo(["defer x 2026-11-07T09:00:00.0000000+00:00"]);
    }
}
