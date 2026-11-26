using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.PullRequests;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit.Services;

/// A chat link's state is looked up in the background, once per item however many chips show it and
/// one lookup at a time, and is held for a while so re-rendering costs nothing. A pull request a
/// listed session holds is never looked up.
public class GitHubLinkStatusesTests {
    static readonly GitHubLink Pull = GitHubLink.Parse("https://github.com/kurrent-io/kcap/pull/2625")!;
    static readonly GitHubLink SameAsIssue = GitHubLink.Parse("https://github.com/Kurrent-IO/kcap/issues/2625")!;
    static readonly GitHubLink Other = GitHubLink.Parse("https://github.com/kurrent-io/kcap/issues/7")!;

    sealed class Lookups {
        public readonly ConcurrentQueue<(GitHubLink Link, TaskCompletionSource<GitHubLinkState?> Answer)> Calls = new();

        public Task<GitHubLinkState?> LookUp(GitHubLink link, CancellationToken ct) {
            var answer = new TaskCompletionSource<GitHubLinkState?>();
            Calls.Enqueue((link, answer));
            return answer.Task;
        }

        public void Answer(int call, GitHubLinkState? state) => Calls.ElementAt(call).Answer.SetResult(state);
    }

    static (GitHubLinkStatuses Statuses, Lookups Lookups, FakeTimeProvider Time) Build(
            IObservable<IReadOnlyDictionary<string, RailPullRequestSummary>>? sessions = null) {
        var lookups = new Lookups();
        var time = new FakeTimeProvider();
        return (new GitHubLinkStatuses(lookups.LookUp, time, ImmediateScheduler.Instance, sessions), lookups, time);
    }

    static async Task Until(Func<bool> condition) {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        await Assert.That(condition()).IsTrue();
    }

    [Test]
    public async Task An_item_shown_twice_is_looked_up_once_and_both_chips_get_its_state() {
        var (statuses, lookups, _) = Build();
        using var _ = statuses;
        var first = new List<GitHubLinkState>();
        var second = new List<GitHubLinkState>();
        using var a = statuses.Watch(Pull).Subscribe(first.Add);
        using var b = statuses.Watch(SameAsIssue).Subscribe(second.Add);

        await Assert.That(lookups.Calls.Count).IsEqualTo(1);
        await Assert.That(first).IsEmpty();

        lookups.Answer(0, GitHubLinkState.Merged);

        await Assert.That(first).IsEquivalentTo([GitHubLinkState.Merged]);
        await Assert.That(second).IsEquivalentTo([GitHubLinkState.Merged]);
    }

    [Test]
    public async Task A_known_state_arrives_at_once_and_is_looked_up_again_only_once_it_is_stale() {
        var (statuses, lookups, time) = Build();
        using var _ = statuses;
        statuses.Watch(Pull).Subscribe(_ => { }).Dispose();
        lookups.Answer(0, GitHubLinkState.Open);

        var seen = new List<GitHubLinkState>();
        statuses.Watch(Pull).Subscribe(seen.Add).Dispose();
        await Assert.That(seen).IsEquivalentTo([GitHubLinkState.Open]);
        await Assert.That(lookups.Calls.Count).IsEqualTo(1);

        time.Advance(GitHubLinkStatuses.Fresh);
        using var again = statuses.Watch(Pull).Subscribe(seen.Add);
        await Assert.That(lookups.Calls.Count).IsEqualTo(2);
        lookups.Answer(1, GitHubLinkState.Merged);
        await Assert.That(seen).IsEquivalentTo([GitHubLinkState.Open, GitHubLinkState.Open, GitHubLinkState.Merged]);
    }

