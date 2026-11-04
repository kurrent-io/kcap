using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.RepoEvidence;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit;

public class WatchSecondaryPullRequestTests {
    const string CliRoot   = "/h/dev/cli-wt";
    const string OtherRoot = "/h/dev/other";

    static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    static string? FakeFindRoot(string dir) =>
        dir.StartsWith(CliRoot, StringComparison.Ordinal)   ? CliRoot
      : dir.StartsWith(OtherRoot, StringComparison.Ordinal) ? OtherRoot
      : null;

    static WatchState StateWithSecondaryRoot(params string[] moreRoots) {
        var state = new WatchState { SecondaryRoots = new SecondaryRepoRoots(FakeFindRoot, "/h/dev/server") };
        foreach (var root in new[] { CliRoot }.Concat(moreRoots)) {
            state.SecondaryRoots.OnLine("claude",
                $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Edit","input":{"file_path":"{{{root}}}/src/x.cs"}}]}}""");
        }
        return state;
    }

    /// The first probe waits for a root, then the next waits a minute after it.
    [Test]
    public async Task The_secondary_probe_is_due_only_once_a_root_is_known() {
        var now   = DateTimeOffset.UnixEpoch.AddDays(1);
        var empty = new WatchState { SecondaryRoots = new SecondaryRepoRoots(FakeFindRoot, "/h/dev/server") };
        var known = StateWithSecondaryRoot();

        await Assert.That(WatchCommand.SecondaryProbeDue(empty, now)).IsFalse();
        await Assert.That(WatchCommand.SecondaryProbeDue(known, now)).IsTrue();
        known.LastSecondaryProbe = now.AddSeconds(-30);
        await Assert.That(WatchCommand.SecondaryProbeDue(known, now)).IsFalse();
    }

    static RepositoryPayload CliPr(int number = 915, string host = "github.com") => new() {
        Host = host, Owner = "kurrent-io", RepoName = "kcap-cli", Branch = "feature", PrNumber = number, PrUrl = $"https://{host}/kurrent-io/kcap-cli/pull/{number}"
    };

    static Func<string, TimeSpan, Task<RepositoryPayload?>> Detects(RepositoryPayload? repo) => (_, _) => Task.FromResult(repo);

    static Task Link(WatchState state, Func<string, TimeSpan, Task<RepositoryPayload?>> detect, Func<RepositoryPayload, CancellationToken, Task<bool>> post,
        TimeSpan? budget = null, CancellationToken ct = default, Action? beat = null) =>
        WatchCommand.LinkSecondaryPullRequestsAsync(state, detect, post, budget ?? Budget, TimeProvider.System, ct, beat ?? (() => { }));

    // A root whose probe eats the whole pass must not shadow the roots after it every time.
    [Test]
    public async Task A_pass_that_runs_out_resumes_at_the_next_root() {
        var state  = StateWithSecondaryRoot(OtherRoot);
        var probed = new List<string>();
        var never  = new TaskCompletionSource<RepositoryPayload?>();

        Task<RepositoryPayload?> Detect(string root, TimeSpan _) {
            probed.Add(root);
            return probed.Count == 1 ? never.Task : Task.FromResult<RepositoryPayload?>(null);
        }

        await Link(state, Detect, (_, _) => Task.FromResult(true), budget: TimeSpan.FromMilliseconds(50));
        await Link(state, Detect, (_, _) => Task.FromResult(true));

        await Assert.That(probed).IsEquivalentTo([CliRoot, OtherRoot, CliRoot], CollectionOrdering.Matching);
    }

