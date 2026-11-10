namespace Capacitor.Cli.Core.Tests.Unit;

public class GitPorcelainTests {
    [Test]
    public async Task A_rename_or_copy_record_keeps_both_paths() {
        var entries = GitPorcelain.ParseZ("R  new.txt\0old.txt\0C  copy.txt\0source.txt\0 M edited.txt\0?? fresh.txt\0").ToList();

        await Assert.That(entries).IsEquivalentTo([
            new PorcelainEntry("R ", "new.txt", "old.txt"),
            new PorcelainEntry("C ", "copy.txt", "source.txt"),
            new PorcelainEntry(" M", "edited.txt"),
            new PorcelainEntry("??", "fresh.txt"),
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>Git writes a worktree-side rename, a moved intent-to-add file, with its original path too.</summary>
    [Test]
    public async Task A_worktree_side_rename_keeps_its_original_path_out_of_the_next_record() {
        var entries = GitPorcelain.ParseZ(" R moved.txt\0added.txt\0 M edited.txt\0").ToList();

        await Assert.That(entries).IsEquivalentTo([
            new PorcelainEntry(" R", "moved.txt", "added.txt"),
            new PorcelainEntry(" M", "edited.txt"),
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_record_cut_off_by_a_truncated_read_is_dropped() {
        var entries = GitPorcelain.ParseZ(" M whole.txt\0R  new.txt\0ol").ToList();

        await Assert.That(entries).IsEquivalentTo([new PorcelainEntry(" M", "whole.txt")]);
    }
}
