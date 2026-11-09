namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="GitRepoKey"/>. The key is what a repo allow or deny list compares
/// against, so both halves have to hold: a real checkout must actually RESOLVE — a key generator
/// that only ever returned null would refuse every repository under an allowlist while looking
/// correct from the fail-closed side, and would drop every repository out of every deny list —
/// and a checkout with nothing to resolve must come back null rather than guess.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class GitRepoKeyTests {
    [Test]
    public async Task An_ssh_origin_resolves_to_owner_and_repo() {
        using var repo = GitRepo.CreateWithCommit();
        repo.AddRemote("git@github.com:acme/widgets.git");

        await Assert.That(GitRepoKey.ForCheckout(repo.Path)).IsEqualTo("acme/widgets");
    }

    [Test]
    public async Task An_https_origin_resolves_to_the_same_key() {
        using var repo = GitRepo.CreateWithCommit();
        repo.AddRemote("https://github.com/acme/widgets.git");

        await Assert.That(GitRepoKey.ForCheckout(repo.Path)).IsEqualTo("acme/widgets");
    }

    // A pasted URL keeps its trailing slash, and `acme/widgets/` matches no entry anyone would
    // write — so the repo would fall out of its own deny list.
    [Test]
    public async Task A_remote_written_with_a_trailing_slash_resolves_to_the_plain_key() {
        using var repo = GitRepo.CreateWithCommit();
        repo.AddRemote("https://github.com/acme/widgets/");

        await Assert.That(GitRepoKey.ForCheckout(repo.Path)).IsEqualTo("acme/widgets");
    }

    // The mode-independence a repo list rests on: a reviewer borrowing a linked worktree and one
    // running in the primary checkout must produce ONE key, or the same repository would be
    // admitted under one containment and refused under another.
    [Test]
    public async Task A_linked_worktree_resolves_to_the_repository_behind_it() {
        using var repo = GitRepo.CreateWithCommit();
        repo.AddRemote("git@github.com:acme/widgets.git");

        var worktree = repo.AddWorktree("linked", "side");

        await Assert.That(GitRepoKey.ForCheckout(worktree.Path)).IsEqualTo("acme/widgets");
    }

    // A submodule keeps its .git as a FILE pointing into the superproject's .git/modules, so the
    // config naming it is not where a checkout root normally keeps one. Resolving it to the
    // superproject — or to nothing — would let work in a submodule past that submodule's own
    // exclusion. kcap-cli is itself a submodule of kcap-server, so this is the ordinary case here.
    [Test]
    public async Task A_submodule_resolves_to_its_own_repository_not_the_superproject() {
        using var super = GitRepo.CreateWithCommit();
        using var sub   = GitRepo.CreateWithCommit();

        super.AddRemote("git@github.com:acme/platform.git");

        // Added from a local path because a fake URL cannot be cloned; the remote it should be
        // judged by is then written where a real submodule keeps it.
        super.Do("-c", "protocol.file.allow=always", "submodule", "add", "-q", sub.Path, "vendor");
        GitRepo.At(super.PathTo("vendor")).Do("remote", "set-url", "origin", "git@github.com:acme/widgets.git");

        await Assert.That(GitRepoKey.ForCheckout(super.PathTo("vendor"))).IsEqualTo("acme/widgets");
        await Assert.That(GitRepoKey.ForCheckout(super.Path)).IsEqualTo("acme/platform");
    }

    [Test]
    public async Task A_subdirectory_resolves_to_its_repositorys_key() {
        using var repo = GitRepo.CreateWithCommit();
        repo.AddRemote("git@github.com:acme/widgets.git");

        var nested = repo.CreateDir("src", "deep");

        await Assert.That(GitRepoKey.ForCheckout(nested.Path)).IsEqualTo("acme/widgets");
    }

    [Test]
    public async Task A_repository_with_no_origin_remote_has_no_key() {
        using var repo = GitRepo.CreateWithCommit();

        await Assert.That(GitRepoKey.ForCheckout(repo.Path)).IsNull();
    }

    [Test]
    public async Task A_remote_that_is_not_a_recognizable_git_url_has_no_key() {
        using var repo = GitRepo.CreateWithCommit();
        repo.AddRemote("not-a-url");

        await Assert.That(GitRepoKey.ForCheckout(repo.Path)).IsNull();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task A_path_that_names_nothing_has_no_key(string? path) {
        await Assert.That(GitRepoKey.ForCheckout(path)).IsNull();
    }

    // A daemon deciding a launch and a hook deciding the session it starts must read one key.
    [Test]
    public async Task For_a_server_that_stores_local_repositories_one_with_no_remote_keys_by_its_local_identity() {
        using var config = new TempConfigRoot("key");
        using var repo   = GitRepo.Create();
        var local  = LocalRepository.Of(config.Root, repo.Path)!.Value;
        var server = Supporting(config);

        await Assert.That(GitRepoKey.ForCheckout(repo.Path, config.Root, server)).IsEqualTo($"{local.Owner}/{local.Name}");
    }

    // The hooks send that server no owner for it, so a list cannot place it either.
    [Test]
    public async Task For_a_server_not_known_to_store_them_a_repository_with_no_remote_stays_unplaced() {
        using var config = new TempConfigRoot("key");
        using var repo   = GitRepo.Create();

        await Assert.That(GitRepoKey.ForCheckout(repo.Path, config.Root, "https://never-seen.example")).IsNull();
    }

    [Test]
    public async Task For_any_server_a_remote_still_names_the_repository() {
        using var config = new TempConfigRoot("key");
        using var repo   = GitRepo.Create();
        repo.AddRemote("git@github.com:acme/widgets.git");

        await Assert.That(GitRepoKey.ForCheckout(repo.Path, config.Root, Supporting(config))).IsEqualTo("acme/widgets");
        await Assert.That(GitRepoKey.ForCheckout(repo.Path, config.Root, "https://never-seen.example")).IsEqualTo("acme/widgets");
    }

    [Test]
    public async Task For_any_server_a_folder_outside_any_repository_stays_unplaced() {
        using var config = new TempConfigRoot("key");

        await Assert.That(GitRepoKey.ForCheckout(config.CreateDir("plain").Path, config.Root, Supporting(config))).IsNull();
    }

    static string Supporting(TempConfigRoot config) {
        const string server = "https://kcap.example";
        ServerVersionStore.Set(server, LocalRepositorySupport.MinimumServerVersion, config.Root, TimeProvider.System);

        return server;
    }
}
