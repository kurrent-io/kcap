using Capacitor.Cli.Core.Workspace;

namespace Capacitor.Cli.Core.Tests.Unit.Workspace;

/// <summary>What one span over a real repository reports, for each way a turn can change the tree.</summary>
[ParallelLimiter<SubprocessLimit>]
public class WorkspaceChangeDetectionTests {
    [Test]
    public async Task Create_edit_and_delete_are_each_reported_by_type() {
        using var rig = new WorkspaceRig();
        rig.Write("edited.txt", "one");
        rig.Write("deleted.txt", "gone soon");
        rig.Repo.CommitAll("seed");

        var changes = await rig.ChangesAsync(() => {
            rig.Write("created.txt", "new");
            rig.Write("edited.txt", "two");
            rig.Delete("deleted.txt");
        });

        await Assert.That(changes).IsEquivalentTo(new Dictionary<string, string> {
            ["created.txt"] = "create", ["edited.txt"] = "edit", ["deleted.txt"] = "delete",
        });
    }

    [Test]
    public async Task A_staged_rename_is_an_edit_of_the_new_path_and_a_delete_of_the_old() {
        using var rig = new WorkspaceRig();
        rig.Write("old.txt", "content");
        rig.Repo.CommitAll("seed");

        var changes = await rig.ChangesAsync(() => rig.Repo.Do("mv", "old.txt", "new.txt"));

        await Assert.That(changes).IsEquivalentTo(new Dictionary<string, string> { ["new.txt"] = "edit", ["old.txt"] = "delete" });
    }

    [Test]
    public async Task An_unstaged_rename_is_a_create_and_a_delete() {
        using var rig = new WorkspaceRig();
        rig.Write("old.txt", "content");
        rig.Repo.CommitAll("seed");

        var changes = await rig.ChangesAsync(() => File.Move(rig.Repo.PathTo("old.txt"), rig.Repo.PathTo("new.txt")));

        await Assert.That(changes).IsEquivalentTo(new Dictionary<string, string> { ["new.txt"] = "create", ["old.txt"] = "delete" });
    }

    [Test]
    public async Task A_file_edited_and_committed_in_the_turn_is_reported() {
        using var rig = new WorkspaceRig();

        var changes = await rig.ChangesAsync(() => {
            rig.Write("README.md", "changed");
            rig.Repo.CommitAll("edit");
        });

        await Assert.That(changes).IsEquivalentTo(new Dictionary<string, string> { ["README.md"] = "edit" });
    }

    [Test]
    public async Task A_commit_reset_away_in_the_same_turn_is_still_reported() {
        using var rig = new WorkspaceRig();

        var changes = await rig.ChangesAsync(() => {
            rig.Write("README.md", "changed");
            rig.Repo.CommitAll("edit");
            rig.Repo.Do("reset", "-q", "--hard", "HEAD~1");
        });

        await Assert.That(changes).IsEquivalentTo(new Dictionary<string, string> { ["README.md"] = "edit" });
    }

    [Test]
    public async Task An_amended_commit_reports_its_files() {
        using var rig = new WorkspaceRig();
        rig.Write("first.txt", "one");
        rig.Repo.CommitAll("seed");

        var changes = await rig.ChangesAsync(() => {
            rig.Write("second.txt", "two");
            rig.Repo.Add();
            rig.Repo.Do("commit", "-q", "--amend", "--no-edit");
        });

        await Assert.That(changes).IsEquivalentTo(new Dictionary<string, string> { ["first.txt"] = "create", ["second.txt"] = "create" });
    }

    [Test]
    public async Task A_repositorys_first_commit_lists_its_files() {
        using var rig = new WorkspaceRig();
        using var empty = GitRepo.Create("ws-unborn");

        await Assert.That(await rig.Store.OpenAsync(WorkspaceRig.Session, empty.Path, WorkspaceOpen.Turn, WorkspaceRig.Budget)).IsTrue();
        empty.CreateFile("a.txt", "a");
        empty.CommitAll("initial");
        await rig.CloseAsync();

        var changes = rig.Spooled().SelectMany(r => r.Changes).ToList();
        await Assert.That(changes).IsEquivalentTo([new WorkspaceChange("a.txt", "create")]);
    }

