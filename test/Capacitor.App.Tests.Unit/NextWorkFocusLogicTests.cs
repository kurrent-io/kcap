using Capacitor.App.ViewModels.NextWork;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

/// The desktop band groups rows exactly as the web Home band does; these pin the rules that decide where a row lands.
public class NextWorkFocusLogicTests {
    static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    static NextWorkCandidateDto Row(string key, string kind, string label, int tier, int rank, double days = 0, int seen = 0,
            List<NextWorkEvidenceDto>? evidence = null, bool bug = false) =>
        new(key, kind, key[(key.IndexOf(':') + 1)..], label, null, "h1", "Arm", tier, "kept", null, rank, label,
            new(0, null, bug, seen, days), evidence ?? []);

    static NextWorkSnapshot Snapshot(params NextWorkCandidateDto[] rows) =>
        NextWorkSnapshot.From(new(Now, false, false, null, [.. rows]));

    [Test]
    public async Task Someone_waiting_leads_and_three_picks_follow_then_the_rest() {
        var focus = NextWorkFocusLogic.Build(Snapshot(
            Row("pr:1", "pr", "#1 Review me", 1, 1),
            Row("wi:a", "work_item", "A-1 First", 3, 2, evidence: [new("tracker_state", "linear", "state Todo (unstarted), priority 3")]),
            Row("wi:b", "work_item", "B-2 Second", 3, 3, evidence: [new("tracker_state", "linear", "state Todo (unstarted), priority 3")]),
            Row("wi:c", "work_item", "C-3 Third", 3, 4, evidence: [new("tracker_state", "linear", "state Todo (unstarted), priority 3")]),
            Row("wi:d", "work_item", "D-4 Fourth", 3, 5, evidence: [new("tracker_state", "linear", "state Todo (unstarted), priority 3")])), Now);

        await Assert.That(focus.Waiting.Select(i => i.Row.TargetKey)).IsEquivalentTo(["pr:1"]);
        await Assert.That(focus.Picks.Select(i => i.Row.TargetKey)).IsEquivalentTo(["wi:a", "wi:b", "wi:c"]);
        await Assert.That(focus.More.Select(i => i.Row.TargetKey)).IsEquivalentTo(["wi:d"]);
    }

    [Test]
    public async Task An_assigned_bug_is_picked_before_the_engines_order() {
        var focus = NextWorkFocusLogic.Build(Snapshot(
            Row("wi:a", "work_item", "A-1 First", 3, 1, evidence: [new("tracker_state", "linear", "state Todo (unstarted), priority 3")]),
            Row("wi:b", "work_item", "B-2 Second", 3, 2, evidence: [new("tracker_state", "linear", "state Todo (unstarted), priority 3")]),
            Row("wi:c", "work_item", "C-3 Third", 3, 3, evidence: [new("tracker_state", "linear", "state Todo (unstarted), priority 3")]),
            Row("wi:z", "work_item", "Z-9 Mine", 3, 9, bug: true,
                evidence: [new("identity", "linear", "Z-9 assigned to you"), new("tracker_state", "linear", "state Todo (unstarted), priority 2")])), Now);

        await Assert.That(focus.Picks[0].Row.TargetKey).IsEqualTo("wi:z");
    }

    [Test]
    public async Task A_chore_is_a_note_and_an_old_loose_end_goes_quiet() {
        var focus = NextWorkFocusLogic.Build(Snapshot(
            Row("le:1", "loose_end", "File an issue for the flaky test", 3, 1, days: 1),
            Row("le:2", "loose_end", "Consider caching FX rates", 3, 2, days: NextWorkFocusLogic.QuietLooseEndDays),
            Row("wi:q", "work_item", "Untracked idea", 3, 3, days: NextWorkFocusLogic.QuietDays)), Now);

        await Assert.That(focus.Notes.Select(i => i.Row.TargetKey)).IsEquivalentTo(["le:1"]);
        await Assert.That(focus.Quiet.Select(i => i.Row.TargetKey)).IsEquivalentTo(["le:2", "wi:q"]);
    }

    [Test]
    public async Task A_loose_end_naming_an_items_key_folds_under_that_item() {
        var focus = NextWorkFocusLogic.Build(Snapshot(
            Row("wi:a", "work_item", "PAY-214 Gateway drops requests", 2, 1),
            Row("le:9", "loose_end", "Add jitter to PAY-214's retry backoff", 3, 2)), Now);

        var item = focus.Picks.Single();
        await Assert.That(item.Row.TargetKey).IsEqualTo("wi:a");
        await Assert.That(item.Folded.Select(f => f.TargetKey)).IsEquivalentTo(["le:9"]);
    }

    [Test]
    public async Task Near_identical_notes_count_as_one_that_came_up_again_and_names_keep_them_apart() {
        await Assert.That(NextWorkFocusLogic.Similar(
            "Add a regression test for the retry backoff jitter in the gateway client",
            "Add regression test for retry backoff jitter in gateway client")).IsTrue();
        await Assert.That(NextWorkFocusLogic.Similar(
            "Add a regression test for NextWorkBandTests covering the filters",
            "Add a regression test for HomePageTests covering the filters")).IsFalse();
    }
}
