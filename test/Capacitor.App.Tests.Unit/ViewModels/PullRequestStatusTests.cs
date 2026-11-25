using Capacitor.Cli.Core.PullRequests;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit.ViewModels;

public class PullRequestStatusTests {
    [Test]
    public async Task Preview_checks_review_and_lifecycle_preserve_unknowns_and_independent_outcomes() {
        var overview = new PullRequestOverviewDto {
            Lifecycle = "open", Mergeable = false, ReviewDecision = "review_required",
            Checks = new() { Availability = new() { Status = "ready" }, Rollup = "pending",
                Counts = new() { ["pending"] = new() { Kind = "lower_bound", Value = 2 } } },
        };
        var pending = PullRequestEvidence.From(overview, stale: true);

        await Assert.That(PullRequestTones.LifecycleStatus(overview).PreviewGlyph).IsEqualTo(StatusGlyphKind.OpenPullRequest);
        await Assert.That(pending.Checks.PreviewText).IsEqualTo("Checks pending");
        await Assert.That(pending.Checks.PreviewGlyph).IsEqualTo(StatusGlyphKind.Pending);
        await Assert.That(pending.Checks.RequiresAttention).IsFalse();
        await Assert.That(pending.Review.RequiresAttention).IsTrue();
        await Assert.That(pending.Conflicts.RequiresAttention).IsTrue();
        await Assert.That(pending.Stale).IsTrue();

        var passing = PullRequestEvidence.From(overview with {
            Mergeable = null, ReviewDecision = null, Checks = overview.Checks with { Rollup = "success" },
        }, stale: false);
        await Assert.That(passing.Checks.PreviewText).IsEqualTo("Checks passing");
        await Assert.That(passing.Review.PreviewText).IsEqualTo("Review decision unknown");
        await Assert.That(passing.Incomplete).IsTrue();
        await Assert.That(passing.Signals.Any(status => status.PreviewText.Contains("Ready to merge", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Only_live_pull_requests_with_unknown_threads_make_a_rollup_incomplete() {
        var complete = new PullRequestOverviewDto {
            Lifecycle = "open", Mergeable = true, ReviewDecision = "approved",
            Checks = new() { Availability = new() { Status = "ready" }, Rollup = "success" },
            Threads = new() { Availability = new() { Status = "ready" }, Unresolved = new() { Kind = "exact", Value = 0 } },
        };
        var ready = RailItem(1, complete);
        var failed = RailItem(2, complete with { Checks = complete.Checks with { Rollup = "failure" } });
        await Assert.That(ready.IsIncomplete).IsFalse();
        foreach (var lifecycle in new[] { "open", "draft", "closed", "merged" }) {
            var item = RailItem(3, complete with { Lifecycle = lifecycle, Threads = null });
            var live = lifecycle is "open" or "draft";
            await Assert.That(item.IsIncomplete).IsEqualTo(live);
            var rollup = new RailPullRequestSummary([failed, item]);
            var labels = rollup.PreviewSignals.Select(status => status.Text).ToArray();
            await Assert.That(labels.Contains("Status incomplete")).IsEqualTo(live);
            await Assert.That(labels).Contains(live ? "1+ need attention" : "1 need attention");
        }
    }

    [Test]
    public async Task Single_live_pull_request_keeps_lifecycle_pending_checks_and_incomplete_evidence_visible() {
        var overview = new PullRequestOverviewDto {
            Lifecycle = "open", Mergeable = null, ReviewDecision = "approved",
            Checks = new() { Availability = new() { Status = "ready" }, Rollup = "success" },
            Threads = new() { Availability = new() { Status = "ready" }, Unresolved = new() { Kind = "exact", Value = 0 } },
        };
        var summary = new RailPullRequestSummary([RailItem(1, overview)]);
        await Assert.That(summary.IncompleteCoverage).IsFalse();
        await Assert.That(summary.PreviewSignals.Select(status => status.PreviewText)).IsEquivalentTo(new[] { "Open", "Status incomplete" });

        var pending = new RailPullRequestSummary([RailItem(1, overview with { Checks = overview.Checks with { Rollup = "pending" } })]);
        await Assert.That(pending.PreviewSignals.Single(status => status.PreviewText == "Checks pending").PreviewGlyph)
            .IsEqualTo(StatusGlyphKind.Pending);
    }

    [Test]
    public async Task Stale_single_pull_request_marks_age_separately_from_an_actionable_failure() {
        var overview = new PullRequestOverviewDto {
            Lifecycle = "open", Checks = new() { Availability = new() { Status = "ready" }, Rollup = "failure" },
        };
        var item = RailItem(1, overview, stale: true);
        var summary = new RailPullRequestSummary([item]);
        await Assert.That(summary.PreviewSignals.Single(s => s.PreviewText == "Earlier snapshot").PreviewGlyph)
            .IsEqualTo(StatusGlyphKind.Stale);
        await Assert.That(item.PickerTip.PullRequestSignals.Single(s => s.PreviewText == "Earlier snapshot").PreviewGlyph)
            .IsEqualTo(StatusGlyphKind.Stale);
        await Assert.That(summary.PreviewSignals.Single(s => s.PreviewText == "Checks failed").RequiresAttention).IsTrue();
    }

    static RailPullRequestMenuItem RailItem(int number, PullRequestOverviewDto overview, bool stale = false) =>
        new(PullRequestWire.Subject(FakePullRequestSource.Link(number)), null, PullRequestTones.LifecycleStatus(overview).Text) {
            Evidence = PullRequestEvidence.From(overview, stale),
        };

    [Test]
    public async Task A_pending_check_pulses() {
        var status = new PullRequestStatus("Checks pending", "pending");
        await Assert.That(status.IsPulsing).IsTrue();
        await Assert.That(status.IsWarning).IsFalse();
    }

    [Test]
    public async Task A_conflict_is_the_warning_colour() {
        var status = new PullRequestStatus("Merge conflicts", "conflict");
        await Assert.That(status.IsWarning).IsTrue();
        await Assert.That(status.IsPulsing).IsFalse();
    }

    /// A required review waits on people, not a pipeline, so it keeps the warning colour and does not pulse.
    [Test]
    public async Task A_required_review_is_the_warning_colour_and_still() {
        var status = new PullRequestStatus("Review required", "warning");
        await Assert.That(status.IsWarning).IsTrue();
        await Assert.That(status.IsPulsing).IsFalse();
        await Assert.That(status.Tip).Contains("required review");
    }

    [Test]
    public async Task A_status_tip_explains_when_detail_is_absent() {
        await Assert.That(new PullRequestStatus("Merged", "merged").Tip).Contains("merged");
        await Assert.That(new PullRequestStatus("Open", "open").Tip).Contains("open");
        await Assert.That(new PullRequestStatus("2 failed", "failure", "1 failed · 0 pending · 1 passed").Tip)
            .IsEqualTo("1 failed · 0 pending · 1 passed");
        await Assert.That(new PullRequestStatus("Checks passing", "success", "All checks have passed.").Tip)
            .IsEqualTo("All checks have passed.");
    }

    [Test]
    public async Task A_draft_is_still() {
        var status = new PullRequestStatus("Draft", "draft");
        await Assert.That(status.IsPulsing).IsFalse();
        await Assert.That(status.IsSuccess).IsFalse();
    }

    [Test]
    public async Task Success_failure_and_merged_keep_their_colours() {
        await Assert.That(new PullRequestStatus("", "success").IsSuccess).IsTrue();
        await Assert.That(new PullRequestStatus("", "merged").IsSuccess).IsTrue();
        await Assert.That(new PullRequestStatus("", "open").IsSuccess).IsFalse();
        await Assert.That(new PullRequestStatus("", "failure").IsDanger).IsTrue();
        await Assert.That(new PullRequestStatus("", "closed").IsDanger).IsTrue();
    }

    [Test]
    public async Task Checks_and_reviews_use_filled_discs_git_lifecycle_keeps_glyphs() {
        await Assert.That(new PullRequestStatus("", "success").UsesDiscIcon).IsTrue();
        await Assert.That(new PullRequestStatus("", "failure").UsesDiscIcon).IsTrue();
        await Assert.That(new PullRequestStatus("", "pending").UsesDiscIcon).IsTrue();
        await Assert.That(new PullRequestStatus("", "pending").IsPulsing).IsTrue();
        await Assert.That(new PullRequestStatus("", "warning").UsesDiscIcon).IsTrue();
        await Assert.That(new PullRequestStatus("", "warning").IsWarning).IsTrue();
        await Assert.That(new PullRequestStatus("", "merged").UsesGlyphIcon).IsTrue();
        await Assert.That(new PullRequestStatus("", "open").UsesGlyphIcon).IsTrue();
        await Assert.That(new PullRequestStatus("", "conflict").UsesGlyphIcon).IsTrue();
    }
}
