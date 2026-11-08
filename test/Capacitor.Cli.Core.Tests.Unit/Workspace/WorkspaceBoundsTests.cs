using Capacitor.Cli.Core.Workspace;

namespace Capacitor.Cli.Core.Tests.Unit.Workspace;

/// <summary>Every bound a snapshot keeps, each marking the span truncated when passed. The span still
/// posts what it has.</summary>
[ParallelLimiter<SubprocessLimit>]
public class WorkspaceBoundsTests {
    public static IEnumerable<Func<(string Bound, WorkspaceBounds Bounds)>> PassedBounds() {
        yield return () => ("status entries", WorkspaceBounds.Default with { StatusEntries = 2 });
        yield return () => ("hashed paths", WorkspaceBounds.Default with { HashedPaths = 1 });
        yield return () => ("hashed bytes", WorkspaceBounds.Default with { HashedBytes = 10 });
        yield return () => ("file size", WorkspaceBounds.Default with { FileBytes = 10 });
    }

    [Test]
    [MethodDataSource(nameof(PassedBounds))]
    public async Task Passing_a_bound_marks_the_span_truncated_and_still_posts(string bound, WorkspaceBounds bounds) {
        using var rig = new WorkspaceRig(bounds);
        rig.Write("dirty-a.txt", "twenty bytes of text");
        rig.Write("dirty-b.txt", "twenty bytes of text");

        // Sorts first, so a cut status still lists it.
        var spooled = await rig.SpanAsync(() => rig.Write("0-new.txt", "twenty bytes of text"));

        await Assert.That(spooled).HasSingleItem();
        await Assert.That(spooled[0].Truncated).IsTrue();
        await Assert.That(spooled[0].Changes).IsNotEmpty();
    }

    [Test]
    public async Task A_file_over_the_size_bound_counts_as_changed_when_its_size_moved() {
        using var rig = new WorkspaceRig(WorkspaceBounds.Default with { FileBytes = 10 });
        rig.Write("big.txt", "twenty bytes of text");

        var spooled = await rig.SpanAsync(() => rig.Write("big.txt", "now forty bytes of text, a different size"));

        await Assert.That(spooled.SelectMany(r => r.Changes)).IsEquivalentTo([new WorkspaceChange("big.txt", "edit")]);
    }

    [Test]
    public async Task Running_out_of_the_snapshot_budget_after_status_marks_it_truncated() {
        using var repo = GitRepo.CreateWithCommit("ws-budget");
        repo.CreateFile("dirty.txt", "x");

        // The smallest budget that still reads status: everything after it runs out.
        WorkspaceSnapshot? snapshot = null;
        for (var free = 1; free < 40 && snapshot is null; free++) {
            var clock = new StallingClock(read => read > free);
            snapshot = await new WorkspaceSnapshotter(new WorkspaceGit(clock), clock).TakeAsync(repo.Path, TimeSpan.FromSeconds(1.5));
        }

        await Assert.That(snapshot).IsNotNull();
        await Assert.That(snapshot!.Truncated).IsTrue();
        await Assert.That(snapshot.Entries["dirty.txt"].Hash).IsNull();
    }

    [Test]
    public async Task A_HEAD_that_could_not_be_read_is_not_taken_for_an_unborn_branch() {
        using var repo = GitRepo.CreateWithCommit("ws-head");
        // The second read is the HEAD lookup's deadline; an unborn branch would read no reflog at all,
        // and every commit the reflog has would then look gained at the span's close.
        var clock = new StallingClock(read => read == 2);

        var snapshot = await new WorkspaceSnapshotter(new WorkspaceGit(clock), clock).TakeAsync(repo.Path, TimeSpan.FromSeconds(1.5));

        await Assert.That(snapshot!.HeadReflog).IsEqualTo(-1);
        await Assert.That(snapshot.Truncated).IsTrue();
    }
}