    [Test]
    public async Task A_merge_is_never_looked_up_again() {
        var (statuses, lookups, time) = Build();
        using var _ = statuses;
        statuses.Watch(Pull).Subscribe(_ => { }).Dispose();
        lookups.Answer(0, GitHubLinkState.Merged);

        time.Advance(TimeSpan.FromDays(1));
        statuses.Watch(Pull).Subscribe(_ => { }).Dispose();

        await Assert.That(lookups.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_failed_lookup_leaves_no_state_and_is_not_retried_until_stale() {
        var (statuses, lookups, time) = Build();
        using var _ = statuses;
        var seen = new List<GitHubLinkState>();
        using var watch = statuses.Watch(Pull).Subscribe(seen.Add);
        lookups.Answer(0, null);

        statuses.Watch(Pull).Subscribe(seen.Add).Dispose();
        await Assert.That(seen).IsEmpty();
        await Assert.That(statuses.Current(Pull)).IsNull();
        await Assert.That(lookups.Calls.Count).IsEqualTo(1);

        time.Advance(GitHubLinkStatuses.Fresh);
        statuses.Watch(Pull).Subscribe(seen.Add).Dispose();
        await Assert.That(lookups.Calls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_failed_refresh_keeps_the_last_state() {
        var (statuses, lookups, time) = Build();
        using var _ = statuses;
        statuses.Watch(Pull).Subscribe(_ => { }).Dispose();
        lookups.Answer(0, GitHubLinkState.Open);
        time.Advance(GitHubLinkStatuses.Fresh);
        statuses.Watch(Pull).Subscribe(_ => { }).Dispose();
        lookups.Answer(1, null);

        await Assert.That(statuses.Current(Pull)).IsEqualTo(GitHubLinkState.Open);
    }

    [Test]
    public async Task Lookups_run_one_at_a_time() {
        var (statuses, lookups, _) = Build();
        using var _ = statuses;
        using var a = statuses.Watch(Pull).Subscribe(_ => { });
        using var b = statuses.Watch(Other).Subscribe(_ => { });

        await Assert.That(lookups.Calls.Count).IsEqualTo(1);
        lookups.Answer(0, GitHubLinkState.Open);

        await Until(() => lookups.Calls.Count == 2);
        await Assert.That(lookups.Calls.ElementAt(1).Link).IsEqualTo(Other);
    }

    [Test]
    public async Task A_link_no_chip_shows_by_its_turn_is_not_looked_up_until_one_does() {
        var (statuses, lookups, _) = Build();
        using var _ = statuses;
        using var a = statuses.Watch(Pull).Subscribe(_ => { });
        statuses.Watch(Other).Subscribe(_ => { }).Dispose();

        lookups.Answer(0, GitHubLinkState.Open);
        await Task.Delay(100);
        await Assert.That(lookups.Calls.Count).IsEqualTo(1);

        using var b = statuses.Watch(Other).Subscribe(_ => { });
        await Until(() => lookups.Calls.Count == 2);
        await Assert.That(lookups.Calls.ElementAt(1).Link).IsEqualTo(Other);
    }

    [Test]
    public async Task A_pull_request_a_listed_session_holds_is_not_looked_up() {
        var sessions = new BehaviorSubject<IReadOnlyDictionary<string, RailPullRequestSummary>>(new Dictionary<string, RailPullRequestSummary> {
            ["s1"] = new([Item("github.com", "Merged"), Item("github.example.com", "Open", number: 7)]),
        });
        var (statuses, lookups, _) = Build(sessions);
        using var _ = statuses;
        var seen = new List<GitHubLinkState>();
        using var watch = statuses.Watch(SameAsIssue).Subscribe(seen.Add);

        await Assert.That(seen).IsEquivalentTo([GitHubLinkState.Merged]);
        await Assert.That(lookups.Calls).IsEmpty();

        using var other = statuses.Watch(Other).Subscribe(_ => { });
        await Assert.That(lookups.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_session_s_newer_state_reaches_a_shown_chip() {
        var sessions = new BehaviorSubject<IReadOnlyDictionary<string, RailPullRequestSummary>>(new Dictionary<string, RailPullRequestSummary> {
            ["s1"] = new([Item("github.com", "Open")]),
        });
        var (statuses, _, _) = Build(sessions);
        using var _ = statuses;
        var seen = new List<GitHubLinkState>();
        using var watch = statuses.Watch(Pull).Subscribe(seen.Add);

        sessions.OnNext(new Dictionary<string, RailPullRequestSummary> { ["s1"] = new([Item("github.com", "Merged")]) });

        await Assert.That(seen).IsEquivalentTo([GitHubLinkState.Open, GitHubLinkState.Merged]);
    }

    [Test]
    public async Task A_lookup_started_before_a_session_s_state_arrived_does_not_overwrite_it() {
        var sessions = new Subject<IReadOnlyDictionary<string, RailPullRequestSummary>>();
        var (statuses, lookups, _) = Build(sessions);
        using var _ = statuses;
        using var watch = statuses.Watch(Pull).Subscribe(_ => { });

        sessions.OnNext(new Dictionary<string, RailPullRequestSummary> { ["s1"] = new([Item("github.com", "Merged")]) });
        lookups.Answer(0, GitHubLinkState.Open);

        await Assert.That(statuses.Current(Pull)).IsEqualTo(GitHubLinkState.Merged);
    }

    /// The rail republishes every session's summary when any one changes, so an unchanged report comes round again.
    [Test]
    public async Task A_session_repeating_its_report_does_not_overwrite_a_newer_lookup() {
        var open = new Dictionary<string, RailPullRequestSummary> { ["s1"] = new([Item("github.com", "Open")]) };
        var sessions = new Subject<IReadOnlyDictionary<string, RailPullRequestSummary>>();
        var (statuses, lookups, time) = Build(sessions);
        using var _ = statuses;
        sessions.OnNext(open);
        time.Advance(GitHubLinkStatuses.Fresh);
        using var watch = statuses.Watch(Pull).Subscribe(_ => { });
        lookups.Answer(0, GitHubLinkState.Closed);

        sessions.OnNext(new Dictionary<string, RailPullRequestSummary>(open) { ["s2"] = new([Item("github.com", "Open", number: 7)]) });

        await Assert.That(statuses.Current(Pull)).IsEqualTo(GitHubLinkState.Closed);
    }

    [Test]
    public async Task Two_sessions_repeating_different_reports_do_not_overwrite_a_newer_lookup_but_a_change_does() {
        var reports = new Dictionary<string, RailPullRequestSummary> {
            ["s1"] = new([Item("github.com", "Open")]), ["s2"] = new([Item("github.com", "Closed")]),
        };
        var sessions = new Subject<IReadOnlyDictionary<string, RailPullRequestSummary>>();
        var (statuses, lookups, time) = Build(sessions);
        using var _ = statuses;
        sessions.OnNext(reports);
        time.Advance(GitHubLinkStatuses.Fresh);
        using var watch = statuses.Watch(Pull).Subscribe(_ => { });
        lookups.Answer(0, GitHubLinkState.Draft);

        sessions.OnNext(new Dictionary<string, RailPullRequestSummary>(reports));
        await Assert.That(statuses.Current(Pull)).IsEqualTo(GitHubLinkState.Draft);

        sessions.OnNext(new Dictionary<string, RailPullRequestSummary>(reports) { ["s1"] = new([Item("github.com", "Merged")]) });
        await Assert.That(statuses.Current(Pull)).IsEqualTo(GitHubLinkState.Merged);
    }

    [Test]
    public async Task An_older_session_summary_does_not_undo_a_merge() {
        var sessions = new Subject<IReadOnlyDictionary<string, RailPullRequestSummary>>();
        var (statuses, _, _) = Build(sessions);
        using var _ = statuses;
        sessions.OnNext(new Dictionary<string, RailPullRequestSummary> { ["s1"] = new([Item("github.com", "Merged")]) });
        sessions.OnNext(new Dictionary<string, RailPullRequestSummary> { ["s2"] = new([Item("github.com", "Open")]) });

        await Assert.That(statuses.Current(Pull)).IsEqualTo(GitHubLinkState.Merged);
    }

    [Test]
    public async Task Links_on_screen_past_the_capacity_are_all_kept() {
        using var statuses = new GitHubLinkStatuses((_, _) => Task.FromResult<GitHubLinkState?>(GitHubLinkState.Open),
            new FakeTimeProvider(), ImmediateScheduler.Instance);
        var links = Enumerable.Range(1, 1100).Select(n => GitHubLink.Parse($"https://github.com/kurrent-io/kcap/issues/{n}")!).ToList();
        var watches = links.Select(link => statuses.Watch(link).Subscribe(_ => { })).ToList();

        await Assert.That(links.Count(link => statuses.Current(link) is null)).IsEqualTo(0);
        watches.ForEach(watch => watch.Dispose());
    }

    [Test]
    public async Task A_session_repeating_its_report_restores_a_state_evicted_since() {
        var open = new Dictionary<string, RailPullRequestSummary> { ["s1"] = new([Item("github.com", "Open")]) };
        var sessions = new Subject<IReadOnlyDictionary<string, RailPullRequestSummary>>();
        var time = new FakeTimeProvider();
        using var statuses = new GitHubLinkStatuses((_, _) => Task.FromResult<GitHubLinkState?>(GitHubLinkState.Closed),
            time, ImmediateScheduler.Instance, sessions);
        sessions.OnNext(open);
        time.Advance(TimeSpan.FromSeconds(1));
        foreach (var n in Enumerable.Range(1, 1100)) {
            using var watch = statuses.Watch(GitHubLink.Parse($"https://github.com/kurrent-io/kcap/issues/{n}")!).Subscribe(_ => { });
        }
        await Assert.That(statuses.Current(Pull)).IsNull();

        sessions.OnNext(new Dictionary<string, RailPullRequestSummary>(open));

        await Assert.That(statuses.Current(Pull)).IsEqualTo(GitHubLinkState.Open);
    }

    static RailPullRequestMenuItem Item(string host, string lifecycle, int number = 2625) => new(new PullRequestSubjectDto {
        Provider = "github", Host = host, RepoHash = "hash", Owner = "kurrent-io", RepoName = "kcap", Number = number,
    }, "Render GitHub links as chips", lifecycle);
}
