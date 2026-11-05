using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>A git repository kcap has no remote to name stores a repository and branch all the same; a
/// folder outside git still stores neither, and a repository with a remote keeps its identity.</summary>
[ParallelLimiter<SubprocessLimit>]
public class RepositoryDetectionLocalTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir]        public required TempDir        Tmp    { get; init; }

    Task<RepositoryPayload?> Detect(string cwd, TimeProvider? time = null) =>
        RepositoryDetection.DetectRepositoryAsync(new GitProviderRouter(), Config.Root, cwd, time ?? TimeProvider.System);

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
}
