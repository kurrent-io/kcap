using Capacitor.Cli.Recording.PrDetection;

namespace Capacitor.Cli.Recording.Tests.Unit;

/// <summary>
/// The Claude hook path passes <c>detectPullRequest: false</c> so a live
/// <c>gh pr view</c> / <c>glab</c> round-trip (~600 ms to GitHub) never blocks
/// the 5 s SessionStart deadline. The watcher owns PR detection and backfills
/// it independently, so the hook only needs base repo info (owner/repo/branch).
/// </summary>
public class RepositoryDetectionPrSkipTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static CommandRunner RecordingRunner(List<string> commands) =>
        (cmd, args, _, _) => {
            commands.Add(cmd);
            string? reply = (cmd, args) switch {
                ("git", "branch --show-current") => "main",
                ("git", "config user.name")      => "Tester",
                ("git", "config user.email")     => "t@example.com",
                ("git", "remote get-url origin") => "git@github.com:acme/widget.git",
                _                                => null
            };
            return Task.FromResult(reply);
        };

    /// <summary>A process started before git was installed has no git on its PATH, and git refuses a
    /// repository another account owns. The checkout's own files still name the repository.</summary>
    [Test]
    public async Task Git_that_will_not_answer_leaves_the_checkout_to_name_the_repository() {
        using var repo = GitRepo.Create();
        repo.Do("remote", "add", "origin", "git@github.invalid:acme/widget.git");

        var detected = await RepositoryDetection.DetectRepositoryAsync(new GitProviderRouter(), Config.Root,
            repo.Path, TimeProvider.System, detectPullRequest: false, run: (_, _, _, _) => Task.FromResult<string?>(null));

        await Assert.That(detected!.Owner).IsEqualTo("acme");
        await Assert.That(detected.RepoName).IsEqualTo("widget");
        await Assert.That(detected.Branch).IsEqualTo("main");
    }

    [Test]
    public async Task Git_that_answers_only_the_branch_still_leaves_the_checkout_to_name_the_remote() {
        using var repo = GitRepo.Create();
        repo.Do("remote", "add", "origin", "git@github.invalid:acme/widget.git");

        var detected = await RepositoryDetection.DetectRepositoryAsync(new GitProviderRouter(), Config.Root,
            repo.Path, TimeProvider.System, detectPullRequest: false,
            run: (_, args, _, _) => Task.FromResult(args.Contains("--show-current") || args.Contains("rev-parse") ? "main" : null));

        await Assert.That(detected!.Owner).IsEqualTo("acme");
    }

    [Test]
    public async Task Git_that_will_not_answer_outside_a_checkout_finds_no_repository() {
        using var cwd = new TempDir();

        var detected = await RepositoryDetection.DetectRepositoryAsync(new GitProviderRouter(), Config.Root,
            cwd.Path, TimeProvider.System, detectPullRequest: false, run: (_, _, _, _) => Task.FromResult<string?>(null));

        await Assert.That(detected).IsNull();
    }

    [Test]
    public async Task DetectPullRequest_false_never_spawns_gh_or_glab() {
        using var cwd = new TempDir();
        var commands = new List<string>();

        var repo = await RepositoryDetection.DetectRepositoryAsync(new GitProviderRouter(), Config.Root, 
            cwd.Path, TimeProvider.System, budget: TimeSpan.FromSeconds(5), detectPullRequest: false, run: RecordingRunner(commands));

        await Assert.That(repo).IsNotNull();
        await Assert.That(repo!.Owner).IsEqualTo("acme");
        await Assert.That(repo.RepoName).IsEqualTo("widget");
        await Assert.That(repo.Branch).IsEqualTo("main");
        await Assert.That(repo.PrNumber).IsNull();

        // The gate is the whole point: no provider probe (gh auth status) and no
        // detector (gh/glab) was ever spawned — only git commands ran.
        await Assert.That(commands.All(c => c == "git")).IsTrue();
    }

    [Test]
    public async Task DetectPullRequest_true_does_probe_the_provider() {
        using var cwd = new TempDir();
        var commands = new List<string>();

        await RepositoryDetection.DetectRepositoryAsync(new GitProviderRouter(), Config.Root, 
            cwd.Path, TimeProvider.System, budget: TimeSpan.FromSeconds(5), detectPullRequest: true, run: RecordingRunner(commands));

        // Proves the flag actually gates the round-trip: with detection ON, the
        // GitHub provider probe (gh) is attempted.
        await Assert.That(commands.Any(c => c == "gh")).IsTrue();
    }

    [Test]
    public async Task EnrichWithRepositoryInfo_false_never_spawns_gh_or_glab() {
        using var cwd = new TempDir();
        var commands = new List<string>();
        var payload  = $$"""{"cwd":"{{cwd.Path.Replace("\\", "/")}}","hook_event_name":"session-start"}""";

        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(new GitProviderRouter(), Config.Root, 
            payload, TimeProvider.System, budget: TimeSpan.FromSeconds(5), detectPullRequest: false, run: RecordingRunner(commands));

        await Assert.That(enriched).Contains("acme");
        await Assert.That(commands.All(c => c == "git")).IsTrue();
    }
}
