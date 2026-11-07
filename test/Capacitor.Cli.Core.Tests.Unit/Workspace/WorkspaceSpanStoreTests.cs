using System.Diagnostics;
using System.Text.Json;
using Capacitor.Cli.Core.Workspace;

namespace Capacitor.Cli.Core.Tests.Unit.Workspace;

[ParallelLimiter<SubprocessLimit>]
public class WorkspaceSpanStoreTests {
    [Test]
    public async Task A_second_open_before_close_ends_the_earlier_turn_instead_of_losing_it() {
        using var rig = new WorkspaceRig();

        await rig.OpenAsync();
        rig.Write("first-turn.txt", "x");
        await rig.OpenAsync();
        rig.Write("second-turn.txt", "x");
        await rig.CloseAsync();

        var spans = rig.Spooled().Select(r => r.Changes.Select(c => c.Path).Single()).Order().ToList();
        await Assert.That(spans).IsEquivalentTo(["first-turn.txt", "second-turn.txt"]);
    }

    [Test]
    public async Task A_close_with_no_open_span_posts_nothing() {
        using var rig = new WorkspaceRig();
        rig.Write("unwatched.txt", "x");

        await Assert.That(await rig.CloseAsync()).IsTrue();

        await Assert.That(rig.SpoolFiles()).IsEmpty();
    }

    [Test]
    public async Task A_span_with_no_changes_posts_nothing() {
        using var rig = new WorkspaceRig();

        var spooled = await rig.SpanAsync(() => { });

        await Assert.That(spooled).IsEmpty();
        await Assert.That(rig.Baselines()).IsEmpty();
    }

    [Test]
    public async Task A_turn_that_moves_to_another_worktree_posts_both_trees() {
        using var rig = new WorkspaceRig();
        var b = rig.Repo.AddWorktree(rig.Repo.PathTo("..", Path.GetFileName(rig.Repo.Path) + "-b"), "b");
        try {
            await rig.OpenAsync();
            rig.Write("in-a.txt", "x");
            await Assert.That(await rig.OpenAsync(b.Path, WorkspaceOpen.Join)).IsTrue();
            b.CreateFile("in-b.txt", "x");
            await rig.CloseAsync();

            var byRoot = rig.Spooled().ToDictionary(r => r.RepoRoot, r => r.Changes.Select(c => c.Path).Single());
            await Assert.That(byRoot).IsEquivalentTo(new Dictionary<string, string> {
                [Toplevel(rig.Repo)] = "in-a.txt", [Toplevel(b)] = "in-b.txt",
            });
        } finally {
            rig.Repo.Try("worktree", "remove", "--force", b.Path);
        }
    }

    [Test]
    public async Task Joining_a_tree_that_already_has_a_span_keeps_its_baseline() {
        using var rig = new WorkspaceRig();

        await rig.OpenAsync();
        rig.Write("before-join.txt", "x");
        await rig.OpenAsync(mode: WorkspaceOpen.Join);
        await rig.CloseAsync();

        await Assert.That(rig.Spooled().SelectMany(r => r.Changes).Select(c => c.Path)).IsEquivalentTo(["before-join.txt"]);
    }

    [Test]
    public async Task Outside_a_repository_nothing_opens() {
        using var rig = new WorkspaceRig();
        using var plain = new TempDir("ws-plain");

        await Assert.That(await rig.OpenAsync(plain.Path)).IsFalse();
        await Assert.That(rig.Baselines()).IsEmpty();
    }

    [Test]
    public async Task An_open_while_another_process_holds_the_lock_skips_the_span() {
        using var rig = new WorkspaceRig();
        var store = rig.Store;
        var quick = new WorkspaceSpanStore(rig.Config.Root, WorkspaceRig.Server, rig.Spool,
            new WorkspaceSnapshotter(new WorkspaceGit(rig.Time), rig.Time), rig.Time) { LockWait = TimeSpan.FromMilliseconds(50) };

        using (WorkspaceSpanLock.TryAcquire(rig.SessionSpanDir, TimeSpan.Zero)) {
            await Assert.That(await quick.OpenAsync(WorkspaceRig.Session, rig.Repo.Path, WorkspaceOpen.Turn, WorkspaceRig.Budget)).IsFalse();
        }

        await Assert.That(await store.OpenAsync(WorkspaceRig.Session, rig.Repo.Path, WorkspaceOpen.Turn, WorkspaceRig.Budget)).IsTrue();
    }

