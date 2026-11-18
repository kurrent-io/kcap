using System.Collections.Frozen;
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit.ViewModels;

public class SessionRailViewModelTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Attention_tracks_the_unfiltered_row_source_while_the_rail_hides_live_sessions() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System);
            rail.State = RailState.Ended;
            var waiting = AgentRow.FromLocal(Dto("waiting", "/repo") with { AwaitingInput = true }, new RepoIdentity("repo", "repo"));
            var failed = AgentRow.FromRemote(RemoteDto("failed", "owner", "repo") with { Status = "Failed" });
            var working = AgentRow.FromLocal(Dto("working", "/repo") with { AwaitingInput = false }, new RepoIdentity("repo", "repo"));
            directory.Rows.AddOrUpdate([waiting, failed, working]);

            await Assert.That(rail.Groups).IsEmpty();
            await Assert.That(rail.Attention).IsEqualTo((2, 1));
            rail.Scope = RailScope.Team;
            rail.Grouping = RailGrouping.Status;
            rail.State = RailState.Live;
            foreach (var group in rail.Groups) await group.ToggleCommand.Execute();
            await Assert.That(rail.Attention).IsEqualTo((2, 1));
            rail.State = RailState.Ended;

            directory.Rows.AddOrUpdate(waiting with { AwaitingInput = false });
            await Assert.That(rail.Attention).IsEqualTo((1, 1));
            directory.Rows.AddOrUpdate(failed with { Status = "Completed" });
            await Assert.That(rail.Attention).IsEqualTo((0, 0));
            directory.Rows.AddOrUpdate(working with { AwaitingInput = true });
            await Assert.That(rail.Attention).IsEqualTo((1, 0));
            directory.Rows.RemoveKey(working.Key);
            await Assert.That(rail.Attention).IsEqualTo((0, 0));
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Permission_and_answer_changes_count_each_present_row_once() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var pending = new BehaviorSubject<IReadOnlySet<string>>(FrozenSet<string>.Empty);
            using var answers = new BehaviorSubject<IReadOnlySet<string>>(FrozenSet<string>.Empty);
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System,
                agentsWithPending: pending, agentsAwaitingAnswer: answers);
            var row = AgentRow.FromLocal(Dto("a", "/repo") with { AwaitingInput = false }, new RepoIdentity("repo", "repo"));
            directory.Rows.AddOrUpdate(row);
            await Assert.That(rail.Attention.Count).IsEqualTo(0);

            pending.OnNext(new HashSet<string> { "a", "absent" });
            await Assert.That(rail.Attention.Count).IsEqualTo(1);
            answers.OnNext(new HashSet<string> { "a" });
            await Assert.That(rail.Attention.Count).IsEqualTo(1);
            pending.OnNext(FrozenSet<string>.Empty);
            await Assert.That(rail.Attention.Count).IsEqualTo(1);
            answers.OnNext(FrozenSet<string>.Empty);
            await Assert.That(rail.Attention.Count).IsEqualTo(0);
            pending.OnNext(new HashSet<string> { "a" });
            directory.Rows.RemoveKey(row.Key);
            await Assert.That(rail.Attention.Count).IsEqualTo(0);
        });
    }

    // "/x/wt/<leaf>" resolves to "/x" — a stand-in for GitRepository.ResolveMainRepoRoot so no
    // test touches real .git files.
    static string Resolve(string path) {
        var marker = path.IndexOf("/wt/", StringComparison.Ordinal);
        return marker < 0 ? path : path[..marker];
    }

    static AgentStatusDto Dto(string id, string? repoPath, string status = "Running", DateTime? created = null) =>
        new(id, "agent", "claude", repoPath, status, null, null, null, created ?? DateTime.UtcNow, null, null);

    static AgentInstanceDto RemoteDto(string id, string owner, string repo, string daemon = "work-mac") => new() {
        AgentId = id, Status = "Running", DaemonName = daemon, OwnerUserId = "u1", Vendor = "claude",
        RepoOwner = owner, RepoName = repo,
    };

    /// Builds the rail over a fresh AgentDirectory — daemon and remote fed by the fakes, repo
    /// identity resolved by originUrl (null unless a test needs merged remote/local groups).
    static (FakeDaemonClientService Service, FakeRemoteAgents Remote, SessionRailViewModel Rail) Build(
            Action<string>? open = null, Action<string>? openRemote = null,
            Func<string, string>? resolveRepoRoot = null, Func<string, string?>? originUrl = null) {
        var service = new FakeDaemonClientService();
        var remote = new FakeRemoteAgents();
        var directory = new AgentDirectory(
            service, remote, new FakeServerLane(), new RepoIdentityResolver(originUrl ?? (_ => null)),
            resolveRepoRoot ?? Resolve, null, null, TimeProvider.System);
        var rail = new SessionRailViewModel(
            directory, open ?? (_ => { }), openRemote ?? (_ => { }), TimeProvider.System, afterPaint: run => run());
        return (service, remote, rail);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Row_revisions_preserve_commands_and_bindings_and_refresh_all_session_facts() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            var opened = new List<string>();
            using var prs = new System.Reactive.Subjects.BehaviorSubject<IReadOnlyDictionary<string, RailPullRequestSummary>>(
                new Dictionary<string, RailPullRequestSummary> {
                    ["published"] = new([new(Capacitor.Cli.Core.PullRequests.PullRequestWire.Subject(FakePullRequestSource.Link(42)), null, "Open")])
                });
            using var rail = new SessionRailViewModel(directory, opened.Add, _ => { }, TimeProvider.System, pullRequests: prs, afterPaint: run => run());
            var row = AgentRow.FromLocal(Dto("a", "/repo") with { SessionId = null }, new RepoIdentity("path:/repo", "repo"));
            directory.Rows.AddOrUpdate(row);
            var session = rail.Groups.Single().Sessions.Single();
            var command = session.OpenCommand;
            rail.OpenSessionKey = "local:a";
            directory.Rows.AddOrUpdate(row with { Title = "Updated", Model = "new model", SessionId = "published", LiveSubagents = 2, AwaitingInput = false });
            await Assert.That(ReferenceEquals(session, rail.Groups.Single().Sessions.Single())).IsTrue();
            await Assert.That(ReferenceEquals(command, session.OpenCommand)).IsTrue();
            await Assert.That(session.Primary).IsEqualTo("Updated");
            await Assert.That(session.Model).IsEqualTo("new model");
            await Assert.That(session.Meta).EndsWith("2 subagents");
            await Assert.That(session.Status.Kind).IsEqualTo(AgentStatusKind.Working);
            await Assert.That(session.IsSelected).IsTrue();
            await Assert.That(session.PullRequestLabel).IsEqualTo("PR #42");
            await session.OpenCommand.Execute();
            await Assert.That(opened).IsEquivalentTo(["a"]);
            directory.Rows.RemoveKey(row.Key);
            prs.OnNext(new Dictionary<string, RailPullRequestSummary>());
            await Assert.That(session.PullRequestLabel).IsEqualTo("PR #42");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_selected_pending_launch_stays_selected_when_published_without_selecting_its_remote_namesake() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            var identity = new RepoIdentity("path:/repo", "repo");
            var pending = AgentRow.FromPending(new PendingLaunchDto("same", "claude", "/repo", "Launching", DateTime.UtcNow, "spawned"), identity);
            directory.Rows.AddOrUpdate(pending);
            directory.Rows.AddOrUpdate(AgentRow.FromRemote(RemoteDto("same", "owner", "repo")));
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System);

            rail.OpenSessionKey = "local:same";
            await Assert.That(rail.Groups.SelectMany(repo => repo.Sessions).Single(row => row.IsSelected).Key).IsEqualTo("pending:same");

            directory.Rows.Edit(rows => {
                rows.RemoveKey(pending.Key);
                rows.AddOrUpdate(AgentRow.FromLocal(Dto("same", "/repo"), identity));
            });
            await Assert.That(rail.Groups.SelectMany(repo => repo.Sessions).Single(row => row.IsSelected).Key).IsEqualTo("local:same");

            rail.OpenSessionKey = "remote:same";
            await Assert.That(rail.Groups.SelectMany(repo => repo.Sessions).Single(row => row.IsSelected).Key).IsEqualTo("remote:same");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Unrelated_pending_changes_leave_other_repository_and_session_presentations_untouched() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var pending = new System.Reactive.Subjects.BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>());
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System, pending);
            directory.Rows.AddOrUpdate(AgentRow.FromLocal(Dto("a", "/a"), new RepoIdentity("path:/a", "a")));
            directory.Rows.AddOrUpdate(AgentRow.FromLocal(Dto("b", "/b"), new RepoIdentity("path:/b", "b")));
            foreach (var repo in rail.Groups) await repo.ToggleCommand.Execute();
            var unaffected = rail.Groups.Single(repo => repo.Label == "b");
            var header = unaffected.HeaderStatus;
            var status = unaffected.Sessions.Single().Status;
            pending.OnNext(new HashSet<string> { "a" });
            await Assert.That(ReferenceEquals(header, unaffected.HeaderStatus)).IsTrue();
            await Assert.That(ReferenceEquals(status, unaffected.Sessions.Single().Status)).IsTrue();
            await Assert.That(rail.Groups.Single(repo => repo.Label == "a").HeaderStatus!.Kind).IsEqualTo(AgentStatusKind.NeedsYou);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Session_age_advances_without_directory_revisions() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
            using var directory = new FakeAgentDirectory();
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, time);
            directory.Rows.AddOrUpdate(AgentRow.FromLocal(Dto("a", "/repo", created: time.GetUtcNow().UtcDateTime), new RepoIdentity("path:/repo", "repo")));
            var session = rail.Groups.Single().Sessions.Single();
            await Assert.That(session.Meta).IsEqualTo("just now");
            time.Advance(TimeSpan.FromMinutes(2));
            await Assert.That(session.Meta).IsEqualTo("2m ago");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Revealing_a_session_honors_origin_and_covers_pending_sessions() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            directory.Rows.AddOrUpdate(AgentRow.FromLocal(Dto("same", "/local"), new RepoIdentity("path:/local", "local")));
            directory.Rows.AddOrUpdate(AgentRow.FromRemote(RemoteDto("same", "owner", "remote")));
            directory.Rows.AddOrUpdate(AgentRow.FromPending(new PendingLaunchDto("pending", "claude", "/pending", "Title", DateTime.UtcNow, "spawned"),
                new RepoIdentity("path:/pending", "pending")));
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System);
            foreach (var repo in rail.Groups) repo.ToggleCommand.Execute().Subscribe();
            rail.NotifySessionOpened("same", AgentOrigin.Remote);
            await Assert.That(rail.Groups.Single(r => r.IsExpanded).Label).IsEqualTo("owner/remote");
            rail.Groups.Single(r => r.IsExpanded).ToggleCommand.Execute().Subscribe();
            rail.NotifySessionOpened("same", AgentOrigin.Local);
            await Assert.That(rail.Groups.Single(r => r.IsExpanded).Label).IsEqualTo("local");
            rail.Groups.Single(r => r.IsExpanded).ToggleCommand.Execute().Subscribe();
            rail.NotifySessionOpened("pending", AgentOrigin.Local);
            await Assert.That(rail.Groups.Single(r => r.IsExpanded).Label).IsEqualTo("pending");
            rail.Groups.Single(r => r.IsExpanded).ToggleCommand.Execute().Subscribe();
            rail.NotifySessionOpened("missing", AgentOrigin.Local);
            await Assert.That(rail.Groups.Any(r => r.IsExpanded)).IsFalse();
        });
    }

    /// Stepping follows the drawn order across groups, skips a folded group's rows, starts from an
    /// end when nothing is selected and stops at either end.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Stepping_opens_the_adjacent_shown_session_in_rail_order() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            var opened = new List<string>();
            using var rail = new SessionRailViewModel(directory, opened.Add, _ => { }, TimeProvider.System);
            var now = DateTime.UtcNow;
            void Add(string id, string repo, int minutesAgo) =>
                directory.Rows.AddOrUpdate(AgentRow.FromLocal(Dto(id, $"/{repo}", created: now.AddMinutes(-minutesAgo)), new RepoIdentity($"path:/{repo}", repo)));
            Add("a-new", "a", 1);
            Add("a-old", "a", 5);
            Add("b-only", "b", 2);
            Add("c-only", "c", 3);
            await rail.Groups.Single(g => g.Label == "b").ToggleCommand.Execute();

            await Assert.That(rail.OpenAdjacent(1)).IsTrue();
            await Assert.That(rail.OpenAdjacent(-1)).IsTrue();
            await Assert.That(opened).IsEquivalentTo(["a-new", "c-only"], CollectionOrdering.Matching);

            opened.Clear();
            rail.OpenSessionKey = "local:a-old";
            await rail.OpenNextSessionCommand.Execute();
            rail.OpenSessionKey = "local:c-only";
            await rail.OpenPreviousSessionCommand.Execute();
            await Assert.That(opened).IsEquivalentTo(["c-only", "a-old"], CollectionOrdering.Matching);

            opened.Clear();
            await Assert.That(rail.OpenAdjacent(1)).IsFalse();
            rail.OpenSessionKey = "local:b-only";
            await Assert.That(rail.OpenAdjacent(-1)).IsTrue();
            rail.OpenSessionKey = "local:a-new";
            await Assert.That(rail.OpenAdjacent(-1)).IsFalse();
            await Assert.That(opened).IsEquivalentTo(["a-old"], CollectionOrdering.Matching);
        });
    }

    /// The default order is newest first, ties by id; a status flip never moves a row, a changed
    /// time does, and the same rows arriving in another order land in the same places.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Newest_first_order_survives_updates_and_reversed_refreshes() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, _, rail) = Build();
            using (rail) {
                var created = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
                var rows = new[] { Dto("c", "/repo", created: created.AddHours(1)),
                    Dto("b", "/repo", created: created), Dto("a", "/repo", created: created) };
                service.Agents.AddOrUpdate(rows);
                await Assert.That(rail.Groups.Single().Sessions.Select(s => s.Id))
                    .IsEquivalentTo(["c", "a", "b"], CollectionOrdering.Matching);
                service.Agents.AddOrUpdate(rows[1] with { Title = "Updated title", AwaitingInput = true });
                await Assert.That(rail.Groups.Single().Sessions.Select(s => s.Id))
                    .IsEquivalentTo(["c", "a", "b"], CollectionOrdering.Matching);
                var session = rail.Groups.Single().Sessions.Single(row => row.Id == "b");
                service.Agents.AddOrUpdate(rows[1] with { CreatedAt = created.AddHours(2) });
                await Assert.That(rail.Groups.Single().Sessions.Select(row => row.Id))
                    .IsEquivalentTo(["b", "c", "a"], CollectionOrdering.Matching);
                await Assert.That(ReferenceEquals(session, rail.Groups.Single().Sessions.First())).IsTrue();
                service.Agents.Clear();
                service.Agents.AddOrUpdate(rows.Reverse());
                await Assert.That(rail.Groups.Single().Sessions.Select(s => s.Id))
                    .IsEquivalentTo(["c", "a", "b"], CollectionOrdering.Matching);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Groups_sessions_under_repository_with_no_repository_last() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, _, rail) = Build();
            using (rail) {
                service.Agents.AddOrUpdate(Dto("a1", "/dev/zeta"));
                service.Agents.AddOrUpdate(Dto("a2", "/dev/alpha/wt/feature-x"));
                service.Agents.AddOrUpdate(Dto("a3", "/dev/alpha"));
                service.Agents.AddOrUpdate(Dto("a4", null));

                await Assert.That(rail.Groups.Select(r => r.Label))
                    .IsEquivalentTo(["alpha", "zeta", "No repository"], CollectionOrdering.Matching);

                var alpha = rail.Groups[0];
                await Assert.That(alpha.Sessions.Select(row => row.Id)).IsEquivalentTo(["a2", "a3"]);

                var noRepo = rail.Groups[2];
                await Assert.That(noRepo.IsNoRepository).IsTrue();
                await Assert.That(noRepo.Sessions.Select(row => row.Id)).IsEquivalentTo(["a4"]);
            }
        });
    }

    /// `/repo` and `/repo/` are one repository — without separator normalization the rail would
    /// show two same-leaf groups for one checkout.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Trailing_separators_do_not_split_a_repository_group() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, _, rail) = Build();
            using (rail) {
                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha"));
                service.Agents.AddOrUpdate(Dto("a2", "/dev/alpha/"));

                await Assert.That(rail.Groups).Count().IsEqualTo(1);
                await Assert.That(rail.Groups[0].Key).IsEqualTo("path:/dev/alpha");
                await Assert.That(rail.Groups[0].RootDisplay).IsEqualTo("/dev/alpha");
                await Assert.That(rail.Groups[0].Sessions.Select(s => s.Id))
                    .IsEquivalentTo(["a1", "a2"]);
            }
        });
    }

    /// A reviewer that borrowed a checkout belongs on that checkout's node, beside the session it
    /// reviews, including a snapshot reviewer that names its borrowed checkout.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_borrowed_reviewer_files_under_the_worktree_it_reviews() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, _, rail) = Build();
            using (rail) {
                const string worktree = "/dev/alpha/wt/agent-1";
                service.Agents.AddOrUpdate(Dto("p1", "/dev/alpha") with { WorktreePath = worktree, WorkLocation = "owned" });
                service.Agents.AddOrUpdate(Dto("r1", "/dev/alpha") with {
                    WorktreePath = worktree, WorkLocation = "borrowed", BorrowedFrom = worktree });
                service.Agents.AddOrUpdate(Dto("s1", "/dev/alpha") with {
                    WorktreePath = "/snapshots/borrowed-1", WorkLocation = "borrowed", BorrowedFrom = worktree });

                var node = rail.Groups.Single();
                await Assert.That(node.Sessions.Select(s => s.Id)).IsEquivalentTo(["p1", "r1", "s1"]);

                await Assert.That(node.Sessions.Single(row => row.Id == "s1").CheckoutPath).IsEqualTo(worktree);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Counts_track_the_cache() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, _, rail) = Build();
            using (rail) {
                await Assert.That(rail.IsEmpty).IsTrue();
                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha"));
                service.Agents.AddOrUpdate(Dto("a2", "/dev/alpha/wt/feature-x"));
                await Assert.That(rail.IsEmpty).IsFalse();
                await Assert.That(rail.Groups[0].CountText).IsEqualTo("2 sessions");

                service.Agents.RemoveKey("a2");
                await Assert.That(rail.Groups[0].CountText).IsEqualTo("1 session");
            }
        });
    }

    /// A stopped agent's row reads Done before the server lists its session ended; the group count
    /// agrees with the row instead of calling it live.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_completed_local_row_counts_as_ended() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System);
            var repo = new RepoIdentity("path:/repo", "repo");
            directory.Rows.AddOrUpdate(AgentRow.FromLocal(Dto("a", "/repo"), repo));
            directory.Rows.AddOrUpdate(AgentRow.FromLocal(Dto("b", "/repo", status: "Completed"), repo));

            await Assert.That(rail.Groups.Single().CountText).IsEqualTo("1 live · 1 ended");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Selecting_a_session_does_not_reorder_the_directory_rows() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, _, rail) = Build();
            using (rail) {
                var old = Dto("a1", "/dev/alpha", created: DateTime.UtcNow.AddHours(-2));
                service.Agents.AddOrUpdate(old);
                service.Agents.AddOrUpdate(Dto("a2", "/dev/alpha", created: DateTime.UtcNow.AddHours(-1)));
                await Assert.That(rail.Groups[0].Sessions[0].Id).IsEqualTo("a2");
                await Assert.That(rail.Groups[0].Sessions[1].Id).IsEqualTo("a1");
                rail.OpenSessionKey = "local:a1";
                await Assert.That(rail.Groups[0].Sessions[1].Id).IsEqualTo("a1");
                await Assert.That(rail.Groups[0].Sessions[1].IsSelected).IsTrue();
                await Assert.That(rail.Groups[0].Sessions[0].Id).IsEqualTo("a2");
                await Assert.That(rail.Groups[0].Sessions[1].Id).IsEqualTo("a1");
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Repository_collapse_survives_updates_and_group_recreation() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, _, rail) = Build();
            using (rail) {
                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha"));
                service.Agents.AddOrUpdate(Dto("b1", "/dev/beta"));
                var alpha = rail.Groups[0];
                await Assert.That(alpha.IsExpanded).IsTrue();
                alpha.ToggleCommand.Execute().Subscribe();
                service.Agents.AddOrUpdate(Dto("a2", "/dev/alpha"));
                rail.OpenSessionKey = "local:a2";
                await Assert.That(alpha.IsExpanded).IsFalse();
                await Assert.That(alpha.CountText).IsEqualTo("2 sessions");
                await Assert.That(rail.Groups[1].IsExpanded).IsTrue();
                service.Agents.RemoveKeys(["a1", "a2"]);
                service.Agents.AddOrUpdate(Dto("a3", "/dev/alpha"));
                var recreated = rail.Groups[0];
                await Assert.That(recreated.IsExpanded).IsFalse();
                recreated.ToggleCommand.Execute().Subscribe();
                service.Agents.RemoveKey("a3");
                service.Agents.AddOrUpdate(Dto("a4", "/dev/alpha"));
                await Assert.That(rail.Groups[0].IsExpanded).IsTrue();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Dispose_stops_tracking() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, _, rail) = Build();
            service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha"));
            rail.Dispose();
            service.Agents.AddOrUpdate(Dto("a2", "/dev/zeta"));
            await Assert.That(rail.Groups).Count().IsEqualTo(1);
        });
    }

    /// The window's close and the app's quit both clear the selection after the app has disposed
    /// the rail; that must not throw.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Selection_changes_after_dispose_are_ignored() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) {
                rail.OpenSessionKey = "server:s1";
                rail.Dispose();
                rail.OpenSessionKey = null;
                rail.OpenSessionKey = "local:a1";
                await Assert.That(sessions.Kept).IsEqualTo("s1");
            }
        });
    }

    /// A quit can still drive the rail's controls after the app has disposed it; that must not
    /// throw, reach the service or persist.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Control_changes_after_dispose_are_ignored() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, persisted, rail) = BuildWithControls();
            using (directory) using (sessions) {
                rail.Dispose();
                rail.SetScopeCommand.Execute(RailScope.Team).Subscribe();
                rail.Scope = RailScope.Team;
                rail.State = RailState.Ended;
                rail.Grouping = RailGrouping.Date;
                rail.Sort = RailSort.Title;
                rail.Descending = !rail.Descending;
                rail.ApplyPreferences(new RailPreferences(RailScope.Team, RailState.Ended, RailGrouping.Date, RailSort.Title, false, []));

                await Assert.That(sessions.Scope).IsEqualTo(RailScope.Mine);
                await Assert.That(persisted).IsEmpty();
            }
        });
    }

    /// A local checkout and a remote agent on the same GitHub repo land in one repo group — the
    /// remote session carries the machine badge, the local one doesn't.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task RemoteRowsGroupWithLocalRowsOfTheSameRepository() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, remote, rail) = Build(originUrl: _ => "git@github.com:kurrent-io/kcap-cli.git");
            using (rail) {
                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha"));
                remote.Cache.AddOrUpdate(RemoteDto("b1", "kurrent-io", "kcap-cli"));

                await Assert.That(rail.Groups).Count().IsEqualTo(1);
                await Assert.That(rail.Groups.Single().RootDisplay).IsEqualTo("kurrent-io/kcap-cli");
                var sessions = rail.Groups.Single().Sessions.ToList();
                await Assert.That(sessions).Count().IsEqualTo(2);

                var local = sessions.Single(s => s.Id == "a1");
                var remoteSession = sessions.Single(s => s.Id == "b1");
                await Assert.That(local.MachineBadge).IsNull();
                await Assert.That(remoteSession.MachineBadge).IsEqualTo("work-mac");
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_clicked_row_is_selected_before_its_open_runs_and_a_refused_open_reverts_it() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var paints = new Queue<Action>();
            var opened = new List<string>();
            var (directory, rail) = OpeningRail(paints, opened, refuse: "refused");
            using (directory)
            using (rail) {
                RailSessionViewModel Row(string id) => rail.Groups.Single().Sessions.Single(s => s.Id == id);

                Row("next").OpenCommand.Execute().Subscribe();
                await Assert.That(Row("next").IsSelected).IsTrue();
                await Assert.That(Row("open").IsSelected).IsFalse();
                await Assert.That(opened).IsEmpty();
                paints.Dequeue()();
                await Assert.That(opened).IsEquivalentTo(["next"]);
                await Assert.That(Row("next").IsSelected).IsTrue();

                Row("refused").OpenCommand.Execute().Subscribe();
                await Assert.That(Row("refused").IsSelected).IsTrue();
                paints.Dequeue()();
                await Assert.That(Row("refused").IsSelected).IsFalse();
                await Assert.That(Row("next").IsSelected).IsTrue();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_later_click_supersedes_an_open_not_yet_run() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var paints = new Queue<Action>();
            var opened = new List<string>();
            var (directory, rail) = OpeningRail(paints, opened, refuse: null);
            using (directory)
            using (rail) {
                RailSessionViewModel Row(string id) => rail.Groups.Single().Sessions.Single(s => s.Id == id);

                Row("next").OpenCommand.Execute().Subscribe();
                Row("refused").OpenCommand.Execute().Subscribe();
                while (paints.Count > 0) paints.Dequeue()();

                await Assert.That(opened).IsEquivalentTo(["refused"]);
                await Assert.That(Row("refused").IsSelected).IsTrue();
                await Assert.That(Row("next").IsSelected).IsFalse();
            }
        });
    }

    static (FakeAgentDirectory Directory, SessionRailViewModel Rail) OpeningRail(Queue<Action> paints, List<string> opened, string? refuse) {
        var directory = new FakeAgentDirectory();
        SessionRailViewModel? rail = null;
        rail = new SessionRailViewModel(directory, id => {
            opened.Add(id);
            if (id != refuse) rail!.OpenSessionKey = $"local:{id}";
        }, _ => { }, TimeProvider.System, afterPaint: paints.Enqueue);
        foreach (var id in new[] { "open", "next", "refused" })
            directory.Rows.AddOrUpdate(AgentRow.FromLocal(Dto(id, "/repo"), new RepoIdentity("path:/repo", "repo")));
        rail.OpenSessionKey = "local:open";
        return (directory, rail);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task RemoteRowOpensInWebNotInWorkspace() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            string? openedLocal = null;
            string? openedRemote = null;
            var (_, remote, rail) = Build(open: id => openedLocal = id, openRemote: id => openedRemote = id);
            using (rail) {
                remote.Cache.AddOrUpdate(RemoteDto("b1", "o", "r"));

                var session = rail.Groups.Single().Sessions.Single();
                session.OpenCommand.Execute().Subscribe();

                await Assert.That(openedRemote).IsEqualTo("b1");
                await Assert.That(openedLocal).IsNull();
            }
        });
    }

    /// A remote agent with no resolved GitHub identity falls to the "daemon:" group key — its
    /// RootDisplay names the checkout and the daemon, never the raw key.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task RootDisplay_names_a_daemon_scoped_repo_by_path_and_daemon() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, remote, rail) = Build();
            using (rail) {
                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha"));
                remote.Cache.AddOrUpdate(new AgentInstanceDto {
                    AgentId = "b1", Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1",
                    Vendor = "claude", RepoPath = "/srv/beta",
                });

                var alpha = rail.Groups.Single(r => r.Label == "alpha");
                await Assert.That(alpha.RootDisplay).IsEqualTo("/dev/alpha");

                var beta = rail.Groups.Single(r => r.Label == "beta");
                await Assert.That(beta.RootDisplay).IsEqualTo("/srv/beta on work-mac");
            }
        });
    }

    /// A group named by its folder says so; one named by a repository's remote does not.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_folder_group_reads_as_a_folder_and_a_remote_repository_does_not() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, remote, rail) = Build();
            using (rail) {
                service.Agents.AddOrUpdate(Dto("a1", "/dev/scratch"));
                remote.Cache.AddOrUpdate(RemoteDto("b1", "kurrent-io", "kcap-cli"));

                var folder = rail.Groups.Single(r => r.Label == "scratch");
                await Assert.That(folder.IsFolder).IsTrue();
                await Assert.That(folder.Tooltip).StartsWith("/dev/scratch\nFolder, not a repository with a remote");
                await Assert.That(folder.ToggleAccessibleName).Contains(" folder scratch");

                var repository = rail.Groups.Single(r => r.Label == "kurrent-io/kcap-cli");
                await Assert.That(repository.IsFolder).IsFalse();
                await Assert.That(repository.ToggleAccessibleName).DoesNotContain("folder");
            }
        });
    }

    // ── controls, grouping, sorting, paging ────────────────────────────────────────────────────

    static readonly DateTime Noon = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    static AgentRow Server(string id, string status = "ended", DateTime? lastActivity = null, string? title = null, string? repoName = "kcap-cli") =>
        AgentRow.FromServer(new RepoSessionDto {
            SessionId = id, Status = status, AccessLevel = "full", Vendor = "claude", Title = title ?? id,
            Owner = new() { UserId = "me", DisplayName = "Alexey" },
            StartedAt = new DateTimeOffset((lastActivity ?? Noon).AddHours(-1)),
            LastActivityAt = new DateTimeOffset(lastActivity ?? Noon),
            EndedAt = status == "ended" ? new DateTimeOffset(lastActivity ?? Noon) : null,
            Repo = repoName is null ? null : new() { Hash = "h", Owner = "kurrent-io", Name = repoName },
        }, hostedPresentation: false);

    static AgentRow Local(string id, string status = "Running", DateTime? created = null, string? title = null, bool? awaiting = null) =>
        AgentRow.FromLocal(Dto(id, "/repo", status, created ?? Noon) with { Title = title ?? id, AwaitingInput = awaiting }, new RepoIdentity("repo:kurrent-io/kcap-cli", "kurrent-io/kcap-cli"));

    static (FakeAgentDirectory Directory, FakeServerSessionsService Sessions, Microsoft.Extensions.Time.Testing.FakeTimeProvider Time, List<RailPreferences> Persisted, SessionRailViewModel Rail)
        BuildWithControls(RailPreferences? preferences = null) {
        var directory = new FakeAgentDirectory();
        var sessions = new FakeServerSessionsService();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(Noon));
        var persisted = new List<RailPreferences>();
        var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, time,
            openServerSession: _ => { }, sessions: sessions, preferences: preferences, persistPreferences: persisted.Add);
        return (directory, sessions, time, persisted, rail);
    }

    static IEnumerable<string> Labels(SessionRailViewModel rail) => rail.Groups.Select(g => g.Label);
    static IEnumerable<string> Ids(RailGroupViewModel group) => group.Sessions.Select(s => s.Id);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_rail_is_empty_when_the_state_filter_leaves_no_rows() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate(Server("ended"));
                await Assert.That(rail.IsEmpty).IsFalse();

                rail.State = RailState.Live;

                await Assert.That(rail.IsEmpty).IsTrue();
            }
        });
    }

    /// The PR tone cache reads only what this filter admits, so it must follow the state filter and
    /// the selected row the rail shows whatever the filter says.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_rail_publishes_the_rows_it_shows() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            var shown = new System.Reactive.Subjects.ReplaySubject<Func<AgentRow, bool>>(1);
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System, shownRows: shown);
            var ended = Server("ended");
            Func<AgentRow, bool>? current = null;
            using var _s = shown.Subscribe(filter => current = filter);
            await Assert.That(current!(ended)).IsTrue();

            rail.State = RailState.Live;
            await Assert.That(current!(ended)).IsFalse();

            rail.OpenSessionKey = ended.Key;
            await Assert.That(current!(ended)).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Selecting_a_server_row_keeps_its_session_in_the_listing() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                rail.OpenSessionKey = "server:s1";
                await Assert.That(sessions.Kept).IsEqualTo("s1");
                rail.OpenSessionKey = "local:a1";
                await Assert.That(sessions.Kept).IsNull();
            }
        });
    }

    /// Two viewers in a row: the earlier one's read, however late, is not the shape to show.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_late_preferences_load_for_an_earlier_viewer_is_ignored() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var directory = new FakeAgentDirectory();
            var sessions = new FakeServerSessionsService();
            var loads = new Dictionary<string, TaskCompletionSource<RailPreferences>>();
            var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System, sessions: sessions,
                bindUserPreferences: id => (loads[id] = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task);
            using (directory) using (sessions) using (rail) {
                sessions.ViewerUserIdSubject.OnNext("u1");
                sessions.ViewerUserIdSubject.OnNext("u2");
                loads["u2"].SetResult(RailPreferences.Default with { Scope = RailScope.Team });
                await WorkspaceFixtures.WaitUntilAsync(() => rail.Scope == RailScope.Team, what: "the later viewer's shape");

                loads["u1"].SetResult(RailPreferences.Default with { Grouping = RailGrouping.Date });

                await Task.Delay(100);
                await Assert.That(rail.Scope).IsEqualTo(RailScope.Team);
                await Assert.That(rail.Grouping).IsEqualTo(RailGrouping.Repository);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_named_viewer_applies_that_users_remembered_shape_without_persisting_it() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var directory = new FakeAgentDirectory();
            var sessions = new FakeServerSessionsService();
            var persisted = new List<RailPreferences>();
            var bound = new List<string>();
            var teams = RailPreferences.Default with { Scope = RailScope.Team, Grouping = RailGrouping.Date };
            var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System,
                sessions: sessions, persistPreferences: persisted.Add,
                bindUserPreferences: id => { bound.Add(id); return Task.FromResult(teams); });
            using (directory) using (sessions) using (rail) {
                sessions.ViewerUserIdSubject.OnNext("u1");

                await WorkspaceFixtures.WaitUntilAsync(() => rail.Scope == RailScope.Team, what: "the viewer's shape applied");
                await Assert.That(rail.Grouping).IsEqualTo(RailGrouping.Date);
                await Assert.That(sessions.Scope).IsEqualTo(RailScope.Team);
                await Assert.That(bound).IsEquivalentTo(new[] { "u1" });
                await Assert.That(persisted).IsEmpty();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_locked_scope_stays_on_mine() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var directory = new FakeAgentDirectory();
            var sessions = new FakeServerSessionsService();
            var teams = RailPreferences.Default with { Scope = RailScope.Team, Grouping = RailGrouping.Date };
            var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System,
                sessions: sessions, preferences: teams with { Grouping = RailGrouping.Repository },
                scopeLocked: true, bindUserPreferences: _ => Task.FromResult(teams));
            using (directory) using (sessions) using (rail) {
                await Assert.That(rail.Scope).IsEqualTo(RailScope.Mine);
                await Assert.That(sessions.Scope).IsEqualTo(RailScope.Mine);
                await Assert.That(rail.CanChangeScope).IsFalse();
                await Assert.That(rail.ScopeTip).IsEqualTo(SessionRailViewModel.ScopeLockedTip);

                sessions.ViewerUserIdSubject.OnNext("u1");
                await WorkspaceFixtures.WaitUntilAsync(() => rail.Grouping == RailGrouping.Date, what: "the viewer's shape applied");
                await Assert.That(rail.Scope).IsEqualTo(RailScope.Mine);

                rail.SetScopeCommand.Execute(RailScope.Team).Subscribe();
                await Assert.That(rail.Scope).IsEqualTo(RailScope.Mine);
                await Assert.That(sessions.Scope).IsEqualTo(RailScope.Mine);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Date_grouping_names_today_and_yesterday_and_regroups_at_midnight() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, time, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate(Server("today", lastActivity: Noon.AddHours(-3)));
                directory.Rows.AddOrUpdate(Server("yesterday", lastActivity: Noon.AddHours(-14)));

                rail.Grouping = RailGrouping.Date;
                await Assert.That(Labels(rail)).IsEquivalentTo(["Today", "Yesterday"], CollectionOrdering.Matching);
                await Assert.That(rail.Groups[0].Kind).IsEqualTo(RailGrouping.Date);

                time.Advance(TimeSpan.FromHours(12).Add(TimeSpan.FromMinutes(1)));
                await Assert.That(Labels(rail)).IsEquivalentTo(["Yesterday", "Fri 23 Oct"], CollectionOrdering.Matching);
                await Assert.That(rail.Groups[0].RootDisplay).IsEqualTo("Yesterday");
                await Assert.That(rail.Groups[0].Tooltip).StartsWith("Yesterday");
            }
        });
    }

    /// The day key is a Gregorian date whatever calendar the user's culture counts in: it is parsed
    /// back invariantly for the label and persisted as the collapsed-group key.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Date_groups_key_on_the_gregorian_date_under_a_buddhist_calendar_culture() {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("th-TH");
        try {
            await AvaloniaSession.WithImmediateRxScheduler(async () => {
                var (directory, sessions, _, _, rail) = BuildWithControls();
                using (directory) using (sessions) using (rail) {
                    directory.Rows.AddOrUpdate(Server("today", lastActivity: Noon.AddHours(-3)));

                    rail.Grouping = RailGrouping.Date;

                    await Assert.That(rail.Groups[0].Key).IsEqualTo("day:2026-10-24");
                    await Assert.That(rail.Groups[0].Label).IsEqualTo("Today");
                }
            });
        } finally {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Status_grouping_orders_waiting_running_failed_ended_and_omits_empty_groups() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate(Server("ended"));
                directory.Rows.AddOrUpdate(Local("failed", status: "Failed"));
                directory.Rows.AddOrUpdate(Local("running", awaiting: false));
                directory.Rows.AddOrUpdate(Local("waiting", awaiting: true));

                rail.Grouping = RailGrouping.Status;
                await Assert.That(Labels(rail)).IsEquivalentTo(["Waiting on you", "Running", "Failed", "Ended"], CollectionOrdering.Matching);

                directory.Rows.RemoveKey("local:failed");
                await Assert.That(Labels(rail)).IsEquivalentTo(["Waiting on you", "Running", "Ended"], CollectionOrdering.Matching);
            }
        });
    }

    /// The chip reads Working for a subagent the daemon has lost to a pause, so the group must say
    /// Running too, and the row goes back once the subagent ends.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Status_grouping_follows_work_only_the_transcript_sees() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var sessions = new FakeServerSessionsService();
            var work = new BehaviorSubject<IReadOnlyDictionary<string, RunningWork>>(FrozenDictionary<string, RunningWork>.Empty);
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { },
                new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(Noon)),
                agentsRunningWork: work, openServerSession: _ => { }, sessions: sessions);
            directory.Rows.AddOrUpdate(Local("paused", awaiting: true));
            rail.Grouping = RailGrouping.Status;
            await Assert.That(Labels(rail)).IsEquivalentTo(["Waiting on you"], CollectionOrdering.Matching);

            work.OnNext(new Dictionary<string, RunningWork> { ["local:paused"] = new(0, 1) });
            await Assert.That(Labels(rail)).IsEquivalentTo(["Running"], CollectionOrdering.Matching);

            work.OnNext(FrozenDictionary<string, RunningWork>.Empty);
            await Assert.That(Labels(rail)).IsEquivalentTo(["Waiting on you"], CollectionOrdering.Matching);
        });
    }

    /// The chip reads Working for a parent waiting on input with a subagent still running, so the
    /// group must say Running too.
    [Test]
    public async Task Status_grouping_puts_a_waiting_parent_with_a_live_subagent_under_running() {
        var row = Local("parent", awaiting: true) with { LiveSubagents = 1 };

        await Assert.That(SessionStatusDots.Label(row)).IsEqualTo("Working");
        await Assert.That(SessionRailViewModel.GroupKey(row, RailGrouping.Status, DateOnly.FromDateTime(Noon), TimeZoneInfo.Utc))
            .IsEqualTo("status:1");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task None_grouping_is_one_headerless_group() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate(Server("e1"));
                directory.Rows.AddOrUpdate(Local("a1"));

                rail.Grouping = RailGrouping.None;
                var group = rail.Groups.Single();
                await Assert.That(group.ShowsHeader).IsFalse();
                await Assert.That(group.Sessions.Count).IsEqualTo(2);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Sort_by_title_ascending_orders_within_a_group_and_descending_flips_it() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate(Server("1", title: "b"));
                directory.Rows.AddOrUpdate(Server("2", title: "A"));
                directory.Rows.AddOrUpdate(Server("3", title: "c"));

                rail.Sort = RailSort.Title;
                rail.Descending = false;
                await Assert.That(Ids(rail.Groups.Single())).IsEquivalentTo(["2", "1", "3"], CollectionOrdering.Matching);
                rail.Descending = true;
                await Assert.That(Ids(rail.Groups.Single())).IsEquivalentTo(["3", "1", "2"], CollectionOrdering.Matching);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Last_updated_sort_uses_last_activity_then_created() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate(Server("s1", lastActivity: Noon.AddHours(-2)));
                directory.Rows.AddOrUpdate(Server("s2", lastActivity: Noon.AddHours(-1)));
                directory.Rows.AddOrUpdate(Local("a1", created: Noon));

                await Assert.That(Ids(rail.Groups.Single())).IsEquivalentTo(["a1", "s2", "s1"], CollectionOrdering.Matching);
                rail.Descending = false;
                await Assert.That(Ids(rail.Groups.Single())).IsEquivalentTo(["s1", "s2", "a1"], CollectionOrdering.Matching);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Live_filter_hides_ended_rows_except_the_selected_one() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate(Local("a1"));
                directory.Rows.AddOrUpdate(Server("e1"));
                directory.Rows.AddOrUpdate(Server("e2"));

                rail.State = RailState.Live;
                await Assert.That(Ids(rail.Groups.Single())).IsEquivalentTo(["a1"]);
                rail.OpenSessionKey = "server:e1";
                await Assert.That(Ids(rail.Groups.Single()).OrderBy(id => id, StringComparer.Ordinal)).IsEquivalentTo(["a1", "e1"], CollectionOrdering.Matching);

                rail.State = RailState.Ended;
                await Assert.That(Ids(rail.Groups.Single()).OrderBy(id => id, StringComparer.Ordinal)).IsEquivalentTo(["e1", "e2"], CollectionOrdering.Matching);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task View_all_shows_while_the_listing_holds_older_ended_sessions_the_filter_admits() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                await Assert.That(rail.ShowsViewAll).IsFalse();

                sessions.PagingSubject.OnNext(new ServerSessionsPaging(20, 84, 1, true, false));
                await Assert.That(rail.ShowsViewAll).IsTrue();

                rail.State = RailState.Live;
                await Assert.That(rail.ShowsViewAll).IsFalse();

                rail.State = RailState.Ended;
                await Assert.That(rail.ShowsViewAll).IsTrue();

                sessions.PagingSubject.OnNext(new ServerSessionsPaging(4, 4, 1, false, false));
                await Assert.That(rail.ShowsViewAll).IsFalse();
            }
        });
    }

    /// The ended list loads a page at a time, so a group's ended count is a floor until every page is in.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_group_count_reads_as_a_floor_while_ended_pages_remain() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate([Server("e1"), Server("e2"), Server("e3"), Server("e4")]);
                sessions.PagingSubject.OnNext(new ServerSessionsPaging(20, 106, 1, true, false));
                var group = rail.Groups.Single();
                await Assert.That(group.CountText).IsEqualTo("4+ sessions");
                await Assert.That(group.Tooltip).Contains("Counts cover the ended sessions loaded so far");

                rail.State = RailState.Live;
                rail.State = RailState.Ended;
                await Assert.That(rail.Groups.Single().CountText).IsEqualTo("4+ sessions");

                sessions.PagingSubject.OnNext(new ServerSessionsPaging(106, 106, 6, false, false));
                group = rail.Groups.Single();
                await Assert.That(group.CountText).IsEqualTo("4 sessions");
                await Assert.That(group.Tooltip).DoesNotContain("Counts cover");
            }
        });
    }

    /// A group with only live rows loaded can still have ended sessions on a later page.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_live_only_group_count_reads_as_a_floor_while_ended_pages_remain() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate([Local("a"), Local("b")]);
                sessions.PagingSubject.OnNext(new ServerSessionsPaging(20, 106, 1, true, false));
                await Assert.That(rail.Groups.Single().CountText).IsEqualTo("2+ sessions");

                rail.State = RailState.Live;
                await Assert.That(rail.Groups.Single().CountText).IsEqualTo("2 sessions");
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Live_not_shown_renders_the_remainder_line() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                await Assert.That(rail.LiveNotShownText).IsNull();
                sessions.LiveNotShownSubject.OnNext(100);
                await Assert.That(rail.LiveNotShownText).IsEqualTo("100 more live sessions · narrow the scope");
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Changing_a_control_persists_the_preferences_and_scope_reaches_the_service() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, persisted, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate(Local("a1"));

                rail.Scope = RailScope.Team;
                await Assert.That(persisted[^1].Scope).IsEqualTo(RailScope.Team);
                await Assert.That(sessions.Scope).IsEqualTo(RailScope.Team);

                rail.Grouping = RailGrouping.Date;
                await Assert.That(persisted[^1].Grouping).IsEqualTo(RailGrouping.Date);

                await rail.Groups.Single().ToggleCommand.Execute();
                await Assert.That(persisted[^1].Collapsed).IsEquivalentTo([rail.Groups.Single().Key]);
                await Assert.That(rail.Preferences).IsEqualTo(persisted[^1]);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Apply_preferences_rearranges_without_persisting() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, persisted, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate(Server("e1"));

                rail.ApplyPreferences(new RailPreferences(RailScope.Team, RailState.Ended, RailGrouping.Date, RailSort.Title, false, []));

                await Assert.That(rail.Grouping).IsEqualTo(RailGrouping.Date);
                await Assert.That(rail.Sort).IsEqualTo(RailSort.Title);
                await Assert.That(rail.Descending).IsFalse();
                await Assert.That(sessions.Scope).IsEqualTo(RailScope.Team);
                await Assert.That(Labels(rail)).IsEquivalentTo(["Today"]);
                await Assert.That(persisted).IsEmpty();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Initial_preferences_shape_the_rail_and_remember_collapsed_groups() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var prefs = new RailPreferences(RailScope.Mine, RailState.All, RailGrouping.Repository, RailSort.Created, true, ["repo:kurrent-io/kcap-cli"]);
            var (directory, sessions, _, _, rail) = BuildWithControls(prefs);
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate(Local("a1"));

                await Assert.That(rail.Sort).IsEqualTo(RailSort.Created);
                await Assert.That(rail.Groups.Single().IsExpanded).IsFalse();
            }
        });
    }

    // ── work items ─────────────────────────────────────────────────────────────────────────────

    static RepoSessionDto Tracked(string id, string? workItem, DateTime lastActivity, string status = "ended", DateTime? workItemCreated = null) => new() {
        SessionId = id, Status = status, AccessLevel = "full", Vendor = "claude", Title = id,
        Owner = new() { UserId = "me", DisplayName = "Alexey" },
        StartedAt = new DateTimeOffset(lastActivity.AddHours(-1)), LastActivityAt = new DateTimeOffset(lastActivity),
        EndedAt = status == "ended" ? new DateTimeOffset(lastActivity) : null,
        Repo = new() { Hash = "h", Owner = "kurrent-io", Name = "kcap-cli" },
        WorkItem = workItem is null ? null : new() { WorkItemId = workItem, Title = workItem, CreatedAt = workItemCreated is { } at ? new DateTimeOffset(at) : null },
    };

    static (FakeAgentDirectory Directory, FakeServerSessionsService Sessions, SessionRailViewModel Rail) BuildWithWorkItems(
            Func<string, string, Task<bool>>? fileSession = null, Func<string, string, Task<bool>>? unfileSession = null) {
        var directory = new FakeAgentDirectory();
        var sessions = new FakeServerSessionsService();
        var rail = new SessionRailViewModel(directory, _ => { }, _ => { },
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(Noon)),
            openServerSession: _ => { }, sessions: sessions,
            preferences: RailPreferences.Default with { Grouping = RailGrouping.WorkItem, Project = new RailProject("p", "P", ["h"]) },
            readWorkItems: (_, _) => Task.FromResult<RailWorkItems?>(new(true, [])),
            fileSession: fileSession, unfileSession: unfileSession);
        return (directory, sessions, rail);
    }

    static void List(FakeAgentDirectory directory, FakeServerSessionsService sessions, params RepoSessionDto[] listed) {
        foreach (var dto in listed) {
            (dto.Status == "active" ? sessions.LiveCache : sessions.EndedCache).AddOrUpdate(dto);
            directory.Rows.AddOrUpdate(AgentRow.FromServer(dto, hostedPresentation: false));
        }
    }

    /// A row dropped on another work item is filed there, dropped out it leaves its work item, and where it already is nothing happens.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Dropping_a_row_files_it_under_a_work_item_or_out_of_one() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var calls = new List<string>();
            var (directory, sessions, rail) = BuildWithWorkItems(
                (item, id) => { calls.Add($"file {item} {id}"); return Task.FromResult(true); },
                (item, id) => { calls.Add($"unfile {item} {id}"); return Task.FromResult(true); });
            using (directory) using (sessions) using (rail) {
                List(directory, sessions, Tracked("a", "AI-1", Noon.AddHours(-1)), Tracked("b", "AI-2", Noon.AddHours(-2)),
                    Tracked("loose", null, Noon.AddHours(-3)));
                IReadOnlyList<RailSessionViewModel> Row(string id) => [rail.Groups.SelectMany(g => g.Sessions).Single(s => s.Row.SessionId == id)];

                rail.DropOn(Row("a"), "AI-2")!();
                rail.DropOn(Row("a"), null)!();
                rail.DropOn(Row("loose"), "AI-1")!();

                await Assert.That(rail.DropOn(Row("a"), "AI-1") is null).IsTrue();
                await Assert.That(rail.DropOn(Row("loose"), null) is null).IsTrue();
                await Assert.That(calls).IsEquivalentTo(["file AI-2 a", "unfile AI-1 a", "file AI-1 loose"],
                    TUnit.Assertions.Enums.CollectionOrdering.Matching);
            }
        });
    }

    /// A work item nobody has worked on for three days leaves the rail's work items, and its sessions fall back to their days.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Only_work_items_worked_on_lately_head_the_rail() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, rail) = BuildWithWorkItems();
            using (directory) using (sessions) using (rail) {
                List(directory, sessions, Tracked("recent", "AI-1", Noon.AddDays(-2)), Tracked("old", "AI-2", Noon.AddDays(-4)));

                var workItems = rail.Groups.Where(g => g.IsWorkItem).Select(g => g.Label);
                await Assert.That(workItems).IsEquivalentTo(new[] { "AI-1" });
                await Assert.That(rail.Groups.Where(g => !g.IsWorkItem).SelectMany(Ids)).Contains("old");
            }
        });
    }

    /// A dropped row moves into its work item, opened, before the server answers, and moves back when the server refuses the drop.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_drop_shows_at_once_and_a_refused_one_moves_back() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var (directory, sessions, rail) = BuildWithWorkItems((_, _) => answer.Task);
            using (directory) using (sessions) using (rail) {
                List(directory, sessions, Tracked("a", "AI-1", Noon.AddHours(-1)), Tracked("loose", null, Noon.AddHours(-3)));
                var loose = rail.Groups.SelectMany(g => g.Sessions).Single(s => s.Row.SessionId == "loose");

                rail.DropOn([loose], "AI-1")!();
                var shown = Ids(rail.Groups.Single(g => g.WorkItemId == "AI-1")).ToList();
                var expanded = rail.Groups.Single(g => g.WorkItemId == "AI-1").IsExpanded;
                answer.SetResult(false);

                await Assert.That(shown).Contains("loose");
                await Assert.That(expanded).IsTrue();
                await WorkspaceFixtures.WaitUntilAsync(() => !Ids(rail.Groups.Single(g => g.WorkItemId == "AI-1")).Contains("loose"),
                    what: "the refused drop moved back");
            }
        });
    }

    /// Work items sort newest created first, so a newer or live session filed under an older one doesn't move it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Work_items_keep_their_created_order_whatever_their_sessions_do() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, rail) = BuildWithWorkItems();
            using (directory) using (sessions) using (rail) {
                List(directory, sessions, Tracked("a", "AI-1", Noon.AddHours(-1), status: "active", workItemCreated: Noon.AddDays(-2)),
                    Tracked("b", "AI-2", Noon.AddHours(-2), workItemCreated: Noon.AddDays(-1)));

                await Assert.That(rail.Groups.Where(g => g.IsWorkItem).Select(g => g.Label))
                    .IsEquivalentTo(["AI-2", "AI-1"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            }
        });
    }

    /// A listing that changes a work item but not the order of work items leaves the groups in place rather than rebuilding them.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_work_item_change_that_keeps_their_order_does_not_rebuild_the_groups() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, rail) = BuildWithWorkItems();
            using (directory) using (sessions) using (rail) {
                List(directory, sessions, Tracked("a", "AI-1", Noon.AddHours(-1)), Tracked("b", "AI-2", Noon.AddHours(-2)));
                var resets = 0;
                ((System.Collections.Specialized.INotifyCollectionChanged)rail.Groups).CollectionChanged += (_, e) => {
                    if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++;
                };

                List(directory, sessions, Tracked("c", "AI-2", Noon.AddHours(-3)));

                await Assert.That(Ids(rail.Groups.Single(g => g.Label == "AI-2"))).Contains("c");
                await Assert.That(resets).IsEqualTo(0);
            }
        });
    }

    /// A work item shows its five most recent sessions plus any live or open one, and counts the rest for its page.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_work_item_shows_its_recent_live_and_open_sessions() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, rail) = BuildWithWorkItems();
            using (directory) using (sessions) using (rail) {
                List(directory, sessions, [
                    .. Enumerable.Range(1, 8).Select(i => Tracked($"s{i}", "AI-1", Noon.AddHours(-i))),
                    Tracked("live", "AI-1", Noon.AddDays(-1), status: "active"),
                ]);
                rail.OpenSessionKey = "server:s8";

                var group = rail.Groups.Single(g => g.IsWorkItem);
                await Assert.That(Ids(group)).IsEquivalentTo(new[] { "s1", "s2", "s3", "s4", "s5", "live", "s8" });
                await Assert.That(group.MoreText).IsEqualTo("2 more");
            }
        });
    }

    /// A work item's page over the open session selects the work item's row, and the session row
    /// stays listed without reading selected until the page closes.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_open_work_item_page_selects_its_row_over_the_open_session() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, rail) = BuildWithWorkItems();
            using (directory) using (sessions) using (rail) {
                List(directory, sessions, [.. Enumerable.Range(1, 8).Select(i => Tracked($"s{i}", "AI-1", Noon.AddHours(-i)))]);
                rail.OpenSessionKey = "server:s8";
                var group = rail.Groups.Single(g => g.IsWorkItem);
                RailSessionViewModel Open() => group.Sessions.Single(s => s.Id == "s8");

                rail.OpenWorkItemId = "AI-1";
                await Assert.That(group.IsSelected).IsTrue();
                await Assert.That(Open().IsSelected).IsFalse();

                rail.OpenWorkItemId = null;
                await Assert.That(group.IsSelected).IsFalse();
                await Assert.That(Open().IsSelected).IsTrue();
            }
        });
    }
}
