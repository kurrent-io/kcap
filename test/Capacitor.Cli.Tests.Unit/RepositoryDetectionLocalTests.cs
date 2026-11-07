using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.PrDetection;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>A git repository kcap has no remote to name stores a repository and branch all the same, once the
/// server it reports to is known to store one; a folder outside git still stores neither, and a repository with a
/// remote keeps its identity.</summary>
[ParallelLimiter<SubprocessLimit>]
public class RepositoryDetectionLocalTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir]        public required TempDir        Tmp    { get; init; }

    const string Server = "https://kcap.example";

    [Before(Test)]
    public Task ReportToAServerThatStoresLocalRepositories() => ReportTo(LocalRepositorySupport.MinimumServerVersion);

    // A URL override in the environment is a server too, so it reports the same version: otherwise these tests
    // would pass or fail by whether the shell running them sets one.
    Task ReportTo(string? version) {
        foreach (var server in new[] { Server, ProfileOverrides.FromEnvironment().Url }.OfType<string>()) {
            ServerVersionStore.Forget(server, Config.Root);
            if (version is not null) ServerVersionStore.Set(server, version, Config.Root, TimeProvider.System);
        }

        return ConfigMutator.MutateAsync(Config.Root, c => c with { Profiles = new() { ["p"] = new Profile { ServerUrl = Server } } });
    }

    Task<RepositoryPayload?> Detect(string cwd, TimeProvider? time = null) =>
        RepositoryDetection.DetectRepositoryAsync(new GitProviderRouter(), Config.Root, cwd, time ?? TimeProvider.System);

    // An older server would keep the identity as an ordinary repository and ask GitHub about it, so it is sent
    // nothing new: no owner, as before.
    [Test]
    [Arguments("1.0.0")]
    [Arguments(null)]
    public async Task A_server_not_known_to_store_local_repositories_gets_no_owner(string? version) {
        await ReportTo(version);
        using var repo = GitRepo.CreateWithCommit();

        var found = await Detect(repo.Path);

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Owner).IsNull();
        await Assert.That(found.RepoName).IsNull();
        await Assert.That(found.Branch).IsNotNull();
    }

    [Test]
    public async Task A_repository_with_no_origin_has_a_local_identity_and_its_branch() {
        using var repo = GitRepo.CreateWithCommit();
        repo.Do("checkout", "-b", "feature/x");
        var expected = LocalRepository.Of(Config.Root, repo.Path)!.Value;

        var found = await Detect(repo.Path);

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Owner).IsEqualTo(expected.Owner);
        await Assert.That(found.RepoName).IsEqualTo(expected.Name);
        await Assert.That(found.Branch).IsEqualTo("feature/x");
        await Assert.That(found.RemoteUrl).IsNull();
        await Assert.That(found.Host).IsNull();
    }

    [Test]
    public async Task A_cached_local_identity_reads_the_same() {
        using var repo = GitRepo.Create();

        var first  = await Detect(repo.Path);
        var second = await Detect(repo.Path);

        await Assert.That(LocalRepository.IsLocal(second!.Owner)).IsTrue();
        await Assert.That(second.Owner).IsEqualTo(first!.Owner);
        await Assert.That(second.RepoName).IsEqualTo(first.RepoName);
    }

    // The servers can change while a detection is cached, and one that stores no local identity is sent none.
    [Test]
    public async Task A_cached_local_identity_is_not_sent_to_a_server_that_since_reports_an_older_version() {
        using var repo = GitRepo.Create();
        await Assert.That(LocalRepository.IsLocal((await Detect(repo.Path))!.Owner)).IsTrue();

        await ReportTo("1.0.0");
        var found = await Detect(repo.Path);

        await Assert.That(found!.Owner).IsNull();
        await Assert.That(found.RepoName).IsNull();
    }

    [Test]
    public async Task A_folder_outside_git_stays_repository_less() =>
        await Assert.That(await Detect(Tmp.CreateDir("plain").Path)).IsNull();

    [Test]
    public async Task A_remote_keeps_its_identity() {
        using var repo = GitRepo.Create();
        repo.AddRemote("git@github.com:acme/widgets.git");

        var found = await Detect(repo.Path);

        await Assert.That(found!.Owner).IsEqualTo("acme");
        await Assert.That(found.RepoName).IsEqualTo("widgets");
        await Assert.That(found.RemoteUrl).IsEqualTo("git@github.com:acme/widgets.git");
        await Assert.That(found.Host).IsEqualTo("github.com");
    }

    // An origin that is a path names no owner, and the path itself must not leave the machine.
    [Test]
    [Arguments("/srv/private/customer-x/demo.git")]
    [Arguments("../sibling/demo.git")]
    [Arguments("file:///srv/private/customer-x/demo.git")]
    public async Task An_origin_with_no_owner_is_local_and_never_sent(string origin) {
        using var repo = GitRepo.Create();
        repo.AddRemote(origin);

        var found = await Detect(repo.Path);

        await Assert.That(LocalRepository.IsLocal(found!.Owner)).IsTrue();
        await Assert.That(found.RemoteUrl).IsNull();
        await Assert.That(found.Host).IsNull();
    }

    // Identity is derived on every miss, so nothing links the two: earlier sessions stay where they were stored.
    [Test]
    public async Task Gaining_a_remote_moves_new_detections_to_the_remote_once_the_cache_expires() {
        using var repo = GitRepo.Create();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        var before = await Detect(repo.Path, clock);
        repo.AddRemote("git@github.com:acme/widgets.git");
        clock.Advance(TimeSpan.FromHours(2));
        var after = await Detect(repo.Path, clock);

        await Assert.That(LocalRepository.IsLocal(before!.Owner)).IsTrue();
        await Assert.That(after!.Owner).IsEqualTo("acme");
    }

    // An older CLI left owner=null in the cache for a repository with no remote, good for up to an hour.
    [Test]
    public async Task An_older_cache_entry_with_no_owner_does_not_hide_the_local_identity() {
        using var repo = GitRepo.Create();
        await Detect(repo.Path);

        foreach (var file in Directory.GetFiles(Config.Root.Path("cache"), "*.json")) {
            File.WriteAllText(file, $$"""{"schema_version":3,"owner":null,"repo_name":null,"cached_at":"{{DateTimeOffset.UtcNow:O}}"}""");
        }

        await Assert.That(LocalRepository.IsLocal((await Detect(repo.Path))!.Owner)).IsTrue();
    }

    [Test]
    public async Task A_folder_entry_gates_a_local_repository_the_way_a_slug_gates_a_remote() {
        using var repo = GitRepo.Create();
        var body  = JsonSerializer.Serialize(new Dictionary<string, string> { ["cwd"] = repo.Path });
        var entry = $"local~/{Path.GetFileName(repo.Path)}";

        await Assert.That(await RepoExclusion.IsOutOfScopeAsync(new GitProviderRouter(), Config.Root, body, [entry], null, TimeProvider.System)).IsFalse();
        await Assert.That(await RepoExclusion.IsOutOfScopeAsync(new GitProviderRouter(), Config.Root, body, null, [entry], TimeProvider.System)).IsTrue();
        await Assert.That(await RepoExclusion.IsOutOfScopeAsync(new GitProviderRouter(), Config.Root, body, ["acme/widgets"], null, TimeProvider.System)).IsTrue();
    }

    // Its session is still captured, only without a repository, so the folder entry admits nothing but still excludes.
    [Test]
    public async Task For_a_server_not_known_to_store_local_repositories_a_folder_entry_still_excludes() {
        await ReportTo("1.0.0");
        using var repo = GitRepo.Create();
        var body  = JsonSerializer.Serialize(new Dictionary<string, string> { ["cwd"] = repo.Path });
        var entry = $"local~/{Path.GetFileName(repo.Path)}";

        await Assert.That(await RepoExclusion.IsOutOfScopeAsync(new GitProviderRouter(), Config.Root, body, [entry], null, TimeProvider.System)).IsTrue();
        await Assert.That(await RepoExclusion.IsOutOfScopeAsync(new GitProviderRouter(), Config.Root, body, null, [entry], TimeProvider.System)).IsTrue();
    }

    // A hook names the repository by the same keys a launch does, so an entry naming the remote's path still excludes it.
    [Test]
    public async Task An_origin_with_no_owner_is_still_excluded_by_its_remote_path() {
        using var repo = GitRepo.Create();
        repo.AddRemote("ssh://git.example/demo.git");
        var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["cwd"] = repo.Path });

        await Assert.That(await RepoExclusion.IsOutOfScopeAsync(new GitProviderRouter(), Config.Root, body, null, ["demo"], TimeProvider.System)).IsTrue();
    }
}