    [Test]
    public async Task Overlapping_opens_and_closes_leave_every_span_closed_exactly_once() {
        using var rig = new WorkspaceRig();
        var stores = Enumerable.Range(0, 4).Select(_ => rig.StoreFor(WorkspaceRig.Server, rig.Spool)).ToList();

        await Task.WhenAll(stores.Select(async (store, i) => {
            await Task.Yield();
            rig.Write($"turn-{i}.txt", "x");
            if (i % 2 == 0) await store.OpenAsync(WorkspaceRig.Session, rig.Repo.Path, WorkspaceOpen.Turn, WorkspaceRig.Budget);
            else await store.CloseAllAsync(WorkspaceRig.Session, WorkspaceRig.Budget);
        }));
        await rig.CloseAsync();

        var spanIds = rig.Spooled().Select(r => r.SpanId).ToList();
        await Assert.That(spanIds.Distinct().Count()).IsEqualTo(spanIds.Count);
        await Assert.That(rig.Baselines()).IsEmpty();
    }

    [Test]
    public async Task A_lock_held_by_a_process_that_died_is_acquired() {
        Skip.When(OperatingSystem.IsWindows(), "the holder is a perl flock, and the Windows runners have no perl");
        using var rig = new WorkspaceRig();
        Directory.CreateDirectory(rig.SessionSpanDir);
        var lockFile = Path.Combine(rig.SessionSpanDir, ".lock");

        using var holder = Process.Start(new ProcessStartInfo("perl", [
            "-e", "use Fcntl qw(:flock); open(my $f, '>>', $ARGV[0]) or die; flock($f, LOCK_EX) or die; $| = 1; print \"held\\n\"; sleep 60;", lockFile,
        ]) { RedirectStandardOutput = true, UseShellExecute = false })!;
        await Assert.That(await holder.StandardOutput.ReadLineAsync()).IsEqualTo("held");

        await Assert.That(WorkspaceSpanLock.TryAcquire(rig.SessionSpanDir, TimeSpan.FromMilliseconds(100))).IsNull();

        holder.Kill();
        await holder.WaitForExitAsync();

        using var acquired = WorkspaceSpanLock.TryAcquire(rig.SessionSpanDir, TimeSpan.FromSeconds(1));
        await Assert.That(acquired).IsNotNull();
    }

    [Test]
    public async Task A_baseline_whose_span_was_already_spooled_is_deleted_without_closing_again() {
        using var rig = new WorkspaceRig();
        await rig.OpenAsync();
        rig.Write("done.txt", "x");
        var span = JsonSerializer.Deserialize(File.ReadAllText(rig.Baselines().Single()), WorkspaceJsonContext.Default.WorkspaceSpan)!;
        // A hook killed after spooling the span and before deleting its baseline.
        var spooled = new WorkspaceChangesRequest(WorkspaceRig.Session, span.SpanId, span.RepoRoot, span.OpenedAt,
            span.OpenedAt, [new WorkspaceChange("done.txt", "create")], false);
        rig.Spool.Write(spooled);
        var before = File.ReadAllText(rig.SpoolFiles().Single());

        await rig.CloseAsync();

        await Assert.That(rig.Baselines()).IsEmpty();
        await Assert.That(rig.SpoolFiles()).HasSingleItem();
        await Assert.That(File.ReadAllText(rig.SpoolFiles().Single())).IsEqualTo(before);
    }

