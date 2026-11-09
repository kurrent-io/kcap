namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>What a repository with no remote is called: the same everywhere inside it, different
/// from every other repository, machine and person, and naming nothing about where it lives.</summary>
[ParallelLimiter<SubprocessLimit>]
public class LocalRepositoryTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir]        public required TempDir        Tmp    { get; init; }

    [Test]
    [Arguments("local~0123456789abcdef0123", true)]
    [Arguments("LOCAL~0123456789abcdef0123", true)]
    [Arguments("local", false)]
    [Arguments("local-org", false)]
    [Arguments("~jdoe", false)]
    [Arguments("acme", false)]
    [Arguments(null, false)]
    public async Task Only_the_reserved_prefix_marks_a_local_owner(string? owner, bool expected) =>
        await Assert.That(LocalRepository.IsLocal(owner)).IsEqualTo(expected);

    [Test]
    public async Task A_label_names_the_folder_for_local_and_the_slug_otherwise() {
        await Assert.That(LocalRepository.Label("local~0123", "demo")).IsEqualTo("demo (local)");
        await Assert.That(LocalRepository.Label("acme", "widgets")).IsEqualTo("acme/widgets");
    }

    [Test]
    public async Task The_identity_is_the_folder_under_an_opaque_owner_that_fits_the_wire_limit() {
        using var repo = GitRepo.Create();

        var identity = LocalRepository.Of(Config.Root, repo.Path);

        await Assert.That(identity).IsNotNull();
        await Assert.That(identity!.Value.Name).IsEqualTo(Path.GetFileName(repo.Path));
        await Assert.That(LocalRepository.IsLocal(identity.Value.Owner)).IsTrue();
        await Assert.That(identity.Value.Owner.Length).IsLessThanOrEqualTo(39);
        await Assert.That(identity.Value.Owner).DoesNotContain(Path.GetFileName(repo.Path));
    }

    // A subfolder and a linked worktree are the same repository, or one repo splits by where the agent stood.
    [Test]
    public async Task Every_place_inside_a_repository_shares_one_identity() {
        using var repo = GitRepo.CreateWithCommit();
        var sub      = repo.CreateDir("src", "deep").Path;
        var worktree = repo.AddWorktree("linked", "side");

        var atRoot = LocalRepository.Of(Config.Root, repo.Path);

        await Assert.That(atRoot).IsNotNull();
        await Assert.That(LocalRepository.Of(Config.Root, sub)).IsEqualTo(atRoot);
        await Assert.That(LocalRepository.Of(Config.Root, worktree.Path)).IsEqualTo(atRoot);
    }

    // The collision a shared local/<folder> owner would have: unrelated repos merging across a tenant.
    [Test]
    public async Task Two_repositories_with_one_folder_name_stay_apart() {
        using var a = GitRepo.InitIn(Tmp.CreateDir("a", "same-name"));
        using var b = GitRepo.InitIn(Tmp.CreateDir("b", "same-name"));

        var first  = LocalRepository.Of(Config.Root, a.Path);
        var second = LocalRepository.Of(Config.Root, b.Path);

        await Assert.That(first!.Value.Name).IsEqualTo("same-name");
        await Assert.That(second!.Value.Name).IsEqualTo("same-name");
        await Assert.That(first.Value.Owner).IsNotEqualTo(second.Value.Owner);
    }

    // The same path for two people or machines: /workspace/app in a container, /home/dev/app.
    [Test]
    public async Task One_path_under_two_machine_identities_stays_apart() {
        using var repo  = GitRepo.Create();
        using var other = new TempConfigRoot("other-machine");

        await Assert.That(LocalRepository.Of(other.Root, repo.Path)!.Value.Owner)
            .IsNotEqualTo(LocalRepository.Of(Config.Root, repo.Path)!.Value.Owner);
    }

    [Test]
    public async Task The_identity_is_stable_across_calls() {
        using var repo = GitRepo.Create();

        await Assert.That(LocalRepository.Of(Config.Root, repo.Path)).IsEqualTo(LocalRepository.Of(Config.Root, repo.Path));
    }

    [Test]
    public async Task A_bare_repository_is_named_without_its_suffix() {
        using var repo = GitRepo.CreateWithCommit();
        var bare = Tmp.PathTo("shared.git");
        repo.Do("clone", "--bare", repo.Path, bare);

        await Assert.That(LocalRepository.NameOf(bare, Tmp.PathTo("linked-worktree"))).IsEqualTo("shared");
    }

    [Test]
    public async Task A_folder_outside_any_repository_has_none() =>
        await Assert.That(LocalRepository.Of(Config.Root, Tmp.CreateDir("plain").Path)).IsNull();

    [Test]
    public async Task An_unusable_config_directory_means_no_identity_rather_than_an_exception() {
        using var repo = GitRepo.Create();
        var blocked = Tmp.CreateFile("not-a-directory");

        await Assert.That(LocalRepository.Of(new ConfigRoot(blocked), repo.Path)).IsNull();
    }
}