    [Test]
    public async Task A_stash_made_in_the_turn_reports_what_it_took() {
        using var rig = new WorkspaceRig();

        var changes = await rig.ChangesAsync(() => {
            rig.Write("README.md", "stashed edit");
            rig.Repo.Do("stash", "-q");
        });

        await Assert.That(changes).IsEquivalentTo(new Dictionary<string, string> { ["README.md"] = "edit" });
    }

    [Test]
    public async Task A_rebase_is_not_attributed() {
        using var rig = new WorkspaceRig();
        rig.Repo.Checkout("other", create: true);
        rig.Write("theirs.txt", "someone else's");
        rig.Repo.CommitAll("theirs");
        rig.Repo.Checkout("main");
        rig.Write("mine.txt", "earlier work");
        rig.Repo.CommitAll("mine");

        var changes = await rig.ChangesAsync(() => rig.Repo.Do("rebase", "-q", "other"));

        await Assert.That(changes).IsEmpty();
    }

    [Test]
    public async Task Gitignored_paths_are_never_reported() {
        using var rig = new WorkspaceRig();
        rig.Write(".gitignore", "build/\n");
        rig.Repo.CommitAll("ignore");

        var changes = await rig.ChangesAsync(() => {
            rig.Write("build/out.bin", "artifact");
            rig.Write("kept.txt", "real");
        });

        await Assert.That(changes).IsEquivalentTo(new Dictionary<string, string> { ["kept.txt"] = "create" });
    }

    [Test]
    public async Task A_second_edit_to_an_already_dirty_file_is_reported_even_with_its_status_unchanged() {
        using var rig = new WorkspaceRig();
        rig.Write("notes.txt", "one");
        rig.Repo.CommitAll("seed");
        rig.Write("notes.txt", "two");

        var changes = await rig.ChangesAsync(() => rig.Write("notes.txt", "three"));

        await Assert.That(changes).IsEquivalentTo(new Dictionary<string, string> { ["notes.txt"] = "edit" });
    }

    [Test]
    public async Task A_second_edit_inside_an_already_dirty_submodule_names_only_the_submodule() {
        using var rig = new WorkspaceRig();
        using var inner = GitRepo.CreateWithCommit("ws-sub");
        rig.Repo.Do("-c", "protocol.file.allow=always", "submodule", "add", "-q", inner.Path, "sub");
        rig.Repo.CommitAll("add submodule");
        var sub = GitRepo.At(rig.Repo.PathTo("sub"));
        sub.CreateFile("README.md", "dirty once");
        var statusBefore = rig.Repo.Status;

        var changes = await rig.ChangesAsync(() => sub.CreateFile("README.md", "dirty twice"));

        await Assert.That(rig.Repo.Status).IsEqualTo(statusBefore);
        await Assert.That(changes).IsEquivalentTo(new Dictionary<string, string> { ["sub"] = "edit" });
    }

    [Test]
    public async Task Hitting_the_history_cap_marks_the_span_truncated() {
        using var rig = new WorkspaceRig(WorkspaceBounds.Default with { HistoryEntries = 2 });

        var spooled = await rig.SpanAsync(() => {
            for (var i = 0; i < 3; i++) {
                rig.Write($"c{i}.txt", "x");
                rig.Repo.CommitAll($"c{i}");
            }
        });

        await Assert.That(spooled).HasSingleItem();
        await Assert.That(spooled[0].Truncated).IsTrue();
    }

    [Test]
    public async Task Edits_after_the_turn_closed_are_not_recorded() {
        using var rig = new WorkspaceRig();

        await rig.SpanAsync(() => rig.Write("in-turn.txt", "x"));
        // A background subagent that outlives its parent's turn writes here, with no span open.
        rig.Write("after-stop.txt", "x");
        await rig.CloseAsync();
        await rig.SpanAsync(() => { });

        await Assert.That(rig.Spooled().SelectMany(r => r.Changes).Select(c => c.Path)).IsEquivalentTo(["in-turn.txt"]);
    }
}