    [Test]
    public async Task A_span_over_the_event_size_is_cut_off_and_marked_truncated() {
        using var rig = new WorkspaceRig(WorkspaceBounds.Default with { EventBytes = 2_048 });

        var spooled = await rig.SpanAsync(() => {
            for (var i = 0; i < 200; i++) rig.Write($"generated/file-{i:D3}.txt", "x");
        });

        var request = spooled.Single();
        await Assert.That(request.Truncated).IsTrue();
        await Assert.That(request.Changes.Count).IsBetween(1, 199);
        await Assert.That(File.ReadAllBytes(rig.SpoolFiles().Single()).Length).IsLessThanOrEqualTo(2_048);
    }

    [Test]
    public async Task A_temp_file_left_by_a_killed_spool_write_is_ignored_and_the_span_closed_again() {
        using var rig = new WorkspaceRig();
        await rig.OpenAsync();
        rig.Write("survives.txt", "x");
        var span = JsonSerializer.Deserialize(File.ReadAllText(rig.Baselines().Single()), WorkspaceJsonContext.Default.WorkspaceSpan)!;
        var sessionSpool = Path.Combine(rig.Spool.DirectoryPath, WorkspaceRig.Session);
        Directory.CreateDirectory(sessionSpool);
        File.WriteAllText(Path.Combine(sessionSpool, $"{span.SpanId:N}.json.{Guid.NewGuid():N}.tmp"), "{\"torn");

        await rig.CloseAsync();

        var request = rig.Spooled().Single();
        await Assert.That(request.SpanId).IsEqualTo(span.SpanId);
        await Assert.That(request.Changes.Select(c => c.Path)).IsEquivalentTo(["survives.txt"]);
    }

    [Test]
    public async Task A_span_older_than_a_day_is_discarded_instead_of_closed() {
        using var rig = new WorkspaceRig();
        await rig.OpenAsync();
        rig.Write("stale.txt", "x");

        rig.Time.Advance(TimeSpan.FromHours(25));
        await rig.CloseAsync();

        await Assert.That(rig.SpoolFiles()).IsEmpty();
        await Assert.That(rig.Baselines()).IsEmpty();
    }

    [Test]
    public async Task A_span_whose_tree_is_gone_is_discarded() {
        using var rig = new WorkspaceRig();
        var b = rig.Repo.AddWorktree(rig.Repo.PathTo("..", Path.GetFileName(rig.Repo.Path) + "-gone"), "gone");
        await rig.OpenAsync(b.Path);
        rig.Repo.Do("worktree", "remove", "--force", b.Path);

        await rig.CloseAsync();

        await Assert.That(rig.SpoolFiles()).IsEmpty();
        await Assert.That(rig.Baselines()).IsEmpty();
    }

    [Test]
    public async Task Ending_the_session_closes_its_spans_and_deletes_the_baselines() {
        using var rig = new WorkspaceRig();
        await rig.OpenAsync();
        rig.Write("last.txt", "x");

        await rig.Store.EndSessionAsync(WorkspaceRig.Session, WorkspaceRig.Budget);

        await Assert.That(rig.Spooled().SelectMany(r => r.Changes).Select(c => c.Path)).IsEquivalentTo(["last.txt"]);
        await Assert.That(Directory.Exists(rig.SessionSpanDir)).IsFalse();
    }

    [Test]
    public async Task Pruning_deletes_session_directories_untouched_for_a_week_only() {
        using var rig = new WorkspaceRig();
        await rig.OpenAsync();
        var fresh = rig.SessionSpanDir;
        var stale = Path.Combine(rig.Store.DirectoryPath, "fedcba9876543210fedcba9876543210");
        Directory.CreateDirectory(stale);
        var old = rig.Time.GetUtcNow().UtcDateTime - TimeSpan.FromDays(8);
        File.WriteAllText(Path.Combine(stale, "x.json"), "{}");
        File.SetLastWriteTimeUtc(Path.Combine(stale, "x.json"), old);
        Directory.SetLastWriteTimeUtc(stale, old);

        rig.Store.Prune();

        await Assert.That(Directory.Exists(stale)).IsFalse();
        await Assert.That(Directory.Exists(fresh)).IsTrue();
    }

    static string Toplevel(GitRepo repo) => Path.GetFullPath(repo.Do("rev-parse", "--show-toplevel").Text);
}
