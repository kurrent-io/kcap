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
        // A second close would stamp a later observation time and rewrite the file.
        rig.Time.Advance(TimeSpan.FromMinutes(5));

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
        var sessionSpool = Path.Join(rig.Spool.DirectoryPath, WorkspaceRig.Session);
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
    public async Task A_span_that_could_not_be_spooled_survives_the_session_end_for_a_later_retry() {
        using var rig = new WorkspaceRig();
        await rig.OpenAsync();
        rig.Write("unsent.txt", "x");
        // A file where the session's spool directory belongs makes every spool write fail.
        Directory.CreateDirectory(rig.Spool.DirectoryPath);
        var blocker = Path.Join(rig.Spool.DirectoryPath, WorkspaceRig.Session);
        File.WriteAllText(blocker, "");

        await rig.Store.EndSessionAsync(WorkspaceRig.Session, WorkspaceRig.Budget);

        await Assert.That(rig.Baselines()).HasSingleItem();

        File.Delete(blocker);
        await rig.CloseAsync();
        await Assert.That(rig.Spooled().SelectMany(r => r.Changes).Select(c => c.Path)).IsEquivalentTo(["unsent.txt"]);
    }

    /// <summary>A baseline the end could not close survives into the next run. That run starts its own, so the earlier
    /// run's later close, bounded to the spans opened by its end, cannot take the new run's changes; a turn of the
    /// same run still carries the survivor on.</summary>
    [Test]
    [Arguments(WorkspaceOpen.Run, false)]
    [Arguments(WorkspaceOpen.Turn, true)]
    public async Task A_baseline_that_survives_into_a_new_run_is_replaced(WorkspaceOpen mode, bool carriedOn) {
        using var rig = new WorkspaceRig();
        await rig.OpenAsync();
        rig.Write("earlier.txt", "x");
        var endedAt = rig.Time.GetUtcNow();
        rig.Time.Advance(TimeSpan.FromMinutes(1));

        Directory.CreateDirectory(rig.Spool.DirectoryPath);
        var blocker = Path.Join(rig.Spool.DirectoryPath, WorkspaceRig.Session);
        File.WriteAllText(blocker, "");
        await rig.OpenAsync(mode: mode);
        File.Delete(blocker);

        rig.Write("later.txt", "x");
        await rig.Store.EndSessionAsync(WorkspaceRig.Session, WorkspaceRig.Budget, new WorkspaceRunCutoff(null, endedAt));

        await Assert.That(rig.Spooled().SelectMany(r => r.Changes).Any(c => c.Path == "later.txt")).IsEqualTo(carriedOn);
    }

    /// <summary>An end that knows its run closes that run's spans only: not a later run's, and not one whose run was
    /// unreadable, which the next run's start replaces. A span from before spans named a run falls back to the end's time.</summary>
    [Test]
    [Arguments("run-a", true)]
    [Arguments("run-b", false)]
    [Arguments("unknown", false)]
    [Arguments("legacy", true)]
    public async Task An_end_bounded_to_its_run_closes_that_runs_spans_only(string spanRun, bool closed) {
        using var rig = new WorkspaceRig();
        var run = spanRun switch { "legacy" => null, "unknown" => new WorkspaceRun(null), _ => new WorkspaceRun(spanRun) };
        await rig.Store.OpenAsync(WorkspaceRig.Session, rig.Repo.Path, WorkspaceOpen.Run, WorkspaceRig.Budget, run);
        rig.Write("made.txt", "x");

        await rig.Store.EndSessionAsync(WorkspaceRig.Session, WorkspaceRig.Budget, new WorkspaceRunCutoff("run-a", rig.Time.GetUtcNow()));

        await Assert.That(rig.Spooled().SelectMany(r => r.Changes).Any(c => c.Path == "made.txt")).IsEqualTo(closed);
    }

    /// <summary>A session directory that cannot be listed may still hold the run's spans, so the close owed for them stays.</summary>
    [Test]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task A_session_dir_that_cannot_be_listed_still_holds_the_run() {
        Skip.When(OperatingSystem.IsWindows(), "a mode bit is what makes the directory unlistable");
        using var rig = new WorkspaceRig();
        await rig.Store.OpenAsync(WorkspaceRig.Session, rig.Repo.Path, WorkspaceOpen.Run, WorkspaceRig.Budget, new WorkspaceRun("run-a"));
        var run = new WorkspaceRunCutoff("run-a", rig.Time.GetUtcNow());
        await Assert.That(rig.Store.Holds(WorkspaceRig.Session, run)).IsTrue();

        var mode = File.GetUnixFileMode(rig.SessionSpanDir);
        File.SetUnixFileMode(rig.SessionSpanDir, UnixFileMode.None);

        try {
            await Assert.That(rig.Store.Holds(WorkspaceRig.Session, run)).IsTrue();
        } finally {
            File.SetUnixFileMode(rig.SessionSpanDir, mode);
        }
    }

    /// <summary>A Stop whose snapshot is slow must not hold the lock an <c>EnterWorktree</c> open of the same
    /// session needs: the lock covers span and spool files only. The tree's fsmonitor hook makes git status
    /// take two seconds, longer than a waiting hook gives the lock.</summary>
    [Test]
    public async Task A_slow_snapshot_never_holds_the_session_lock() {
        Skip.When(OperatingSystem.IsWindows(), "the slow fsmonitor hook is a shell script");
        using var rig = new WorkspaceRig();
        await rig.OpenAsync();
        rig.Write("slow.txt", "x");
        using var tools = new TempDir("ws-fsmonitor");
        var hook = tools.CreateExecutable("slow-fsmonitor.sh", "#!/bin/sh\nsleep 2\nexit 1\n");
        rig.Repo.Config("core.fsmonitor", hook);

        var close    = rig.CloseAsync();
        var acquired = false;
        for (var i = 0; i < 30 && !close.IsCompleted && !acquired; i++) {
            await Task.Delay(50);
            using var free = WorkspaceSpanLock.TryAcquire(rig.SessionSpanDir, TimeSpan.Zero);
            acquired = free is not null && !close.IsCompleted;
        }
        await close;

        await Assert.That(acquired).IsTrue();
        await Assert.That(rig.Spooled().SelectMany(r => r.Changes).Select(c => c.Path)).IsEquivalentTo(["slow.txt"]);
    }

    /// <summary>An opener holding the lock is about to commit its baseline, so a session end that finds no
    /// baseline must not remove the directory, or the lock file the opener holds, under it.</summary>
    [Test]
    public async Task A_session_end_never_removes_the_directory_while_an_opener_holds_the_lock() {
        using var rig = new WorkspaceRig();
        var store    = new WorkspaceSpanStore(rig.Config.Root, WorkspaceRig.Server, rig.Spool,
            new WorkspaceSnapshotter(new WorkspaceGit(rig.Time), rig.Time), rig.Time) { LockWait = TimeSpan.FromMilliseconds(50) };
        var lockFile = Path.Combine(rig.SessionSpanDir, ".lock");

        using (WorkspaceSpanLock.TryAcquire(rig.SessionSpanDir, TimeSpan.Zero)) {
            await store.EndSessionAsync(WorkspaceRig.Session, WorkspaceRig.Budget);

            await Assert.That(File.Exists(lockFile)).IsTrue();
        }
    }

    /// <summary>A lock file a cleanup retired may still be opened by a hook that was waiting on it; that hook
    /// must take a fresh lock rather than the retired one another process can no longer see.</summary>
    [Test]
    public async Task A_retired_lock_file_is_replaced_not_reused() {
        using var tmp = new TempDir("ws-lock");
        var dir = tmp.CreateDir("session");
        File.WriteAllText(Path.Combine(dir, ".lock"), "retired");

        using var held = WorkspaceSpanLock.TryAcquire(dir, TimeSpan.FromSeconds(1));

        await Assert.That(held).IsNotNull();
        await Assert.That(new FileInfo(Path.Combine(dir, ".lock")).Length).IsEqualTo(0);
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

    /// <summary>The prune runs on every hook, so a stale directory whose lock another hook holds is skipped
    /// at once and left for a later prune; waiting on it would spend the hook's own time.</summary>
    [Test]
    public async Task Pruning_skips_a_busy_stale_directory_without_waiting_on_it() {
        using var rig = new WorkspaceRig();
        var store     = new WorkspaceSpanStore(rig.Config.Root, WorkspaceRig.Server, rig.Spool,
            new WorkspaceSnapshotter(new WorkspaceGit(rig.Time), rig.Time), rig.Time) { LockWait = TimeSpan.FromSeconds(5) };
        var old       = rig.Time.GetUtcNow().UtcDateTime - TimeSpan.FromDays(8);
        var baseline  = Path.Combine(rig.SessionSpanDir, "0123456789abcdef.json");

        using (WorkspaceSpanLock.TryAcquire(rig.SessionSpanDir, TimeSpan.Zero)) {
            File.WriteAllText(baseline, "{}");
            foreach (var file in Directory.EnumerateFiles(rig.SessionSpanDir)) File.SetLastWriteTimeUtc(file, old);
            Directory.SetLastWriteTimeUtc(rig.SessionSpanDir, old);

            var started = TimeProvider.System.GetTimestamp();
            store.Prune();

            await Assert.That(TimeProvider.System.GetElapsedTime(started)).IsLessThan(TimeSpan.FromSeconds(1));
        }

        await Assert.That(File.Exists(baseline)).IsTrue();
    }

    static string Toplevel(GitRepo repo) => Path.GetFullPath(repo.Do("rev-parse", "--show-toplevel").Text);
}