    // The first probe returns on its own, but only after the budget has run out, so the pass
    // ends between iterations: the root it never reached must be next, not the one after it.
    [Test]
    public async Task A_pass_that_runs_out_between_probes_resumes_at_the_root_it_skipped() {
        var state  = StateWithSecondaryRoot(OtherRoot);
        var probed = new List<string>();

        Task<RepositoryPayload?> Detect(string root, TimeSpan _) {
            probed.Add(root);
            if (probed.Count == 1) Thread.Sleep(100);
            return Task.FromResult<RepositoryPayload?>(null);
        }

        await Link(state, Detect, (_, _) => Task.FromResult(true), budget: TimeSpan.FromMilliseconds(20));
        await Link(state, Detect, (_, _) => Task.FromResult(true));

        await Assert.That(probed).IsEquivalentTo([CliRoot, OtherRoot, CliRoot], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_completed_pass_wraps_around_to_the_first_root() {
        var state  = StateWithSecondaryRoot(OtherRoot);
        var probed = new List<string>();

        for (var pass = 0; pass < 2; pass++) {
            await Link(state, (root, _) => { probed.Add(root); return Task.FromResult<RepositoryPayload?>(null); }, (_, _) => Task.FromResult(true));
        }

        await Assert.That(probed).IsEquivalentTo([CliRoot, OtherRoot, CliRoot, OtherRoot], CollectionOrdering.Matching);
    }

    [Test]
    public async Task The_heartbeat_is_touched_before_every_probe() {
        var state = StateWithSecondaryRoot(OtherRoot);
        var beats = 0;

        await Link(state, Detects(null), (_, _) => Task.FromResult(true), beat: () => beats++);

        await Assert.That(beats).IsEqualTo(2);
    }

    [Test]
    public async Task A_detected_pr_is_posted_once() {
        var state  = StateWithSecondaryRoot();
        var probed = new List<string>();
        var posted = new List<RepositoryPayload>();

        for (var pass = 0; pass < 2; pass++) {
            await Link(state,
                (root, _) => { probed.Add(root); return Task.FromResult<RepositoryPayload?>(CliPr()); },
                (pr, _) => { posted.Add(pr); return Task.FromResult(true); });
        }

        await Assert.That(probed).IsEquivalentTo([CliRoot, CliRoot]);
        await Assert.That(posted).Count().IsEqualTo(1);
        await Assert.That(posted[0].PrNumber).IsEqualTo(915);
    }

    [Test]
    public async Task A_failed_post_is_retried_on_the_next_pass() {
        var state = StateWithSecondaryRoot();
        var posts = 0;

        Task<bool> Post(RepositoryPayload _, CancellationToken __) => Task.FromResult(++posts == 2);

        await Link(state, Detects(CliPr()), Post);
        await Link(state, Detects(CliPr()), Post);
        await Link(state, Detects(CliPr()), Post);

        await Assert.That(posts).IsEqualTo(2);
    }

    [Test]
    public async Task A_checkout_without_a_pr_is_probed_again_but_never_posted() {
        var state  = StateWithSecondaryRoot();
        var probed = 0;
        var posted = 0;
        var noPr   = CliPr() with { PrNumber = null, PrUrl = null };

        for (var pass = 0; pass < 2; pass++) {
            await Link(state,
                (_, _) => { probed++; return Task.FromResult<RepositoryPayload?>(noPr); },
                (_, _) => { posted++; return Task.FromResult(true); });
        }

        await Assert.That(probed).IsEqualTo(2);
        await Assert.That(posted).IsEqualTo(0);
    }

    [Test]
    public async Task A_pr_on_a_non_github_host_is_not_posted() {
        var state  = StateWithSecondaryRoot();
        var posted = 0;

        await Link(state, Detects(CliPr(host: "gitlab.com")), (_, _) => { posted++; return Task.FromResult(true); });

        await Assert.That(posted).IsEqualTo(0);
    }

    // In a fork checkout origin names the fork while `gh pr view` resolves the PR in the base
    // repository, and the base repository is the one the PR must be linked under.
    [Test]
    public async Task A_fork_checkout_posts_the_pr_under_its_base_repository() {
        var state  = StateWithSecondaryRoot();
        var posted = new List<RepositoryPayload>();
        var fork   = CliPr() with { Owner = "someone", RepoName = "kcap-cli-fork" };

        await Link(state, Detects(fork), (pr, _) => { posted.Add(pr); return Task.FromResult(true); });

        await Assert.That(posted).Count().IsEqualTo(1);
        await Assert.That(posted[0].Owner).IsEqualTo("kurrent-io");
        await Assert.That(posted[0].RepoName).IsEqualTo("kcap-cli");
        await Assert.That(state.LinkedPullRequests).Contains(("kurrent-io", "kcap-cli", 915));
    }

    [Test]
    public async Task A_pr_whose_url_disagrees_with_its_number_is_not_posted() {
        var state  = StateWithSecondaryRoot();
        var posted = 0;

        await Link(state, Detects(CliPr() with { PrUrl = "https://github.com/kurrent-io/kcap-cli/pull/999" }), (_, _) => { posted++; return Task.FromResult(true); });

        await Assert.That(posted).IsEqualTo(0);
    }

    [Test]
    public async Task A_pr_without_a_parseable_url_is_not_posted() {
        var state  = StateWithSecondaryRoot();
        var posted = 0;

        await Link(state, Detects(CliPr() with { PrUrl = null }), (_, _) => { posted++; return Task.FromResult(true); });

        await Assert.That(posted).IsEqualTo(0);
    }

    [Test]
    public async Task A_github_enterprise_pr_is_not_posted() {
        var state  = StateWithSecondaryRoot();
        var posted = 0;

        await Link(state, Detects(CliPr(host: "ghe.example.com")), (_, _) => { posted++; return Task.FromResult(true); });

        await Assert.That(posted).IsEqualTo(0);
    }

    [Test]
    public async Task A_detection_still_running_at_cancellation_does_not_hold_the_pass() {
        var state = StateWithSecondaryRoot();
        var never = new TaskCompletionSource<RepositoryPayload?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Link(state, (_, _) => never.Task, (_, _) => Task.FromResult(true), ct: cts.Token).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task A_detection_still_running_at_the_budget_does_not_hold_the_pass() {
        var state = StateWithSecondaryRoot();
        var never = new TaskCompletionSource<RepositoryPayload?>();

        await Link(state, (_, _) => never.Task, (_, _) => Task.FromResult(true), budget: TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task A_negative_budget_probes_nothing() {
        var state  = StateWithSecondaryRoot();
        var probed = 0;

        await Link(state, (_, _) => { probed++; return Task.FromResult<RepositoryPayload?>(CliPr()); }, (_, _) => Task.FromResult(true),
            budget: TimeSpan.FromSeconds(-1));

        await Assert.That(probed).IsEqualTo(0);
    }

    [Test]
    public async Task A_new_pr_on_the_same_checkout_is_posted_too() {
        var state  = StateWithSecondaryRoot();
        var posted = new List<int>();

        Task<bool> Post(RepositoryPayload pr, CancellationToken _) { posted.Add(pr.PrNumber!.Value); return Task.FromResult(true); }

        await Link(state, Detects(CliPr(915)), Post);
        await Link(state, Detects(CliPr(916)), Post);

        await Assert.That(posted).IsEquivalentTo([915, 916]);
    }

    [Test]
    public async Task A_watcher_without_a_collector_probes_nothing() {
        var probed = 0;

        await Link(new WatchState(), (_, _) => { probed++; return Task.FromResult<RepositoryPayload?>(CliPr()); }, (_, _) => Task.FromResult(true));

        await Assert.That(probed).IsEqualTo(0);
    }

    [Test]
    public async Task Each_probe_is_handed_no_more_than_the_pass_budget() {
        var state   = StateWithSecondaryRoot();
        var budgets = new List<TimeSpan>();

        await Link(state, (_, budget) => { budgets.Add(budget); return Task.FromResult<RepositoryPayload?>(null); }, (_, _) => Task.FromResult(true),
            budget: TimeSpan.FromSeconds(3));

        await Assert.That(budgets).Count().IsEqualTo(1);
        await Assert.That(budgets[0]).IsLessThanOrEqualTo(TimeSpan.FromSeconds(3));
        await Assert.That(budgets[0]).IsGreaterThan(TimeSpan.Zero);
    }

    [Test]
    public async Task An_exhausted_budget_probes_nothing() {
        var state  = StateWithSecondaryRoot();
        var probed = 0;

        await Link(state, (_, _) => { probed++; return Task.FromResult<RepositoryPayload?>(CliPr()); }, (_, _) => Task.FromResult(true),
            budget: TimeSpan.Zero);

        await Assert.That(probed).IsEqualTo(0);
    }

    [Test]
    public async Task A_cancelled_pass_probes_nothing() {
        var state  = StateWithSecondaryRoot();
        var probed = 0;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Link(state, (_, _) => { probed++; return Task.FromResult<RepositoryPayload?>(CliPr()); }, (_, _) => Task.FromResult(true), ct: cts.Token);

        await Assert.That(probed).IsEqualTo(0);
    }

    [Test]
    public async Task The_post_sees_the_callers_cancellation() {
        var state = StateWithSecondaryRoot();
        using var cts = new CancellationTokenSource();
        var cancelledDuringPost = false;

        await Link(state, Detects(CliPr()), (_, token) => {
            cts.Cancel();
            cancelledDuringPost = token.IsCancellationRequested;
            return Task.FromResult(true);
        }, ct: cts.Token);

        await Assert.That(cancelledDuringPost).IsTrue();
    }

    static string ToolUse(string tool, string path) =>
        $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"{{{tool}}}","input":{"file_path":"{{{path}}}"}}]}}""";

    static (WatchState State, string SubagentsDir) SessionWatcherWithSubagents(string dir) {
        var parent    = Path.Combine(dir, "session.jsonl");
        var subagents = ClaudeSubagentFiles.DirectoryOf(parent);
        Directory.CreateDirectory(subagents);
        return (new WatchState { SecondaryRoots = new SecondaryRepoRoots(FakeFindRoot, "/h/dev/server"), SubagentCheckouts = new ClaudeSubagentCheckouts(parent) }, subagents);
    }

    static async Task<(List<string> Probed, List<RepositoryPayload> Posted)> ObserveAndLink(WatchState state, bool final = false) {
        var probed = new List<string>();
        var posted = new List<RepositoryPayload>();

        WatchCommand.ObserveSubagentCheckouts(state, TimeProvider.System, final);
        await Link(state,
            (root, _) => { probed.Add(root); return Task.FromResult<RepositoryPayload?>(CliPr()); },
            (pr, _) => { posted.Add(pr); return Task.FromResult(true); });

        return (probed, posted);
    }

    /// Two subagents changing files in the same checkout cost one probe, and its PR is posted by the session watcher.
    [Test]
    public async Task A_pr_on_a_checkout_subagents_changed_is_linked_once_by_the_session_watcher() {
        using var dir = new TempDir();
        var (state, subagents) = SessionWatcherWithSubagents(dir.Path);
        await File.WriteAllLinesAsync(Path.Combine(subagents, "agent-a1.jsonl"), [ToolUse("Edit", $"{CliRoot}/src/x.cs")]);
        await File.WriteAllLinesAsync(Path.Combine(subagents, "agent-a2.jsonl"), [ToolUse("Write", $"{CliRoot}/src/y.cs")]);

        var (probed, posted) = await ObserveAndLink(state);

        await Assert.That(probed).IsEquivalentTo([CliRoot]);
        await Assert.That(posted).Count().IsEqualTo(1);
        await Assert.That(posted[0].PrNumber).IsEqualTo(915);
    }

    [Test]
    public async Task A_checkout_a_subagent_only_read_is_never_probed() {
        using var dir = new TempDir();
        var (state, subagents) = SessionWatcherWithSubagents(dir.Path);
        await File.WriteAllLinesAsync(Path.Combine(subagents, "agent-a1.jsonl"), [ToolUse("Read", $"{CliRoot}/src/x.cs")]);

        var (probed, posted) = await ObserveAndLink(state);

        await Assert.That(probed).IsEmpty();
        await Assert.That(posted).IsEmpty();
    }

    [Test]
    public async Task A_pr_url_in_a_subagents_tool_output_is_not_linked() {
        using var dir = new TempDir();
        var (state, subagents) = SessionWatcherWithSubagents(dir.Path);
        await File.WriteAllLinesAsync(Path.Combine(subagents, "agent-a1.jsonl"), [
            ToolUse("Read", $"{CliRoot}/src/x.cs"),
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"See https://github.com/kurrent-io/kcap-cli/pull/915"}]}}""",
        ]);

        var (probed, posted) = await ObserveAndLink(state);

        await Assert.That(probed).IsEmpty();
        await Assert.That(posted).IsEmpty();
    }

    /// A subagent that changes a checkout after an earlier pass read its transcript still has it probed.
    [Test]
    public async Task A_change_appended_after_a_pass_is_seen_by_the_next() {
        using var dir = new TempDir();
        var (state, subagents) = SessionWatcherWithSubagents(dir.Path);
        var transcript = Path.Combine(subagents, "agent-a1.jsonl");
        await File.WriteAllLinesAsync(transcript, [ToolUse("Read", $"{CliRoot}/src/x.cs")]);
        await ObserveAndLink(state);

        await File.AppendAllLinesAsync(transcript, [ToolUse("Edit", $"{CliRoot}/src/x.cs")]);
        var (probed, posted) = await ObserveAndLink(state);

        await Assert.That(probed).IsEquivalentTo([CliRoot]);
        await Assert.That(posted).Count().IsEqualTo(1);
    }

    /// A live pass holds a last line without its newline, as it may still be written; the shutdown pass takes it once it parses.
    [Test]
    public async Task A_last_change_without_a_newline_is_linked_by_the_shutdown_pass() {
        using var dir = new TempDir();
        var (state, subagents) = SessionWatcherWithSubagents(dir.Path);
        await File.WriteAllTextAsync(Path.Combine(subagents, "agent-a1.jsonl"), ToolUse("Edit", $"{CliRoot}/src/x.cs"));

        var (liveProbed, _)  = await ObserveAndLink(state);
        var (probed, posted) = await ObserveAndLink(state, final: true);

        await Assert.That(liveProbed).IsEmpty();
        await Assert.That(probed).IsEquivalentTo([CliRoot]);
        await Assert.That(posted).Count().IsEqualTo(1);
    }

    /// A replacement at least as long as what was read must not resume at the old offset, past the change it holds.
    [Test]
    public async Task A_replaced_transcript_is_read_again_from_its_start() {
        using var dir = new TempDir();
        var (state, subagents) = SessionWatcherWithSubagents(dir.Path);
        var transcript = Path.Combine(subagents, "agent-a1.jsonl");
        await File.WriteAllLinesAsync(transcript, [ToolUse("Read", $"{CliRoot}/src/x.cs")]);
        await ObserveAndLink(state);

        await File.WriteAllLinesAsync(transcript, [ToolUse("Edit", $"{CliRoot}/src/x.cs"), ToolUse("Read", $"{CliRoot}/src/a-much-longer-path-than-before.cs")]);
        var (probed, posted) = await ObserveAndLink(state);

        await Assert.That(probed).IsEquivalentTo([CliRoot]);
        await Assert.That(posted).Count().IsEqualTo(1);
    }
}
