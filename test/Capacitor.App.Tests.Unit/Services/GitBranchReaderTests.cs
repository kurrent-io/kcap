using Capacitor.App.Services;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit.Services;

/// Branch names for the launcher's "Branch from" picker, from a checkout's refs or a remote's
/// ls-remote, without ever waiting on a prompt.
public class GitBranchReaderTests {
    sealed class Runner : IProcessRunner {
        public int Calls;
        public string[]? Args;
        public RunOptions? Options;
        public ProcessResult Result = new(0, "", "", false);

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Calls++;
            Args = args;
            Options = options;
            return Task.FromResult(Result);
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) => throw new NotSupportedException();
    }

    [Test]
    public async Task Local_refs_merge_heads_with_origin_and_name_its_head_the_default() {
        var listing = GitBranchReader.ParseRefs(
            "refs/heads/main\t\nrefs/heads/mine\t\nrefs/remotes/origin/HEAD\trefs/remotes/origin/main\n"
            + "refs/remotes/origin/main\t\nrefs/remotes/origin/Release\t\n");

        await Assert.That(listing.DefaultBranch).IsEqualTo("main");
        await Assert.That(listing.Branches).IsEquivalentTo(
            new[] { "main", "mine", "Release" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Ls_remote_names_the_default_from_heads_symref() {
        var listing = GitBranchReader.ParseLsRemote(
            "ref: refs/heads/trunk\tHEAD\nabc\tHEAD\nabc\trefs/heads/trunk\ndef\trefs/heads/feature/x\n");

        await Assert.That(listing.DefaultBranch).IsEqualTo("trunk");
        await Assert.That(listing.Branches).IsEquivalentTo(
            new[] { "feature/x", "trunk" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_remote_lookup_runs_with_prompts_off_and_is_reused_while_fresh() {
        var runner = new Runner { Result = new(0, "ref: refs/heads/main\tHEAD\nabc\trefs/heads/main\n", "", false) };
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var reader = new GitBranchReader(runner, time);

        var first = await reader.ListRemoteAsync("https://github.com/o/r", CancellationToken.None);
        await reader.ListRemoteAsync("https://github.com/o/r", CancellationToken.None);

        await Assert.That(first!.DefaultBranch).IsEqualTo("main");
        await Assert.That(runner.Calls).IsEqualTo(1);
        await Assert.That(runner.Options!.EnvOverlay!["GIT_TERMINAL_PROMPT"]).IsEqualTo("0");
        await Assert.That(runner.Args!).Contains("https://github.com/o/r");

        time.Advance(TimeSpan.FromMinutes(3));
        await reader.ListRemoteAsync("https://github.com/o/r", CancellationToken.None);
        await Assert.That(runner.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task A_failed_lookup_answers_null_and_is_asked_again() {
        var runner = new Runner { Result = new(128, "", "fatal: could not read Username", false) };
        var reader = new GitBranchReader(runner, TimeProvider.System);

        await Assert.That(await reader.ListRemoteAsync("https://github.com/o/r", CancellationToken.None)).IsNull();
        await Assert.That(await reader.ListRemoteAsync("https://github.com/o/r", CancellationToken.None)).IsNull();
        await Assert.That(runner.Calls).IsEqualTo(2);
    }

    /// git would read a leading dash as an option.
    [Test]
    public async Task A_url_that_reads_as_an_option_is_never_run() {
        var runner = new Runner();
        var reader = new GitBranchReader(runner, TimeProvider.System);

        await Assert.That(await reader.ListRemoteAsync("--upload-pack=touch x", CancellationToken.None)).IsNull();
        await Assert.That(runner.Calls).IsEqualTo(0);
    }

    [Test]
    [ParallelLimiter<SubprocessLimit>]
    public async Task A_real_checkout_lists_its_branches() {
        using var tmp = new TempDir();
        var repo = tmp.CreateDir("repo");
        var runner = new ProcessRunner(TimeProvider.System);
        foreach (var args in new[] {
            new[] { "init", "-q", "-b", "main", repo },
            ["-C", repo, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", "first"],
            ["-C", repo, "branch", "feature/a"],
        }) {
            var result = await runner.RunAsync("git", args, new RunOptions(), CancellationToken.None);
            await Assert.That(result.ExitCode).IsEqualTo(0);
        }

        var listing = await new GitBranchReader(runner, TimeProvider.System).ListLocalAsync(repo, CancellationToken.None);

        await Assert.That(listing!.Branches).IsEquivalentTo(
            new[] { "feature/a", "main" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(listing.DefaultBranch).IsNull();
    }
}
